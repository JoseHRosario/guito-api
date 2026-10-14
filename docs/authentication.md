# Authentication contract

Read before changing auth, secret consumers, OAuth errors or public routes.
See [ADR-0003](adr/0003-dual-auth-google-pkce-plus-api-key.md) and
[ADR-0017](adr/0017-secretless-sheets-reporting-identity.md) for rationale.

## Independent gates, one edge dispatcher

API Gateway uses one REQUEST authorizer with `Authorization` as its sole identity
source, IAM Allow/Deny responses and TTL 0. `src/guito-api-authorizer/Function.cs`
dispatches raw values to AgentKey validation and Bearer tokens to Google validation;
reporting audience selects its distinct confined policy. A failed path never falls
back to another. Missing/invalid credentials fail closed. Keep the authorizer small:
no business logic, Sheets access or API assembly dependency. Crypto/key logic may be
duplicated deliberately between separate assemblies; review both when changing it.

| Caller | Deployed request headers |
| --- | --- |
| Agent | Same key in raw `Authorization` and `X-Api-Key` |
| Human UI | Same Google ID token in Bearer `Authorization` and `x-google-idtoken` |
| Reporting loader | Short-lived Google OIDC Bearer token; distinct exact audience/subject, GET `/Expense/history` only |

`X-Api-Key` and `x-google-idtoken` are not gateway identity sources. Inside the API,
reporting confinement runs first, then ApiKeyMiddleware, then GoogleIdTokenMiddleware
(`Startup.cs`). A valid agent key skips only the Google token check. Human validation
checks the duplicate ID-token header. Reporting tokens cannot become human sessions
or agent keys; any supplied second token must agree. Preserve exact subject/audience,
verified-email and method/path rejection at both gates (see [Sheets refresh](sheets-refresh.md)).

Edge human validation verifies RS256/JWKS, issuer, audience, expiry and email allowlist.
`GOOGLE_CLIENT_ID`/`GOOGLE_ALLOWED_EMAILS` and the API's bound
`AppConfiguration:Authentication` are configured by the deployment pipeline;
missing deployed human configuration must not open access. Local dev configuration
is not evidence of deployed validation. See `Rest/guito-api.http` under the API project
for placeholder-only examples; never paste actual tokens into docs or logs.

## Credentials and responses

Application credentials use SSM SecureString `/guito-api/prod` or
`/guito-api/staging`; local file providers remain available. The API's SecretsPayload
and the authorizer's raw `ApiKeys` loader share payload shape, not a common assembly.
A payload change requires coordinated consumer deployment. The authorizer requires
explicit `SECRETS_LOCATION=AwsSsm` and `SECRETS_SECRET_NAME`. Database credentials
remain Secrets Manager secrets because Data API requires a secret ARN (ADR-0008).
Google refresh credentials for approved unattended smoke are in `/guito-api/human-auth`.
Credential changes require approval; never modify `~/.aws`, print or commit secrets.

Expected business errors use ProblemException → ExceptionToProblemDetailsHandler.
Preserve the owner-approved Google OAuth exception: AuthController token/logout
maps GoogleTokenExchangeException through CallGoogleAsync/GoogleError to verbatim
RFC 6749 `{error, error_description}`, with upstream status (502 on provider failure).
The UI parses that shape; replacing it with ProblemDetails loses error detail.

## Public routes

A route is public only when the gateway route and all in-app gates agree. Check
`deploy/deploy.sh`, middleware public-path definitions and auth SIT together;
`/healthz` alone warms neither auth nor persistence. `/warm` is a separate best-effort
DB wake. Do not broaden public exceptions to fix an authenticated-route failure.
