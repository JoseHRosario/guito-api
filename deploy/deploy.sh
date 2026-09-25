#!/usr/bin/env bash
# T2 (issue #3): full deployment of the Guito API stack — eu-west-1, Project=Guito.
# Idempotent-ish: creates what's missing, updates code/config otherwise.
# Prereqs: aws cli (profile assuming MinervaAIAgent), dotnet 10 SDK, zip.
set -euo pipefail

REGION=eu-west-1
ACCOUNT_ID=$(aws --region "$REGION" sts get-caller-identity --query Account --output text)
ROLE=guito-api-lambda-role

# ENV=production|staging (T8/issue #20). Default staging: a routine deploy is the
# low-risk target and can never hit production by accident; prod is explicit.
ENV=${ENV:-staging}
case "$ENV" in
  production) SUFFIX="";              ASPNETCORE_ENV=Production; SECRET_NAME=guito-api/prod ;;
  staging)    SUFFIX="-staging";      ASPNETCORE_ENV=Staging;    SECRET_NAME=guito-api/staging ;;
  *) echo "ENV must be 'production' or 'staging' (got '$ENV')" >&2; exit 1 ;;
esac
API_NAME=guito-api$SUFFIX
AUTH_NAME=guito-api-authorizer$SUFFIX

# Staging auth parity (T8.1): same OAuth client + allowed emails as production,
# read from the PROD authorizer config when not exported — a staging env must
# exercise the identical human-auth surface, never an invented one.
if [ "$ENV" = staging ]; then
  if [ -z "${GOOGLE_CLIENT_ID:-}" ]; then
    GOOGLE_CLIENT_ID=$(aws --region "$REGION" lambda get-function-configuration \
      --function-name guito-api-authorizer \
      --query 'Environment.Variables.GOOGLE_CLIENT_ID' --output text)
    [ "$GOOGLE_CLIENT_ID" = "None" ] && GOOGLE_CLIENT_ID=""
  fi
  if [ -z "${GOOGLE_ALLOWED_EMAILS:-}" ]; then
    GOOGLE_ALLOWED_EMAILS=$(aws --region "$REGION" lambda get-function-configuration \
      --function-name guito-api-authorizer \
      --query 'Environment.Variables.GOOGLE_ALLOWED_EMAILS' --output text)
    [ "$GOOGLE_ALLOWED_EMAILS" = "None" ] && GOOGLE_ALLOWED_EMAILS=""
  fi
  # Parity is a hard requirement (T8.1): staging human auth must equal prod's.
  if [ -z "$GOOGLE_CLIENT_ID" ] || [ -z "$GOOGLE_ALLOWED_EMAILS" ]; then
    echo "FATAL: cannot read GOOGLE_CLIENT_ID/GOOGLE_ALLOWED_EMAILS from prod authorizer guito-api-authorizer — configure prod first, or export both vars to override." >&2
    exit 1
  fi
fi

# --- 0. Secrets (create only if missing; never echo values) -------------------
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEV_KEY_FILE="$REPO_ROOT/src/guito-api/google-spreadsheets-dev.json"
PROD_SA_FILE="$REPO_ROOT/src/guito-api/google-spreadsheets.json"
# Per-environment SA identity (ADR-0008): the seeded secret must carry the SA of
# THAT environment's spreadsheet — prod seed uses the prod SA key file. Cheap
# wrong-file guard: the dev file at the prod path would silently reproduce the
# T8.3 wrong-SA 500 (auth green, business requests PERMISSION_DENIED).
prod_sa_ok () { # <file> — true unless it carries the dev SA
  [ -f "$1" ] || return 1
  [ "$(python3 -c "import json,sys; print(json.load(open(sys.argv[1]))['client_email'])" "$1")" != "svc-google-sheets-dev@kerumirembora.iam.gserviceaccount.com" ]
}
if ! aws --region "$REGION" secretsmanager describe-secret --secret-id "$SECRET_NAME" >/dev/null 2>&1; then
  NEW_KEY=$(openssl rand -base64 32)
  # Staging: fresh agent key + the dev/staging service-account key (the same SA
  # local dev uses on the dev/staging spreadsheet) in one JSON payload, matching
  # SecretsPayload {GoogleServiceAccount, ApiKeys}. Fails rather than
  # half-provisioning a staging secret the stack cannot use.
  if [ "$ENV" = staging ]; then
    [ -f "$DEV_KEY_FILE" ] || { echo "FATAL: $DEV_KEY_FILE not found — it is gitignored; create it on this machine before a first staging deploy." >&2; exit 1; }
    PAYLOAD=$(python3 -c \
      "import json,sys; sa=json.load(open(sys.argv[2])); print(json.dumps({'GoogleServiceAccount':sa,'ApiKeys':[sys.argv[1]]}))" \
      "$NEW_KEY" "$DEV_KEY_FILE")
    aws --region "$REGION" secretsmanager create-secret --name "$SECRET_NAME" --secret-string "$PAYLOAD" >/dev/null
    echo "Created secret $SECRET_NAME (fresh agent key + dev/staging SA key)."
  else
    if prod_sa_ok "$PROD_SA_FILE"; then
      PAYLOAD=$(python3 -c \
        "import json,sys; sa=json.load(open(sys.argv[2])); print(json.dumps({'GoogleServiceAccount':sa,'ApiKeys':[sys.argv[1]]}))" \
        "$NEW_KEY" "$PROD_SA_FILE")
      aws --region "$REGION" secretsmanager create-secret --name "$SECRET_NAME" --secret-string "$PAYLOAD" >/dev/null
      echo "Created secret $SECRET_NAME (fresh agent key + prod SA key)."
    else
      echo "FATAL: $PROD_SA_FILE not found (or it carries the dev SA) — it is gitignored; create the prod SA key file on this machine before a first production deploy (ADR-0008: the prod secret must carry the prod SA)." >&2
      exit 1
    fi
  fi
