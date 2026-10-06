#!/usr/bin/env bash
# db-apply.sh — apply numbered SQL migrations (docs/db/NNN_*.sql) to an Aurora PostgreSQL
# cluster via the RDS Data API (issue #80, ADR-0012). Idempotent: an applied script is
# recorded in the `_migrations` ledger and skipped on re-run.
#
# Usage: ENV=dev|staging|prod deploy/db-apply.sh
#
# Requires the caller's IAM identity to have rds-data:* on the cluster and
# secretsmanager:GetSecretValue on guito-api/db-admin and guito-api/db-<env>.
set -euo pipefail

ENVIRONMENT="${ENV:-dev}"
case "$ENVIRONMENT" in
  dev) DB="guito_dev" ;;
  staging) DB="guito_staging" ;;
  prod) DB="guito_prod" ;;
  *) echo "Unknown ENV '$ENVIRONMENT' (use dev|staging|prod)"; exit 1 ;;
esac

REGION="${REGION:-eu-west-1}"
CLUSTER_ARN="${CLUSTER_ARN:-arn:aws:rds:${REGION}:497087877832:cluster:db-cluster}"
ADMIN_SECRET_ARN="$(aws secretsmanager describe-secret --secret-id guito-api/db-admin --region "$REGION" --query ARN --output text)"
SQL_DIR="$(cd "$(dirname "$0")/../db/migrations" && pwd)"

# sanity: admin credentials work (also resumes an auto-paused cluster before DDL)
aws rds-data execute-statement --resource-arn "$CLUSTER_ARN" --secret-arn "$ADMIN_SECRET_ARN" \
  --database "$DB" --sql "SELECT 1" --no-cli-pager >/dev/null

APPLIED="$(aws rds-data execute-statement --resource-arn "$CLUSTER_ARN" --secret-arn "$ADMIN_SECRET_ARN" \
  --database "$DB" --sql "SELECT name FROM _migrations" --no-cli-pager 2>/dev/null || true)"
if [ -z "$APPLIED" ] && ! aws rds-data execute-statement --resource-arn "$CLUSTER_ARN" --secret-arn "$ADMIN_SECRET_ARN" \
  --database "$DB" --sql "SELECT 1 FROM _migrations LIMIT 1" --no-cli-pager >/dev/null 2>&1; then
  echo "FATAL: _migrations ledger missing in $DB — apply the ledger migration first (db/migrations/001_init.sql)." >&2
  exit 1
fi

for SQL_FILE in "$SQL_DIR"/*.sql; do
  NAME="$(basename "$SQL_FILE")"
  if echo "$APPLIED" | grep -q "\"stringValue\": \"$NAME\""; then
    echo "$NAME already applied — skipping"
    continue
  fi
  echo "Applying $NAME to $DB ..."
  python3 - "$SQL_FILE" "$CLUSTER_ARN" "$ADMIN_SECRET_ARN" "$DB" "$REGION" <<'PYEOF'
import sys, re, subprocess
sql_file, cluster, secret, db, region = sys.argv[1:6]
sql = re.sub(r'--[^\n]*', '', open(sql_file).read())
statements = [p.strip() for p in sql.split(';') if p.strip()]
for s in statements:
    r = subprocess.run(["aws", "rds-data", "execute-statement", "--resource-arn", cluster,
                        "--secret-arn", secret, "--database", db, "--sql", s, "--no-cli-pager"],
                       capture_output=True, text=True)
    if r.returncode != 0:
        sys.exit(f"FAILED statement: {s[:80]}\n{r.stderr.strip()}")
print(f"  {len(statements)} statement(s) ok")
PYEOF
  aws rds-data execute-statement --resource-arn "$CLUSTER_ARN" --secret-arn "$ADMIN_SECRET_ARN" \
    --database "$DB" --sql "INSERT INTO _migrations (name) VALUES ('$NAME')" --no-cli-pager >/dev/null
  echo "$NAME applied ✓"
done
echo "db-apply done ($ENVIRONMENT → $DB)"
