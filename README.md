# Guito API

Personal expense-tracking API. Guito helps José control expenses and maximize savings: expenses are created manually, extracted by AI from free-form input, or matched from bank transactions. The API exposes HTTP endpoints consumed by the Guito web UI and CLI/AI agents. Expense and Category authority is solely Postgres via the existing Aurora Data API infrastructure; Sheets is presentation-only, populated by Apps Script from the API.

> Currently under revival (2026): migrating to AWS, modernizing the stack, and fixing bank sync. See the [revival plan](docs/guito-revival.md) and [issue #1](https://github.com/JoseHRosario/guito-api/issues/1).

## Architecture

```
Google (ID token)            CLI / AI agents (X-Api-Key)
        │                              │
        ▼                              ▼
   ┌───────────────────────────────────────────────────────────┐
   │  API Gateway HTTP API  (identity source: Authorization)   │
   │  guito-key-authorizer (REQUEST, one function)             │
   │      dispatches on header:                                │
   │        Bearer <jwt>  → Google ID-token validator  ────────│──▶ Google JWKS
   │        raw Authorization → agent-key validator ────────────│──▶ SSM Parameter Store
   │          │ (allowed)                                      │
   │          ▼                                                │
   │  Lambda — guito-api (.NET 10, arm64)                      │──▶ SSM Parameter Store
   │          │ (GoogleIdTokenMiddleware / ApiKeyMiddleware)   │
   └──────────┼────────────────────────────────────────────────┘
              ▼
     Aurora PostgreSQL (RDS Data API)          PSD2 provider (Enable Banking)
```

- **Runtime**: .NET 10 on AWS Lambda (managed dotnet10 runtime, arm64, eu-west-1) behind API Gateway HTTP API; logs in CloudWatch. Two functions: `guito-api` (the app) and `guito-api-authorizer` (the edge auth gate).
- **Datastore**: Aurora PostgreSQL via RDS Data API is the sole Expense, Category and Bank Transactions datastore in every environment. No selector, Sheets fallback or reverse synchronization. See amended ADR-0014/0015 and [retirement/live acceptance](docs/sheets-persistence-retirement.md); this architecture is not a claim of deployed acceptance.
- **Auth**: dual scheme — a personal `X-Api-Key` for CLI/AI agents and Google ID tokens for the UI (issue #13/#8). API Gateway allows one CUSTOM authorizer per route, so the `guito-key-authorizer` REQUEST authorizer is a single function dispatching on the `Authorization` header (its sole identity source): `Bearer <jwt>` → Google ID-token validation (RS256 against Google's JWKS, issuer/audience/expiry/allowlisted-email checks), raw value → agent-key validation. The two validators are independent — neither path falls back into the other. The same checks repeat inside the API (`ApiKeyMiddleware` / `GoogleIdTokenMiddleware`) as defense-in-depth, and each middleware is path-aware: agent requests carry the key in **both** `X-Api-Key` and `Authorization`; human requests carry the ID token in **both** `Authorization: Bearer <token>` and `x-google-idtoken` (see the sample requests in `src/guito-api/Rest/guito-api.http`). Human-path config (`GOOGLE_CLIENT_ID`, `GOOGLE_ALLOWED_EMAILS`) arrives via Lambda env vars set in `deploy/deploy.sh` §3b; unset → human path deny-closed.
- **Human-token smoke**: the OAuth consent happens once (OAuth Playground, `openid email` scope); the refresh token lives in SSM SecureString `/guito-api/human-auth`, and fresh ID tokens are minted unattended via the Google token endpoint — so the live positive smoke needs no human in the loop.
- **Bank sync**: PSD2 transaction retrieval behind `IListTransactionsService` — Enable Banking (free "Restricted Production" tier) is the provider, selected by `AppConfiguration:BankProvider`; consents are re-authenticated manually (~90 days). Provider adapter ships with the T4 Phase-2 work (issue #5 / ADR-0004).
- **AI extraction**: `POST /AI/extract` uses OpenRouter and Jev with authoritative Postgres category names; unknown categories retain existing rejection semantics.

## Getting started

Prerequisite for build/hermetic tests: .NET 10 SDK. A local API run additionally needs approved non-production Data API references, AWS access and retained feature-secret configuration; no Sheets service-account key is needed.

```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"   # if the SDK is not already on PATH
dotnet restore
dotnet run --project src/guito-api  # Kestrel on an explicitly configured non-production profile
```

```bash
dotnet test --filter "Category!=Integration"   # hermetic unit tests (CI gate)
scripts/run-deployed-tests.sh staging read-only  # approved window; business reads and established auth checks
```

Configuration comes from `appsettings.{Environment}.json` plus environment variables (Data API references, authentication and provider configuration). Application secrets remain in SSM SecureString; database credentials remain in Secrets Manager for RDS Data API (ADR-0008). Never commit either or copy them for hermetic tests. Local development must not target production.

## Sheets reporting refresh

The [private standalone Apps Script loader](docs/sheets-refresh.md) reads complete
live Expense history into dedicated reporting tabs. It mints a short-lived Google
OIDC identity for the reporting-only route; no Guito API key or refresh token is
stored in the spreadsheet or script. Installation, consent and staging reopen
acceptance require the owner's separate approval. Existing archive/formula tabs
are never replaced. Hermetic checks: `node --check scripts/sheets-refresh/Code.js`
and `node --test scripts/sheets-refresh/refresh.test.js` (Node 22; no packages).

## Expense category/month query for agents

`GET /Expense?category=Startup&month=2026-10` returns every exact-category Expense
in the requested Europe/Lisbon booking month, ordered by booking date then ID descending.
Uses the sole Postgres Expense authority. Deployment and live acceptance remain separately approval-gated.
There is no pagination or record cap: Data API failures return an explicit error,
never a partial success. Responses use ordinary MVC JSON serialization. See the [Paperclip consumer contract](docs/expense-category-month.md)
for required query parameters, exact amount strings, deployed `Authorization` + `X-Api-Key`
headers, response shape, errors and provider transport constraints.

## Expense contract and Postgres persistence

`POST /Expense` binds one required `bookingDate` to a `DateTimeOffset`; its JSON value is
a string: `yyyy-MM-dd` for legacy Lisbon midnight, or explicit-offset ISO8601 for a known occurrence instant. It also accepts
exact-decimal `amount` (positive, zero or negative; a JSON number, including exponent notation, or an
invariant decimal string), description and category. Amounts must be exactly
representable by .NET decimal: a 96-bit coefficient and scale 0–28 after removing
trailing zeros. Inputs requiring rounding, underflow or overflow return 400; equivalent
trailing-zero representations are accepted. Latest and AI extraction responses expose
one `amount` as invariant exact decimal text (null for an absent latest amount), avoiding
JavaScript Number rounding; there is no duplicate amount field.
Timestamp `bookingDate` accepts any explicit offset (including UTC `Z`); the server preserves
the instant and derives its Europe/Lisbon calendar day. For example,
`"bookingDate":"2026-07-01T23:30:00.123456Z"` becomes Lisbon July 2, 00:30, with all
microseconds retained. Naive datetime strings (including unresolved DST gaps or
repetitions), invalid/missing dates, and more than six fractional digits return 400,
not rounding. The removed request properties `date` and `occurredAt` return 400 even if null (case-insensitive);
other unknown request properties remain tolerated. Latest has one `bookingDate`
occurrence instant: time-enabled clients resubmit it as POST `bookingDate`, not as a second
field. AI extraction exposes `bookingDate` with its existing DateTime day precision
(an offset-free midnight), not a known instant. Clients submit that calendar day as
`yyyy-MM-dd`. Date-only clients send `yyyy-MM-dd`; explicit-offset timestamps retain their instant and microseconds. Currency defaults
to EUR; non-EUR values are rejected. Create returns `{ "id": 123 }` (an Int32 JSON number).

`GET /Expense/latest/{count}` (1–100) returns `expenses` with numeric `id`,
`bookingDate`, `currency`, `creationDate`, `updateDate`, amount, description, category
and creatorEmail. Postgres stores generated integer IDs (returned as JSON numbers),
`booking_date`, an exact numeric amount, `category_id` referencing the existing authoritative categories, `create_date`, and nullable `update_date`. No currency or test-scope column is
stored. EUR remains a compatibility wire value. Latest orders booking date then integer
ID descending; `updateDate` is null until an explicit update. Unknown category names
not present in Postgres return 422; requests never seed categories.
Latest has no `storedOrder`, `date` or `occurredAt` aliases; callers use `id`.
The legacy `X-Sheet-Scope` header is ignored;
there is no scope override. `/Smoke/{id}` is removed and must never map to a
personal Expense operation. `AppConfiguration:ExpenseStore` / `EXPENSE_STORE`
are retired, not deployment controls.

Deployed business verification performs reads only; the established auth-contract
checks still POST invalid codes and garbage tokens, never valid financial writes.
Replacement write acceptance is deferred; this removal adds no isolation or
cleanup framework. See [retirement evidence boundaries](docs/sheets-persistence-retirement.md).
Routine deployment/migrations run through GitHub Actions without Expense resets
or repeated imports. Hermetic fakes do not establish live persistence or privileges.

## Deployment

Deployed by GitHub Actions to Lambda via OIDC role assumption (no long-lived AWS keys in GitHub). Two functions deploy independently from their own project folders: `src/guito-api` → `guito-api` (handler `guito-api::GuitoApi.LambdaEntryPoint::FunctionHandlerAsync`) and `src/guito-api-authorizer` → `guito-api-authorizer` (REQUEST authorizer for `guito-key-authorizer`, IAM-policy responses, TTL 0). Resources are prefixed `guito-` and tagged `Project=Guito`, account 497087877832, region eu-west-1. `deploy/deploy.sh` implements parameter preflight, IAM, functions, HTTP API, routes and access logs for CI. Routine deployment/migrations run through GitHub Actions; production rollout requires explicit owner approval, not a local shell fallback. Application parameters are already provisioned; deployment validates their existence and never creates replacement credentials. SSM is the only deployed application-secret backend; local JSON files remain supported. The authorizer requires `SECRETS_LOCATION=AwsSsm` and an explicit `SECRETS_SECRET_NAME` parameter path. See [ADR 0008](docs/adr/0008-per-environment-secrets-payload.md) for completed retirement and the retained database-secret exception.

### Post-merge IAM reconciliation

The ADR-0008/#124 reconciliation tool below targets retired application-secret
backend grants; it is not a Google service-account revocation tool. Only after
explicit owner approval, merged rollout and approved read-only/live gates in both
environments may an operator reconcile the shared Guito runtime/deployment-role
inline policies. Follow [the retirement runbook](docs/sheets-persistence-retirement.md)
for #150 exclusive credential inventory and separate destructive approval:

```bash
python3 deploy/reconcile-iam.py                         # dry-run plan, no writes
python3 deploy/reconcile-iam.py --apply --rollout-verified
python3 deploy/reconcile-iam.py                         # expect zero changes
```

The script uses the `minerva-agent` profile and verifies the MinervaAIAgent role,
SSM consumer configuration and policy read-back. `--rollout-verified` is an
explicit attestation of the merge/deployment/SIT gate; it does not run those
checks. Database, SSM and unrelated permissions are preserved. Changes are not
transactional: an error aborts; inspect any partial changes before retrying.
Never run `--apply` before production rollout, and never change Minerva policies.

## Contributing

Feature branches + pull requests; PRs are reviewed and merged by the owner. Commits by the AI agent are authored as `Meireles`. Decisions are recorded as ADRs in [`docs/adr/`](docs/adr) — read them before proposing architectural changes.

## Docs

- [Revival plan](docs/guito-revival.md) — phases and grill-session decisions
- [ADRs](docs/adr/) — rationale and amendments, including retired Sheets persistence decisions
- [GLOSSARY.md](GLOSSARY.md) — domain glossary (Expense, Transaction, Match, Extract, PSD2 provider)

## Related repos

- [`guito-ui`](https://github.com/JoseHRosario/guito-ui) — Angular UI (planned; replaces the archived Next.js `guito-web-app`)