fi

# --- 1. IAM role + policies ---------------------------------------------------
aws --region "$REGION" iam get-role --role-name "$ROLE" >/dev/null 2>&1 || {
  cat > /tmp/guito-trust.json <<'EOF'
{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"lambda.amazonaws.com"},"Action":"sts:AssumeRole"}]}
EOF
  aws --region "$REGION" iam create-role --role-name "$ROLE" \
    --assume-role-policy-document file:///tmp/guito-trust.json >/dev/null
}
aws --region "$REGION" iam attach-role-policy --role-name "$ROLE" \
  --policy-arn arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole >/dev/null
aws --region "$REGION" iam put-role-policy --role-name "$ROLE" --policy-name guito-api-secret-read \
  --policy-document "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\",\"Action\":\"secretsmanager:GetSecretValue\",\"Resource\":[\"arn:aws:secretsmanager:$REGION:*:secret:guito-api/prod-*\",\"arn:aws:secretsmanager:$REGION:*:secret:guito-api/staging-*\"]}]}" >/dev/null

# --- 2. Package (arm64 — matches the deployed functions; ARCH=x64 to override) ---
ARCH=${ARCH:-arm64}
dotnet publish src/guito-api -c Release -f net10.0 -r "linux-$ARCH" --self-contained false -o /tmp/pub-api
dotnet publish src/guito-api-authorizer -c Release -f net10.0 -r "linux-$ARCH" --self-contained false -o /tmp/pub-auth
( cd /tmp/pub-api && zip -qr /tmp/guito-api.zip . )
( cd /tmp/pub-auth && rm -f /tmp/guito-authorizer.zip && zip -qr /tmp/guito-authorizer.zip . )

# --- 3. Functions -------------------------------------------------------------
create_or_update () { # name handler memory timeout zip [extra env...]
  local name=$1 handler=$2 memory=$3 timeout=$4 zip=$5; shift 5
  if ! aws --region "$REGION" lambda get-function --function-name "$name" >/dev/null 2>&1; then
    aws --region "$REGION" lambda create-function --function-name "$name" \
      --role "arn:aws:iam::$ACCOUNT_ID:role/$ROLE" --runtime dotnet10 --architectures "$ARCH" \
      --handler "$handler" --zip-file "fileb://$zip" --memory-size "$memory" --timeout "$timeout" \
      --tags Project=Guito "$@" >/dev/null
  else
    aws --region "$REGION" lambda update-function-code --function-name "$name" \
      --zip-file "fileb://$zip" >/dev/null
  fi
  aws --region "$REGION" lambda wait function-active-v2 --function-name "$name"
  # A code update leaves LastUpdateStatus InProgress for a while longer; a
  # configuration update issued immediately races it (ResourceConflictException
  # on CI's fresh runner). Wait it out, then retry a few times as belt and braces.
  aws --region "$REGION" lambda wait function-updated-v2 --function-name "$name" || true
  local last_err=""
  for attempt in 1 2 3 4 5; do
    if err=$(aws --region "$REGION" lambda update-function-configuration --function-name "$name" \
      --handler "$handler" 2>&1 >/dev/null); then
      last_err=""
      break
    fi
    last_err="$err"
    echo "update-function-configuration conflict on $name (attempt $attempt) — retrying…"
    sleep $((attempt * 5))
  done
  # Exhausted retries with the handler still unsynced would leave the next deploy
  # targeting a stale/missing handler — that is a FAILURE, not a warning.
  [ -z "$last_err" ] || { echo "FATAL: update-function-configuration failed for $name after retries: $last_err" >&2; exit 1; }
  aws --region "$REGION" lambda wait function-updated-v2 --function-name "$name"
}

