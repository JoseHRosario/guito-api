# User-session cold-start follow-up — local evidence (#156)

## Scope and incident attribution

API base: `10121de6e829cd6aa7a7a666bf25925330c3730a`, branch
`feature/156-persistent-login`, declared version remains 0.27.0. This follow-up is
local source/test work, not deployment, provisioning, an IAM/schema change or a
live financial/auth test. Protected `AGENTS.md` is unchanged.

The pinned cross-repository investigation supplied these **observed staging
facts** (not re-read from AWS by this worker): at 07:07:53 UTC a session token
request began; Google exchange returned 200 at 07:07:54, JWKS returned 200 at
07:07:55, and the endpoint returned 503 after 16295.4641 ms. The next login
returned 200 in 740 ms. The cluster allowed min ACU 0 with a 300-second auto-pause;
Lambda and gateway bounds were 30 seconds. The browser's previous session deadline
was 15 seconds. Metrics/events reads were denied, with no IAM expansion authorized.
There was no underlying datastore exception in those logs.

**Inference, not proof:** this sequence is consistent with downstream cold-start
latency after consuming the Google code; it does not prove Aurora was paused or
which exception caused that 503. This change removes the identified ordering
hazard and provides sanitized categorical diagnostics for later attribution.
`SELECT 1` readiness cannot guarantee a later session write or key operation.

Canonical `UserSessionEncryptionKey` and the single direct
`011_user_sessions.sql` migration are untouched. The previously observed API
pipeline rejection on canonical-key validation remains a separate deployment
prerequisite; no staging field rename, key read/provisioning/rotation, migration
execution, push or production action was performed by this worker.

## Implemented contract

- `IUserSessionRepository.EnsureReadyAsync` keeps the port in the existing
  aggregate. Services retain datastore-agnostic repository/provider dependencies.
- Read-only readiness runs before Google code exchange and before refresh lease
  acquire. Its 18-second subbudget is inside a caller-linked 25-second operation
  budget; acquire, provider/crypto and completion do not reset that budget.
- Only `DatabaseResumingException` / `DatabaseUnavailableException` enter the new
  one-second readiness retry. Existing shared transport resume delays remain
  exactly one/five seconds, including for existing consumers; their elapsed time
  counts inside readiness. SQL, credentials, unknown/network failures and ambiguous
  mutations gain no retries or synthetic success responses.
- Only known startup/availability exhaustion returns 503 `service_starting` with
  safe “Please try again” wording. Other failures remain `temporarily_unavailable`.
  No automatic authorization-code exchange or business mutation replay is added.
- Refresh has at most two additional seconds of awaited conditional lease cleanup,
  including uncertain acquisition/caller cancellation: 27 seconds application
  budget plus cleanup, below the observed 30-second host/gateway limits. The
  two-minute durable lease recovers crashed workers. No background scheduler.
- Categorical stage/type/trace/timing diagnostics never receive raw exception
  objects or messages. Session Google/JWKS HttpClient default logging is disabled:
  capture tests exposed raw HttpClient transport exception logging otherwise.
- The paired UI's pinned session HTTP deadline is 35 seconds. This API artifact
  does not claim UI implementation or real browser/gateway acceptance.

## Red → green evidence

Commands use `PATH="$HOME/.dotnet:$PATH"`, `DOTNET_ROOT="$HOME/.dotnet"` and
`DOTNET_USE_POLLING_FILE_WATCHER=1`. The last setting avoids the host's inotify
pressure; production settings were not changed. Tests run through the real HTTP
host/controller/services/repository; transport-aware cold-start/deadline scenarios
use the real shared `DataApiClient` over the SDK transport fake (the first minimal
ordering reproducer retains the pre-existing repository transport seam). SDK failures are actually
thrown, not replaced with canned successful datastore responses. A controllable
TimeProvider advances production timers; no repeated 18/25-second sleeps.

| Vertical slice | Observed RED | Observed GREEN |
|---|---|---|
| Readiness before exchange | `Assert.Empty(state.Forms)` failed: one Google code exchange had occurred | readiness failure consumes no code; restoring DB allows the same fake callback to establish |
| Known readiness exhaustion | expected `service_starting`, got `temporarily_unavailable` | typed availability/resume exhaustion at 18 seconds; no Google exchange, safe retry message, recovery |
| Shared resume-delay seam | controlled one-second timer absent: transport used wall-clock delay | actual shared one/five-second delays run on injected clock, three SDK probes then one Google exchange |
| Whole refresh deadline | 25-second timer absent while lease acquire stalled | stalled acquire canceled under the operation budget, original credential/cookie preserved and recovery succeeds |
| Safe diagnostics | stage/category missing; enabling capture also exposed raw HttpClient exception objects | categorical stage/type logs, no captured exception objects or fake secret/token/code/email/key/PKCE values, including log scopes |
| Refresh timeout stage | `lease_acquire category=request_timeout` missing | refresh uses shared operation diagnostics/deadline, including acquire |
| Known shared resume category | `database_resuming type=DatabaseResumingException` missing | shared retry emits type/category without exception payload |
| Known Google config failure | expected `configuration_unavailable`, got provider category | reliable configuration marker preserves safe temporary 503 wire shape |
| Unknown stalled readiness | timeout category missing | `request_timeout`, not a startup claim |
| Known database unavailable category | `database_unavailable type=DatabaseUnavailableException` missing | readiness reports safe categorical type before timeout classification |

Additional HTTP characterizations cover caller cancellation during readiness,
acquire, provider and completion; preservation/recovery of the original session;
startup exhaustion on refresh; one 25-second budget across prior readiness and
later provider/completion stalls; establishment stalls at readiness/provider/write;
awaited two-second cleanup and durable-lease recovery at two minutes; no new
mutation/code replay; SQL/credential/network errors are neither retried nor called
startup. Existing lease-identity/logout/crypto/identity/public-boundary tests remain
in the full suite.

Raw red/green logs are retained outside git under the active profile's scratch:
`156-api-red-{1..10}.log`, `156-api-green-{1..7}.log`, and
`156-api-green-10.log` (final focused suite). The config and unknown-readiness greens
are also captured as passing cases in subsequent red/full-suite logs. Intermediate
compile/analyzer corrections were corrected before the final zero-warning build;
they are not claimed as behavioral RED evidence.

## Verification result

- `dotnet build`: succeeded, **0 warnings, 0 errors**.
- `dotnet test --filter 'Category!=Integration'`: **749 API + 79 authorizer passed**;
  Integration-tagged suites had no matching tests and no live tests were invoked.
- Focused cold-start/deadline HTTP suite: **24 passed**.
- `python3 -m unittest discover -s scripts/tests -v`: **34 passed**, offline fakes.
- All **3 tracked shell scripts**: `bash -n` passed.
- `git diff --check`: passed.

Build/full-suite logs and TRX records are retained in active-profile scratch as
`156-api-build.log`, `156-api-full-tests.log`, `156-api-final-results/` (final full
run), `156-api-test-results/` (earlier full run), and
`156-api-python.log`. Counts come from actual runner/TRX output, not estimated
coverage. Parent independent review is still a separate gate. No claim is made of
live PostgreSQL resume/locks, Google consent, browser cookie behavior, Android
persistence, deployed secret availability or a successful staging rollout.
