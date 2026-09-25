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

The per-environment secret is the source of truth for that environment's SA
identity, and it must match that environment's spreadsheet sharing. When a first
deploy seeds a secret, it seeds the SA key matching that environment (prod seed
reads `src/guito-api/google-spreadsheets.json`, staging/dev seed reads
`google-spreadsheets-dev.json`); if the matching key file is absent on the
machine running the deploy, the deploy fails rather than half-provisioning.
A cheap guard rejects a file carrying the dev SA at the prod path, so the
seed cannot silently reproduce the original bug.

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