create_or_update "$API_NAME" 'guito-api::GuitoApi.LambdaEntryPoint::FunctionHandlerAsync' 512 30 /tmp/guito-api.zip
create_or_update "$AUTH_NAME" 'guito-api-authorizer::GuitoApiAuthorizer.Function::FunctionHandlerAsync' 128 10 /tmp/guito-authorizer.zip

# --- 3b. Function environment variables (secrets + Google human-auth config) --
# Env updates REPLACE all variables, so values are MERGED into the current
# configuration: a plain redeploy never wipes policy set by a previous run,
# and ASPNETCORE_ENVIRONMENT is always enforced for the target environment.
# Provide GOOGLE_CLIENT_ID / GOOGLE_ALLOWED_EMAILS in the shell environment to
# open the human auth path; until then it stays deny-closed (agent path unaffected).
apply_env () { # function-name vars-json-file
  local cur
  cur=$(aws --region "$REGION" lambda get-function-configuration --function-name "$1" \
    --query 'Environment.Variables' --output json)
  python3 -c "import json,sys; d=json.loads(sys.argv[1]) or {}; d.update(json.load(open(sys.argv[2]))); print(json.dumps({'Variables': d}))" \
    "$cur" "$2" > "/tmp/guito-env-$1.json"
  aws --region "$REGION" lambda update-function-configuration --function-name "$1" \
    --environment "file:///tmp/guito-env-$1.json" >/dev/null
  aws --region "$REGION" lambda wait function-updated-v2 --function-name "$1"
}

export SECRET_NAME GOOGLE_CLIENT_ID GOOGLE_ALLOWED_EMAILS ASPNETCORE_ENV
# Authorizer: secret name always; Google policy when provided.
python3 -c "
import json, os
v = {'SECRETS_SECRET_NAME': os.environ['SECRET_NAME']}
for k in ('GOOGLE_CLIENT_ID', 'GOOGLE_ALLOWED_EMAILS'):
    if os.environ.get(k): v[k] = os.environ[k]
print(json.dumps(v))" > /tmp/guito-auth-env.json
# API: environment value always; human-path policy (OAuth audience + JSON-array
# allowlist) when provided.
python3 -c "
import json, os
v = {'ASPNETCORE_ENVIRONMENT': os.environ['ASPNETCORE_ENV']}
if os.environ.get('GOOGLE_CLIENT_ID'):
    v['AppConfiguration__Authentication__OAuthAudience'] = os.environ['GOOGLE_CLIENT_ID']
if os.environ.get('GOOGLE_ALLOWED_EMAILS'):
    v['AppConfiguration__Authentication__AllowedLogins'] = json.dumps(
        [e.strip() for e in os.environ['GOOGLE_ALLOWED_EMAILS'].split(',')])
print(json.dumps(v))" > /tmp/guito-api-env.json
apply_env "$AUTH_NAME" /tmp/guito-auth-env.json
apply_env "$API_NAME" /tmp/guito-api-env.json
[ -n "${GOOGLE_CLIENT_ID:-}" ] && [ -n "${GOOGLE_ALLOWED_EMAILS:-}" ] || \
  echo "NOTE: GOOGLE_CLIENT_ID/GOOGLE_ALLOWED_EMAILS not set — human auth path deployed deny-closed (agent key path unaffected)."

# --- 3. HTTP API --------------------------------------------------------------
API_ID=$(aws --region "$REGION" apigatewayv2 get-apis --query "Items[?Name=='$API_NAME'].ApiId" --output text)
if [ -z "$API_ID" ]; then
  API_ID=$(aws --region "$REGION" apigatewayv2 create-api --name "$API_NAME" --protocol-type HTTP --query ApiId --output text)
fi

FN_ARN=$(aws --region "$REGION" lambda get-function --function-name "$API_NAME" --query Configuration.FunctionArn --output text)
AUTH_ARN=$(aws --region "$REGION" lambda get-function --function-name "$AUTH_NAME" --query Configuration.FunctionArn --output text)

AUTH_ID=$(aws --region "$REGION" apigatewayv2 get-authorizers --api-id "$API_ID" --query "Items[?Name=='guito-key-authorizer'].AuthorizerId" --output text)
[ -n "$AUTH_ID" ] || AUTH_ID=$(aws --region "$REGION" apigatewayv2 create-authorizer --api-id "$API_ID" --name guito-key-authorizer \
  --authorizer-type REQUEST --authorizer-uri "arn:aws:apigateway:$REGION:lambda:path/2015-03-31/functions/$AUTH_ARN/invocations" \
  --identity-source '$request.header.Authorization' --authorizer-payload-format-version 2.0 \
  --authorizer-result-ttl-in-seconds 0 --query AuthorizerId --output text)
