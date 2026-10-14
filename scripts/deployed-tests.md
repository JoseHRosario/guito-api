# Deployed verification after Sheets persistence retirement

Default/CI verification is **read-only** for both environments:

```sh
scripts/run-deployed-tests.sh staging read-only
scripts/run-deployed-tests.sh production read-only
```

`Category=Integration` remains the deployed-suite trait. `LiveMutation=true`
marks every mutating test, including negative auth POSTs and token/logout tests.
Read-only runs exclude them. The shared HTTP transport independently refuses
non-GET/HEAD/OPTIONS methods unless disposable staging is attested, blocks
cross-origin requests, and disables redirects. Google ID-token minting still
uses the established refresh-token exchange; production API verification sends
no mutation requests. Agent and human business/history reads remain covered.
Apps Script/reporting configuration and installer OAuth are retained; history
reads alone do not establish live installer/reporting-identity acceptance.

## Isolated Postgres acceptance (separate approval gate)

No disposable datastore is established merely by the word `staging`, a new
marker/category, or a different secret name. Do not use personal data. Before
execution the operator must inspect the **actual deployed** connection target,
DDL, seeded categories, database role grants and all writers, and attest:

- The target database is disposable, contains no personal financial records,
  and is not production. No copy of personal data may be used as a test fixture.
- Its database credentials have exclusive access to that disposable database,
  not production/personal databases. Different ARN text alone is not proof of grants.
- Synthetic create and revision-aware permanent delete are approved. Retention
  and manual reconciliation of ambiguous/failed cleanup are agreed.
- The specified category already exists; no seeding/category creation is performed.
- Deployments, configuration/domain remapping and concurrent writers are held
  for the entire run. Initial Lambda config readback is not a server-enforced lock.

Supply a local, untracked JSON attestation with these exact fields (values must
come from inspection, not this illustrative schema):

```json
{
  "environment": "staging",
  "baseUrl": "https://EXACT-STAGING-ORIGIN",
  "approvedBy": "operator/change-reference",
  "expiresAt": "ISO-8601 UTC expiry within four hours",
  "disposable": true,
  "noPersonalData": true,
  "exclusiveDatabaseCredentials": true,
  "syntheticCreateDeleteApproved": true,
  "category": "existing category name",
  "resourceArn": "installed Database__ResourceArn",
  "databaseName": "installed Database__DatabaseName",
  "secretArn": "installed Database__SecretArn",
  "revisionId": "inspected staging Lambda RevisionId",
  "codeSha256": "inspected staging Lambda CodeSha256"
}
```

The runner reads staging and production Lambda configuration and fails closed
unless all three database settings are explicit installed environment variables,
match the attestation, and differ from production by database target and secret.
Bundled appsettings defaults are deliberately insufficient: do not mutate config
just to satisfy a test; arrange an approved disposable deployment first. The JSON
is operator evidence, not a cryptographic permission boundary or automatic grant
audit. Missing/unverified attestation means **blocked**, never fallback to personal.

After those prerequisites and explicit execution approval:

```sh
GUITO_STAGING_ISOLATION_FILE=/private/path/attestation.json \
  scripts/run-deployed-tests.sh staging round-trip
```

The round-trip POSTs one unique marker, retains the returned numeric identity,
reads detail with a fresh human client, verifies exact amount/date/category and
ETag/revision, and checks latest-list presence. Cleanup GETs that exact identity,
rechecks this run's values, and DELETEs only with its loaded strong If-Match.
It then verifies detail 404 and latest-list absence. It never calls `/Smoke`,
uses retired scope headers, deletes by list position, or retries an ambiguous
mutation. Failed/mismatched readback refuses deletion. A lost create response
can leave an unknown disposable record: reconcile the disposable database under
the operator's agreement; never rerun blindly or infer ownership from values.
No personal-record writes/deletes are allowed even for negative auth tests.

## Evidence boundaries

Record hermetic tests, exact deployed code/config stamp, isolated staging
round-trip/cleanup, read-only production, Apps Script reporting/authentication,
and credential retirement independently. Routine deployments do not run isolated
acceptance automatically and do not prove it passed. This tooling does not revoke
service-account keys, edit shared secret payloads, or mutate IAM. Exclusive
credential retirement still requires ownership inventory, owner authorization,
preservation of unrelated fields/Apps Script identity and readback evidence.
