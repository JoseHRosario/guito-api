#!/usr/bin/env bash
# T2 (issue #3): full deployment of the Guito API stack — eu-west-1, Project=Guito.
# Idempotent-ish: creates what's missing, updates code/config otherwise.
# Prereqs: aws cli (profile assuming MinervaAIAgent), dotnet 10 SDK, zip.
set -euo pipefail

REGION=eu-west-1
SECRET_NAME=guito-api/prod
ROLE=guito-api-lambda-role
API_NAME=guito-api

# --- 0. Secrets (create only if missing; never echo values) -------------------
if ! aws --region "$REGION" secretsmanager describe-secret --secret-id "$SECRET_NAME" >/dev/null 2>&1; then
  NEW_KEY=$(openssl rand -base64 32)
  PAYLOAD=$(python3 -c "import json,sys; print(json.dumps({'ApiKeys':[sys.argv[1]]}))" "$NEW_KEY")
  aws --region "$REGION" secretsmanager create-secret --name "$SECRET_NAME" --secret-string "$PAYLOAD" >/dev/null
  echo "Created secret $SECRET_NAME (agent key inside; SA key + sheet id must be merged in manually)."
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
  --policy-document "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\",\"Action\":\"secretsmanager:GetSecretValue\",\"Resource\":\"arn:aws:secretsmanager:$REGION:*:secret:$SECRET_NAME-*\"}]}" >/dev/null

# --- 2. Package (x64 by default; pass arm64 to match arm64 functions) --------
ARCH=${ARCH:-x64}
dotnet publish src/guito-api -c Release -f net10.0 -r "linux-$ARCH" --self-contained false -o /tmp/pub-api
dotnet publish src/guito-api-authorizer -c Release -f net10.0 -r "linux-$ARCH" --self-contained false -o /tmp/pub-auth
( cd /tmp/pub-api && zip -qr /tmp/guito-api.zip . )
( cd /tmp/pub-auth && rm -f /tmp/guito-authorizer.zip && zip -qr /tmp/guito-authorizer.zip . )

# --- 3. Functions -------------------------------------------------------------
create_or_update () { # name handler memory timeout zip [extra env...]
  local name=$1 handler=$2 memory=$3 timeout=$4 zip=$5; shift 5
  if ! aws --region "$REGION" lambda get-function --function-name "$name" >/dev/null 2>&1; then
    aws --region "$REGION" lambda create-function --function-name "$name" \
      --role "arn:aws:iam::*:role/$ROLE" --runtime dotnet10 --architectures "$ARCH" \
      --handler "$handler" --zip-file "fileb://$zip" --memory-size "$memory" --timeout "$timeout" \
      --tags Project=Guito "$@" >/dev/null
  else
    aws --region "$REGION" lambda update-function-code --function-name "$name" \
      --zip-file "fileb://$zip" >/dev/null
  fi
  aws --region "$REGION" lambda wait function-active-v2 --function-name "$name"
}

create_or_update "$API_NAME" 'guito-api::GuitoApi.LambdaEntryPoint::FunctionHandlerAsync' 512 30 /tmp/guito-api.zip
create_or_update "$API_NAME-authorizer" 'guito-api-authorizer::GuitoApiAuthorizer.Function::FunctionHandler' 128 10 /tmp/guito-authorizer.zip

# --- 3b. Google human-auth config (issue #13) ----------------------------------
# Provide GOOGLE_CLIENT_ID / GOOGLE_ALLOWED_EMAILS in the shell environment.
# Until set, the authorizer's human path stays deny-closed and the API rejects
# human requests (empty allowlist) — the agent X-Api-Key path is unaffected.
if [ -n "${GOOGLE_CLIENT_ID:-}" ] && [ -n "${GOOGLE_ALLOWED_EMAILS:-}" ]; then
  aws --region "$REGION" lambda update-function-configuration --function-name "$API_NAME-authorizer" \
    --environment "Variables={SECRETS_SECRET_NAME=$SECRET_NAME,GOOGLE_CLIENT_ID=$GOOGLE_CLIENT_ID,GOOGLE_ALLOWED_EMAILS=$GOOGLE_ALLOWED_EMAILS}" >/dev/null
  # In-app defense-in-depth layer mirrors the same policy for the API function.
  API_EMAILS_JSON=$(python3 -c "import json,sys; print(json.dumps([e.strip() for e in sys.argv[1].split(',')]))" "$GOOGLE_ALLOWED_EMAILS")
  aws --region "$REGION" lambda update-function-configuration --function-name "$API_NAME" \
    --environment "Variables={AppConfiguration__Authentication__OAuthAudience=$GOOGLE_CLIENT_ID,AppConfiguration__Authentication__AllowedLogins=$API_EMAILS_JSON,ASPNETCORE_ENVIRONMENT=Production}" >/dev/null
  aws --region "$REGION" lambda wait function-updated-v2 --function-name "$API_NAME-authorizer"
  aws --region "$REGION" lambda wait function-updated-v2 --function-name "$API_NAME"