# Issue #13: the single route authorizer dispatches on header inside the function,
# so BOTH headers must trigger an authorizer invocation.
if [ -n "$AUTH_ID" ]; then
  aws --region "$REGION" apigatewayv2 update-authorizer --api-id "$API_ID" --authorizer-id "$AUTH_ID" \
    --identity-source '$request.header.Authorization' >/dev/null
fi

INT_ID=$(aws --region "$REGION" apigatewayv2 get-integrations --api-id "$API_ID" --query 'Items[0].IntegrationId' --output text)
[ "$INT_ID" = "None" ] && INT_ID=""
[ -n "$INT_ID" ] || INT_ID=$(aws --region "$REGION" apigatewayv2 create-integration --api-id "$API_ID" \
  --integration-type AWS_PROXY --integration-uri "arn:aws:apigateway:$REGION:lambda:path/2015-03-31/functions/$FN_ARN/invocations" \
  --payload-format-version 2.0 --query IntegrationId --output text)

# $default → authorizer; /healthz → public; wire integration target if missing
aws --region "$REGION" apigatewayv2 get-routes --api-id "$API_ID" --query 'Items[].[RouteId,RouteKey]' --output text | while read -r id key; do
  if [ "$key" = '$default' ]; then
    aws --region "$REGION" apigatewayv2 update-route --api-id "$API_ID" --route-id "$id" \
      --authorization-type CUSTOM --authorizer-id "$AUTH_ID" --target "integrations/$INT_ID" >/dev/null
  elif [ "$key" = 'ANY /healthz' ]; then
    aws --region "$REGION" apigatewayv2 update-route --api-id "$API_ID" --route-id "$id" \
      --authorization-type NONE --target "integrations/$INT_ID" >/dev/null
  fi
done
aws --region "$REGION" apigatewayv2 create-route --api-id "$API_ID" --route-key '$default' \
  --authorization-type CUSTOM --authorizer-id "$AUTH_ID" --target "integrations/$INT_ID" >/dev/null 2>&1 || true
aws --region "$REGION" apigatewayv2 create-route --api-id "$API_ID" --route-key 'ANY /healthz' \
  --authorization-type NONE --target "integrations/$INT_ID" >/dev/null 2>&1 || true

aws --region "$REGION" lambda add-permission --function-name "$API_NAME" --statement-id apigw-invoke \
  --action lambda:InvokeFunction --principal apigateway.amazonaws.com \
  --source-arn "arn:aws:execute-api:$REGION:$ACCOUNT_ID:$API_ID/*/*" >/dev/null 2>&1 || true
aws --region "$REGION" lambda add-permission --function-name "$AUTH_NAME" --statement-id apigw-invoke \
  --action lambda:InvokeFunction --principal apigateway.amazonaws.com \
  --source-arn "arn:aws:execute-api:$REGION:$ACCOUNT_ID:$API_ID/authorizers/$AUTH_ID" >/dev/null 2>&1 || true

# Stage with access logs
aws --region "$REGION" apigatewayv2 create-stage --api-id "$API_ID" --stage-name '$default' --auto-deploy >/dev/null 2>&1 || true
aws --region "$REGION" logs create-log-group --log-group-name /aws/apigateway/$API_NAME-access >/dev/null 2>&1 || true
aws --region "$REGION" logs put-retention-policy --log-group-name /aws/apigateway/$API_NAME-access --retention-in-days 14 2>/dev/null || true
ACCESS_LOG_ARN=$(aws --region "$REGION" logs describe-log-groups --log-group-name-prefix /aws/apigateway/$API_NAME-access \
  --query 'logGroups[0].arn' --output text)
aws --region "$REGION" apigatewayv2 update-stage --api-id "$API_ID" --stage-name '$default' \
  --access-log-settings '{"DestinationArn":"'"$ACCESS_LOG_ARN"'","Format":"{\"requestId\":\"$context.requestId\",\"ip\":\"$context.identity.sourceIp\",\"httpMethod\":\"$context.httpMethod\",\"path\":\"$context.path\",\"status\":\"$context.status\",\"authorizerError\":\"$context.authorizer.error\",\"integrationError\":\"$context.integration.error\"}"}' >/dev/null
aws --region "$REGION" apigatewayv2 create-deployment --api-id "$API_ID" --stage-name '$default' >/dev/null

echo "Deployed. Endpoint: $(aws --region "$REGION" apigatewayv2 get-api --api-id "$API_ID" --query ApiEndpoint --output text)"
echo "Smoke test: /healthz → 200; no credentials → 401/403; wrong key → 403; valid key (from secret) → 200; garbage x-google-idtoken → 403; valid Google ID token (GOOGLE_CLIENT_ID configured) → 200."
