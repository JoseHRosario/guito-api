#!/usr/bin/env bash
# T8.2 (issue #22) / T5 (issue #6): run the deployed-endpoint integration test
# suite against the LIVE stack of a target environment.
#   Usage: scripts/run-staging-tests.sh [staging|production]   (default: staging)
#
# The suite needs four values this script resolves from AWS (Secrets Manager /
# API Gateway / Lambda) at run time; no key or token is ever stored in the repo
# or echoed:
#   • GUITO_TARGET_BASE_URL        — ApiEndpoint of the target environment's API
#   • GUITO_TARGET_AGENT_KEY       — ApiKeys[0] of the target environment's secret
#   • GUITO_OTHER_AGENT_KEY        — ApiKeys[0] of the OTHER environment's secret
#     (the negative case: a foreign-environment key must be rejected with 403)
#   • GUITO_TARGET_GOOGLE_ID_TOKEN — minted live: refresh-token exchange against
#     Google's token endpoint using RefreshToken/ClientSecret of secret
#     guito-api/human-auth and GOOGLE_CLIENT_ID read from the prod authorizer's
#     function configuration (ADR-0007: staging shares prod's Google config, so
#     the same minted token is valid for both environments).
#     A failed exchange is fatal: a run that silently tests only half the auth
#     contract is the blind spot ADR-0007 warns about.
# Works under any AWS identity with read access to the guito-api secrets —
# MinervaAIAgent locally, the guito-api-deploy OIDC role in CI (issue #6).
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECTS=(
  "sit/guito-api-auth.IntegrationTests/guito-api-auth.IntegrationTests.csproj"
  "sit/guito-api.IntegrationTests/guito-api.IntegrationTests.csproj"
)
REGION=eu-west-1
HUMAN_AUTH_SECRET=guito-api/human-auth
PROD_AUTHORIZER=guito-api-authorizer   # GOOGLE_CLIENT_ID always read from prod (ADR-0007 parity)

ENV_TARGET=${1:-staging}
case "$ENV_TARGET" in
  staging)    API_NAME=guito-api-staging; TARGET_SECRET=guito-api/staging; OTHER_SECRET=guito-api/prod ;;
  production) API_NAME=guito-api;         TARGET_SECRET=guito-api/prod;    OTHER_SECRET=guito-api/staging ;;
  *) echo "Usage: $0 [staging|production] (got '$ENV_TARGET')" >&2; exit 1 ;;
esac

export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"

command -v aws >/dev/null || { echo "FATAL: aws cli not found" >&2; exit 1; }
command -v dotnet >/dev/null || { echo "FATAL: dotnet not found (expected $HOME/.dotnet)" >&2; exit 1; }

# --- Resolve the target base URL from API Gateway -----------------------------
# All four values are exported: the test process reads them as environment vars.
export GUITO_TARGET_BASE_URL
GUITO_TARGET_BASE_URL=$(aws --region "$REGION" apigatewayv2 get-apis \
  --query "Items[?Name=='$API_NAME'].ApiEndpoint" --output text)
if [ -z "$GUITO_TARGET_BASE_URL" ] || [ "$GUITO_TARGET_BASE_URL" = "None" ]; then
  echo "FATAL: no API Gateway named $API_NAME in $REGION — deploy $ENV_TARGET first (ENV is staging by default in deploy/deploy.sh)." >&2
  exit 1
fi
# ApiEndpoint is typically scheme-less (e.g. "abc123.execute-api.eu-west-1.amazonaws.com");
# the test fixture constructs new Uri(BaseUrl), which throws without a scheme.
case "$GUITO_TARGET_BASE_URL" in
  https://*|http://*) ;;
  *) GUITO_TARGET_BASE_URL="https://$GUITO_TARGET_BASE_URL" ;;
esac

# --- Resolve both agent keys from Secrets Manager -----------------------------
secret_key() {
  aws --region "$REGION" secretsmanager get-secret-value --secret-id "$1" \
    --query SecretString --output text \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["ApiKeys"][0])'
}
export GUITO_TARGET_AGENT_KEY
export GUITO_OTHER_AGENT_KEY
GUITO_TARGET_AGENT_KEY=$(secret_key "$TARGET_SECRET")
GUITO_OTHER_AGENT_KEY=$(secret_key "$OTHER_SECRET")
[ -n "$GUITO_TARGET_AGENT_KEY" ] || { echo "FATAL: $TARGET_SECRET has no ApiKeys[0]" >&2; exit 1; }
[ -n "$GUITO_OTHER_AGENT_KEY" ] || { echo "FATAL: $OTHER_SECRET has no ApiKeys[0]" >&2; exit 1; }

echo "$ENV_TARGET endpoint: $GUITO_TARGET_BASE_URL"

# --- Mint a Google ID token for the human-path tests --------------------------
# Same exchange as src/guito-api/Rest/guito-api.http "MintToken": refresh_token
# grant against Google's token endpoint. The ID token lives ~1h — ample for a run.
export GUITO_TARGET_GOOGLE_ID_TOKEN
GUITO_TARGET_GOOGLE_ID_TOKEN=$(aws --region "$REGION" secretsmanager get-secret-value \
  --secret-id "$HUMAN_AUTH_SECRET" --query SecretString --output text \
  | GOOGLE_CLIENT_ID="$(aws --region "$REGION" lambda get-function-configuration \
      --function-name "$PROD_AUTHORIZER" \
      --query 'Environment.Variables.GOOGLE_CLIENT_ID' --output text)" \
    python3 -c '
import json, os, sys, urllib.parse, urllib.request
creds = json.load(sys.stdin)
client_id = os.environ["GOOGLE_CLIENT_ID"]
if not client_id or client_id == "None":
    sys.exit("FATAL: prod authorizer has no GOOGLE_CLIENT_ID — human path deployed deny-closed (ADR-0007 parity broken).")
data = urllib.parse.urlencode({
    "grant_type": "refresh_token",
    "client_id": client_id,
    "client_secret": creds["ClientSecret"],
    "refresh_token": creds["RefreshToken"],
}).encode()
try:
    with urllib.request.urlopen("https://oauth2.googleapis.com/token", data=data, timeout=30) as r:
        print(json.load(r)["id_token"])
except Exception as e:
    sys.exit(f"FATAL: Google token exchange failed ({e}). The refresh token in secret "
             f"guito-api/human-auth is likely expired/revoked — redo the OAuth consent "
             f"flow and store a fresh RefreshToken in that secret.")
')
[ -n "$GUITO_TARGET_GOOGLE_ID_TOKEN" ] || { echo "FATAL: token exchange produced no id_token" >&2; exit 1; }
echo "Google ID token minted for human-path tests."

# --- Run both suites (keys passed as env vars; never written anywhere) -------
# One resolution, both domain suites: auth contract, then business round-trip.
# Common (guito-api.IntegrationTests.Common) is shared plumbing, never run directly.
cd "$REPO_ROOT"
FAILED=0
for PROJECT in "${PROJECTS[@]}"; do
  echo "=== $PROJECT ==="
  if ! dotnet test "$PROJECT" \
    --filter "Category=Integration" \
    --logger "console;verbosity=normal"; then
    FAILED=1
  fi
done
exit "$FAILED"