else
  echo "NOTE: GOOGLE_CLIENT_ID/GOOGLE_ALLOWED_EMAILS not set — human auth path deployed deny-closed (agent key path unaffected)."
fi

# --- 3. HTTP API --------------------------------------------------------------
API_ID=$(aws --region "$REGION" apigatewayv2 get-apis --query "Items[?Name=='$API_NAME'].ApiId" --output text)
if [ -z "$API_ID" ]; then
  API_ID=$(aws --region "$REGION" apigatewayv2 create-api --name "$API_NAME" --protocol-type HTTP --query ApiId --output text)
fi

FN_ARN=$(aws --region "$REGION" lambda get-function --function-name "$API_NAME" --query Configuration.FunctionArn --output text)
AUTH_ARN=$(aws --region "$REGION" lambda get-function --function-name "$API_NAME-authorizer" --query Configuration.FunctionArn --output text)

AUTH_ID=$(aws --region "$REGION" apigatewayv2 get-authorizers --api-id "$API_ID" --query "Items[?Name=='guito-key-authorizer'].AuthorizerId" --output text)
[ -n "$AUTH_ID" ] || AUTH_ID=$(aws --region "$REGION" apigatewayv2 create-authorizer --api-id "$API_ID" --name guito-key-authorizer \
  --authorizer-type REQUEST --authorizer-uri "arn:aws:apigateway:$REGION:lambda:path/2015-03-31/functions/$AUTH_ARN/invocations" \
  --identity-source '$request.header.X-Api-Key, $request.header.x-google-idtoken' --authorizer-payload-format-version 2.0 \
  --authorizer-result-ttl-in-seconds 0 --query AuthorizerId --output text)
# Issue #13: the single route authorizer dispatches on header inside the function,
# so BOTH headers must trigger an authorizer invocation.
if [ -n "$AUTH_ID" ]; then
  aws --region "$REGION" apigatewayv2 update-authorizer --api-id "$API_ID" --authorizer-id "$AUTH_ID" \
    --identity-source '$request.header.X-Api-Key, $request.header.x-google-idtoken' >/dev/null
fi

INT_ID=$(aws --region "$REGION" apigatewayv2 get-integrations --api-id "$API_ID" --query 'Items[0].IntegrationId' --output text)
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
  --source-arn "arn:aws:execute-api:$REGION:*:$API_ID/*/*" >/dev/null 2>&1 || true
aws --region "$REGION" lambda add-permission --function-name "$API_NAME-authorizer" --statement-id apigw-invoke \
  --action lambda:InvokeFunction --principal apigateway.amazonaws.com \
  --source-arn "arn:aws:execute-api:$REGION:*:$API_ID/authorizers/$AUTH_ID" >/dev/null 2>&1 || true

# Stage with access logs
aws --region "$REGION" apigatewayv2 create-stage --api-id "$API_ID" --stage-name '$default' --auto-deploy >/dev/null 2>&1 || true
aws --region "$REGION" logs create-log-group --log-group-name /aws/apigateway/guito-api-access >/dev/null 2>&1 || true
aws --region "$REGION" logs put-retention-policy --log-group-name /aws/apigateway/guito-api-access --retention-in-days 14 2>/dev/null || true
aws --region "$REGION" apigatewayv2 update-stage --api-id "$API_ID" --stage-name '$default' \
  --access-log-settings '{"DestinationArn":"arn:aws:logs:'"$REGION"':*:log-group:/aws/apigateway/guito-api-access","Format":"{\"requestId\":\"$context.requestId\",\"ip\":\"$context.identity.sourceIp\",\"httpMethod\":\"$context.httpMethod\",\"path\":\"$context.path\",\"status\":\"$context.status\",\"authorizerError\":\"$context.authorizer.error\",\"integrationError\":\"$context.integration.error\"}"}' >/dev/null
aws --region "$REGION" apigatewayv2 create-deployment --api-id "$API_ID" --stage-name '$default' >/dev/null

echo "Deployed. Endpoint: $(aws --region "$REGION" apigatewayv2 get-api --api-id "$API_ID" --query ApiEndpoint --output text)"
echo "Smoke test: /healthz → 200; no credentials → 401/403; wrong key → 403; valid key (from secret) → 200; garbage x-google-idtoken → 403; valid Google ID token (GOOGLE_CLIENT_ID configured) → 200."
