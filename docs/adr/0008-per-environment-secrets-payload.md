# Deployed secrets: one service account per environment

## Status

Accepted (2026-09-25, found live during T8.3 prod regression proof — issue #23)

## Context

Each deployed environment (prod, staging) reads its Google Sheets credential and
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
the existing interfaces (ADR 0011). Names include the leading slash. `Aws` remains
available during rollout; neither backend changes the JSON payload contract.
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

### Rollout and retirement

1. `uv run --with boto3 --with awscrt python deploy/migrate-secrets-to-ssm.py`
   copies application payloads without printing values. It refuses conflicting
   destinations and verifies decrypted read-back byte-for-byte, logging names and
   field names only. It never deletes sources or touches database credentials.
2. Grant `ssm:GetParameter` on `parameter/guito-api/*` to Lambda and deploy roles;
   retain old Secrets Manager grants during cutover.
3. Deploy staging and run `scripts/run-deployed-tests.sh staging`, covering agent
   and live Google-token auth plus sandbox business writes. Exercise
   `ENV=dev deploy/db-apply.sh` to prove the database exception still works.
4. Merge the PR to master; the normal production CI deploy selects SSM. Run the
   same deployed suites against production before retiring its source secrets.
5. Only after both environments are proven, verify all five source/parameter
   pairs still match, then explicitly delete the five **application** source
   secrets with `--force-delete-without-recovery`. Never delete `db-*` secrets.
   IAM cleanup is a separate follow-up. Until production cutover, retain all
   source secrets so rollback to `Aws` stays possible.

Rollback before retirement: restore the old build and `Aws` location/names in
both API and authorizer configuration; preserve Google policy and DB references.
After irreversible source deletion, rollback requires recreating application
secrets from SSM first; never roll back to an `Aws` build against absent sources.

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