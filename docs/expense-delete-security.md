# DELETE security review (PR #148)

## Recommendation

**Keep `DELETE /Expense/{id}`.** The primary sources reviewed do not prohibit
DELETE for an intentionally exposed, authenticated REST deletion endpoint. They
require restricting unnecessary methods and enforcing authentication and
authorization for the actual method and resource. Replacing this operation with
POST would not automatically improve security: it would still perform the same
destructive action and need the same controls. Never implement deletion via GET.

This addresses the method-choice question in PR comment `4268840532`; it is not a
penetration test, deployed-configuration audit, or approval to delete production data.

## What the primary sources say

- **“Unsafe” is an HTTP semantics term, not a declaration of a vulnerability.**
  RFC 9110 §9.2.1 defines safe methods as essentially read-only. DELETE changes
  state, so it is unsafe in that sense, like POST and PUT. §9.3.5 explicitly defines
  DELETE and recommends 204 when deletion has completed with no response content.
  [Safe methods](https://www.rfc-editor.org/rfc/rfc9110.html#section-9.2.1),
  [DELETE](https://www.rfc-editor.org/rfc/rfc9110.html#section-9.3.5).
- **DELETE is idempotent, not harmless or an authorization mechanism.** Repeating
  the same request has the same intended effect; response codes can differ. A first
  204 and subsequent 404 do not contradict idempotency. Idempotency does not justify
  silently fetching a new revision and retrying a destructive action after an
  uncertain outcome. Follow the existing [recovery contract](expense-edit-delete-contract.md#uncertain-outcome-recovery).
  [RFC 9110 §9.2.2](https://www.rfc-editor.org/rfc/rfc9110.html#section-9.2.2).
- **OWASP recommends an allowlist, not a universal DELETE ban.** Its REST Security
  Cheat Sheet says to reject disallowed methods with 405 and to ensure the caller
  is authorized for the incoming method on the collection, action, and record. It
  also requires HTTPS and access control at each non-public endpoint. The example
  allowlist is illustrative, not an exhaustive set of permitted REST verbs.
  [OWASP REST Security](https://cheatsheetseries.owasp.org/cheatsheets/REST_Security_Cheat_Sheet.html#restrict-http-methods),
  [HTTPS](https://cheatsheetseries.owasp.org/cheatsheets/REST_Security_Cheat_Sheet.html#https),
  [access control](https://cheatsheetseries.owasp.org/cheatsheets/REST_Security_Cheat_Sheet.html#access-control).
- **Generic web-server hardening and intentional REST endpoints are different.**
  OWASP WSTG v4.2 warns about misconfigured methods, arbitrary file uploads with
  PUT, method-dependent access-control bypasses, and method overrides. It explicitly
  says that RESTful services needing other methods must be tested so endpoints
  accept only the methods they require. Disabling unused server/WebDAV authoring
  capabilities is compatible with allowing this application-owned DELETE route;
  a method name alone does not establish arbitrary filesystem deletion. This is
  the relevant distinction behind broad “disable dangerous verbs” advice, not a
  claim to know which article prompted the comment.
  [OWASP WSTG-CONF-06](https://owasp.org/www-project-web-security-testing-guide/v42/4-Web_Application_Security_Testing/02-Configuration_and_Deployment_Management_Testing/06-Test_HTTP_Methods).
- **CSRF depends on credential delivery, not changing DELETE to POST.** OWASP
  explains automatic cookie submission, requires protection for state-changing
  requests, and documents custom-header/preflight defenses with strictly trusted
  CORS origins. Microsoft distinguishes automatically supplied cookie credentials
  from explicitly attached bearer tokens and documents antiforgery tokens sent in
  configured headers. If cookie or other ambient browser authentication is added,
  reassess CSRF protection for DELETE, PUT, and POST together; HTTPS alone does not
  prevent CSRF. XSS or stolen tokens remain risks with header authentication.
  [OWASP CSRF](https://cheatsheetseries.owasp.org/cheatsheets/Cross-Site_Request_Forgery_Prevention_Cheat_Sheet.html),
  [Microsoft ASP.NET Core antiforgery](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0).
- **CORS is not authorization.** It governs browser cross-origin access; a
  non-browser caller can send requests without obeying browser CORS enforcement.
  Explicit origins support browser isolation but cannot replace credential and
  resource checks. [Microsoft CORS](https://learn.microsoft.com/en-us/aspnet/core/security/cors?view=aspnetcore-10.0).
- **If-Match prevents lost updates, not unauthorized deletion.** RFC 9110 §13.1.1
  explicitly includes DELETE among state-changing methods that use If-Match.
  A resource ID or revision is not secret authority to act on that resource.
  [RFC 9110 §13.1.1](https://www.rfc-editor.org/rfc/rfc9110.html#section-13.1.1).

## Application-specific findings and conditions

The issue #146 worktree was inspected; concurrent review fixes may still change it.
The [pinned endpoint contract](expense-edit-delete-contract.md) is the policy baseline.

- `ExpenseController` exposes `[HttpDelete("{id}")]`, delegates deletion, and
  returns 204. The request needs no body: the target is the path ID and the
  precondition is the If-Match header. Avoid a new DELETE-body contract; RFC 9110
  warns that DELETE content lacks generally defined semantics and can cause
  intermediary/request-smuggling problems.
- `ApiKeyMiddleware` and `GoogleIdTokenMiddleware` run before controller dispatch.
  The in-app agent path validates `X-Api-Key`; the human path validates
  `x-google-idtoken`. Deployed clients also send the corresponding `Authorization`
  header for the gateway gate (raw key for agents, Bearer token for humans).
  Reporting authentication is separately restricted to GET `/Expense/history`
  by `SheetsReportingAuthenticationMiddleware`; it must not authorize DELETE.
  No cookie-authentication mechanism was found in this inspected request pipeline.
  Keep both deployed gates enabled; development defaults are not production assurance.
- Access is intentionally shared among permitted humans and agents, irrespective
  of `creatorEmail`. Creator attribution is **not** a per-user ownership filter.
  Protect this documented shared boundary rather than inventing creator-only
  deletion. If a future tenant/role boundary is introduced, enforce it server-side
  on the selected record; predictable integer IDs must never substitute for that check.
- `Startup` selects explicit configured CORS origins outside Development; the
  permissive Development policy is separate. Both permit any method/header at the
  CORS layer, which does not create controller routes or grant authorization.
  Verify installed gateway and application policies allow the intended UI's
  DELETE/preflight headers without trusting arbitrary origins.
- `HttpExpenseRevisionResolver` requires one supported strong quoted revision:
  missing is 428, malformed is 400. `DataApiPostgresExpenseRepository` locks
  the exact ID, checks revision, and deletes using ID plus revision in a transaction;
  stale revision is 412. Linked bank transactions are retained and unlinked.
  `PersonalExpenseOperationFilter` rejects smoke or Sheets authority with 409 on
  invocation, before revision resolution; the Sheets adapter also rejects personal
  operations without transport access. `DeleteSmokeAsync` remains separate Sheets
  cleanup. These are
  concurrency, integrity, and datastore-scope controls—not credential checks.
- Maintain HTTPS-only deployed access, credential secrecy/rotation, appropriate
  database permissions, and useful security logging without credentials. Confirm
  destructive intent in the UI; do not mistake UI confirmation for server-side
  authorization. Method overrides or alternate routes must not bypass these gates.

## Verification expectations

For DELETE specifically, exercise the HTTP boundary with auth gates enabled:

1. No credentials → 401; invalid agent key or invalid/disallowed human token →
   denial (the existing in-app invalid-credential contract is 403), before storage
   access. Attribute deployed denials to the gateway or application separately.
2. Valid permitted human/agent → authorized shared access, including an Expense
   created by another permitted identity; only the exact selected ID changes.
3. Reporting identity → 403 on DELETE; smoke scope or Sheets authority → 409,
   with no personal mutation. `/Smoke/{id}` remains independent Sheets cleanup.
4. Missing/malformed/stale If-Match → 428/400/412 without mutation; missing or
   already-deleted record → 404; accepted deletion → 204, subsequent GET → 404.
   Linked transactions survive and unrelated records/links remain unchanged.
5. Trusted browser origin can preflight DELETE with authentication and If-Match
   headers; untrusted origins receive no granting CORS response. Unsupported
   authenticated methods receive 405 and cannot tunnel into deletion.

Existing boundary/human-auth test sources were inspected, **not executed as part
of the original documentation-only research**. These are acceptance expectations, not a
claim every case is already covered or passes. Hermetic fake-Data-API tests do not
prove live PostgreSQL permissions/locking or installed gateway/CORS behavior;
those remain separately approved staging checks. No production requests, AWS
configuration changes, external PR comments, or source-code changes were made
as part of that research. The subsequent repository consolidation and executed
hermetic evidence are recorded in [the contract](expense-edit-delete-contract.md#aggregate-repository-consolidation).
