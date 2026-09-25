#!/usr/bin/env bash
# T8.2 (issue #22): run the deployed-endpoint integration test suite against the
# LIVE staging stack. The suite needs three values this script resolves:
#   • GUITO_STAGING_BASE_URL  — ApiEndpoint of API Gateway guito-api-staging
#   • GUITO_STAGING_AGENT_KEY — ApiKeys[0] of secret guito-api/staging
#   • GUITO_PROD_AGENT_KEY    — ApiKeys[0] of secret guito-api/prod (the negative case)
# All three are fetched from AWS Secrets Manager / API Gateway at run time under
# the MinervaAIAgent assumed role; no key is ever stored in the repo or echoed.
# Usage: scripts/run-staging-tests.sh   (aws cli authenticated; dotnet SDK on PATH)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="tst/guito-api.IntegrationTests/guito-api.IntegrationTests.csproj"
REGION=eu-west-1
API_NAME=guito-api-staging
STAGING_SECRET=guito-api/staging
PROD_SECRET=guito-api/prod

export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"

command -v aws >/dev/null || { echo "FATAL: aws cli not found" >&2; exit 1; }
command -v dotnet >/dev/null || { echo "FATAL: dotnet not found (expected $HOME/.dotnet)" >&2; exit 1; }

# --- Resolve staging base URL from API Gateway -------------------------------
# All three values are exported: the test process reads them as environment vars.
export GUITO_STAGING_BASE_URL
GUITO_STAGING_BASE_URL=$(aws --region "$REGION" apigatewayv2 get-apis \
  --query "Items[?Name=='$API_NAME'].ApiEndpoint" --output text)
if [ -z "$GUITO_STAGING_BASE_URL" ] || [ "$GUITO_STAGING_BASE_URL" = "None" ]; then
  echo "FATAL: no API Gateway named $API_NAME in $REGION — deploy staging first (ENV is staging by default in deploy/deploy.sh)." >&2
  exit 1
fi

# --- Resolve both agent keys from Secrets Manager ----------------------------
secret_key() {
  aws --region "$REGION" secretsmanager get-secret-value --secret-id "$1" \
    --query SecretString --output text \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["ApiKeys"][0])'
}
export GUITO_STAGING_AGENT_KEY
export GUITO_PROD_AGENT_KEY
GUITO_STAGING_AGENT_KEY=$(secret_key "$STAGING_SECRET")
GUITO_PROD_AGENT_KEY=$(secret_key "$PROD_SECRET")
[ -n "$GUITO_STAGING_AGENT_KEY" ] || { echo "FATAL: $STAGING_SECRET has no ApiKeys[0]" >&2; exit 1; }
[ -n "$GUITO_PROD_AGENT_KEY" ] || { echo "FATAL: $PROD_SECRET has no ApiKeys[0]" >&2; exit 1; }

echo "Staging endpoint: $GUITO_STAGING_BASE_URL"

# --- Run the suite (keys passed as env vars; never written anywhere) ---------
cd "$REPO_ROOT"
dotnet test "$PROJECT" \
  --filter "Category=Integration" \
  --logger "console;verbosity=normal"
