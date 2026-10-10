# Deployed secrets: one service account per environment

## Status

Accepted (2026-09-25, found live during T8.3 prod regression proof — issue #23)

## Context

Originally, each deployed environment (prod, staging) read its Google Sheets credential and
agent API keys from one AWS Secrets Manager secret named `guito-api/<env>`
(payload shape `SecretsPayload { GoogleServiceAccount, ApiKeys }`). The
`GoogleServiceAccount` in the secret must be the service account **of that
environment's spreadsheet**:

- `guito-api/prod` must carry the **prod** SA (`svc-google-sheets@…`) — the only
  SA shared on the prod spreadsheet.
- `guito-api/staging` must carry the **dev/staging** SA (`svc-google-sheets-dev@…`)
  — the only SA shared on the dev/staging spreadsheet (ADR-0007 sheet separation).

This was not always explicit, and it bit: the prod secret was seeded with the dev
SA, so every prod Sheets read failed with `PERMISSION_DENIED` and the API returned
500 on all business requests — while auth and `/healthz` stayed green, which made
it look like a code problem. It was not: Google rejected the SA at the sheet.

## Decision

The per-environment payload is the source of truth for that environment's SA
identity, and it must match that environment's spreadsheet sharing. Provision
credentials separately before deploying. Deploy never creates or replaces keys:
a missing/unreadable parameter aborts the deployment.

### Mechanism amendment — issue #95

Application secrets move to **SSM Parameter Store SecureString**, standard tier,
using the default `aws/ssm` KMS key. The five existing application payloads map
byte-for-byte to `/guito-api/prod`, `/guito-api/staging`,
`/guito-api/human-auth`, `/guito-api/eb-staging-pk`, and `/guito-api/eb-prod-pk`.
The dedicated Enable Banking keys remain separate so runtime payload changes
cannot overwrite them. There is currently no `guito-api/dev` runtime secret;
local development remains file-backed, and `AwsSsm` is opt-in.

`AppConfiguration:Secrets:Location=AwsSsm` selects the SSM implementation behind
the existing interfaces (ADR 0011). Names include the leading slash. The legacy
`Aws` application backend was retained during rollout, then retired under #124;
the SSM implementation preserves the JSON payload contract.
Providers decrypt reads, propagate cancellation, and retain the five-minute cache.

**Database exception, approved by José:** `guito-api/db-admin`, `db-dev`,
`db-staging`, and `db-prod` stay in Secrets Manager. The [RDS Data API contract](https://docs.aws.amazon.com/AmazonRDS/latest/AuroraUserGuide/data-api.access.html)
requires a Secrets Manager credential secret ARN; SSM cannot replace it. The
existing Postgres references remain unchanged. `db-apply.sh` deliberately keeps
using the admin secret. Removing Secrets Manager entirely would require a
separate database-access redesign, outside this issue.

This replaces paid application-secret storage with free standard parameters at
personal scale and establishes SSM as the default application-secret mechanism.
It does not eliminate the database-secret charges. Migration rejects payloads
above the 4 KB standard limit; advanced-tier adoption needs an explicit decision.

### Completed rollout and retirement — issue #95

Both environments cut over to SSM and passed deployed auth/business suites. The
five application source secrets were deleted without recovery after exact
source/parameter comparison; the four database secrets remained active. See
[retirement evidence](https://github.com/JoseHRosario/guito-api/issues/95#issuecomment-6100301274).

The historical sequence was a non-destructive byte-for-byte copy to SecureString,
decrypted read-back without logging values, SSM read grants, staging deployment
and live tests, a dev database migration check, then production deployment and
live tests before irreversible application-source deletion. The completed
one-time migration script is removed under #124; normal deployments validate
existing parameters and never recreate credentials.

### Post-cutover cleanup — issue #124

SSM is the only deployed application-secret backend. Local file-backed providers
remain supported; the authorizer requires explicit `AwsSsm` selection and a
parameter name. Legacy application Secrets Manager code and SDK dependencies
are removed where unused. Bank-key normalization and caching are backend-neutral.
Deployment policies retain SSM access and Secrets Manager access to `db-*` only;
live Guito deployment-role reconciliation follows production rollout and must
preserve unrelated statements and all Data API permissions.

Rollback must use an SSM-capable build. Returning to a pre-migration build that
requires the deleted application secrets is not supported by this cleanup;
never deploy one against absent sources.

## Consequences

- Symptom to recognize: auth passes, every business request 500s → check which SA
  the secret carries before suspecting code. Diagnosis: mint a token from the
  secret's SA and replay the Sheets read outside Lambda (read-only), since the
  API's problem-details handler does not surface exception bodies.
- The prod spreadsheet keeps its Google-side sharing restricted to the prod SA.
  Never share the prod sheet with the dev SA to "fix" a secret mismatch — that
  would break the ADR-0007 sheet separation with real financial data.
- A warm Lambda container caches the SA credential at startup; after correcting a
  secret, force a fresh environment before re-smoking, or the fix looks inert.