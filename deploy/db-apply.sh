#!/usr/bin/env bash
# db-apply.sh — apply numbered SQL migrations (docs/db/NNN_*.sql) to an Aurora PostgreSQL
# cluster via the RDS Data API (issue #80, ADR-0012). Idempotent: an applied script is
# recorded in the `_migrations` ledger and skipped on re-run.
#
# Usage: ENV=dev|staging|prod|production deploy/db-apply.sh   ('production' is the
# deploy.sh spelling — an alias of prod — so deploy can pass ENV straight through)
#
# Requires the caller's IAM identity to have rds-data:* on the cluster and
# secretsmanager:GetSecretValue on guito-api/db-admin and guito-api/db-<env>.
set -euo pipefail

ENVIRONMENT="${ENV:-dev}"
case "$ENVIRONMENT" in
  dev) DB="guito_dev" ;;
  staging) DB="guito_staging" ;;
  prod|production) DB="guito_prod"; ENVIRONMENT="prod" ;;
  *) echo "Unknown ENV '$ENVIRONMENT' (use dev|staging|prod|production)" >&2; exit 1 ;;
esac

REGION="${REGION:-eu-west-1}"
CLUSTER_ARN="${CLUSTER_ARN:-arn:aws:rds:${REGION}:497087877832:cluster:db-cluster}"
ADMIN_SECRET_ARN="$(aws secretsmanager describe-secret --secret-id guito-api/db-admin --region "$REGION" --query ARN --output text)"
SQL_DIR="$(cd "$(dirname "$0")/../db/migrations" && pwd)"

# Every Data API statement goes through this retry wrapper: an auto-paused Aurora
# cluster answers DatabaseResumingException for a few seconds while waking (hit
# LIVE in CI on PR #117 — the deploy died on the first SELECT 1). Same policy as
# the app-side DataApiClient retry (PR #108).
rds_exec () { # <sql> [query-flags...] — retries while the cluster resumes
  local sql="$1"; shift
  local attempt=1 max=12 delay=5
  while true; do
    if out=$(aws rds-data execute-statement --resource-arn "$CLUSTER_ARN" --secret-arn "$ADMIN_SECRET_ARN" \
      --database "$DB" --sql "$sql" "$@" 2>&1); then
      printf '%s' "$out"
      return 0
    fi
    if echo "$out" | grep -q DatabaseResumingException && [ "$attempt" -lt "$max" ]; then
      echo "  cluster resuming (attempt $attempt/$max) — waiting ${delay}s ..."
      sleep "$delay"
      attempt=$((attempt + 1))
    else
      echo "$out" >&2
      return 1
    fi
  done
}

# sanity: admin credentials work (also resumes an auto-paused cluster before DDL)
rds_exec "SELECT 1" --no-cli-pager >/dev/null

APPLIED="$(rds_exec "SELECT name FROM _migrations" --no-cli-pager 2>/dev/null || true)"
# A fresh database has no _migrations ledger yet — 001_init.sql creates it and the
# loop below applies everything; the guard only protects against a CORRUPTED ledger.
if [ -z "$APPLIED" ]; then
  rds_exec "SELECT 1 FROM information_schema.tables WHERE table_name='_migrations'" --no-cli-pager 2>/dev/null | grep -q stringValue \
    && { echo "FATAL: _migrations ledger exists but is unreadable in $DB." >&2; exit 1; }
  echo "No _migrations ledger in $DB — fresh database, applying all migrations."
fi

for SQL_FILE in "$SQL_DIR"/*.sql; do
  NAME="$(basename "$SQL_FILE")"
  if echo "$APPLIED" | grep -q "\"stringValue\": \"$NAME\""; then
    echo "$NAME already applied — skipping"
    continue
  fi
  echo "Applying $NAME to $DB ..."
  python3 - "$SQL_FILE" "$CLUSTER_ARN" "$ADMIN_SECRET_ARN" "$DB" "$REGION" <<'PYEOF'
import sys, re, subprocess, time
sql_file, cluster, secret, db, region = sys.argv[1:6]
sql = re.sub(r'--[^\n]*', '', open(sql_file).read())
statements = [p.strip() for p in sql.split(';') if p.strip()]
def run(s):
    # Retry while the auto-paused cluster resumes (DatabaseResumingException) —
    # same policy as the app-side DataApiClient (PR #108) and rds_exec above.
    for attempt in range(12):
        r = subprocess.run(["aws", "rds-data", "execute-statement", "--resource-arn", cluster,
                            "--secret-arn", secret, "--database", db, "--sql", s, "--no-cli-pager"],
                           capture_output=True, text=True)
        if r.returncode == 0:
            return None
        if "DatabaseResumingException" in (r.stderr or ""):
            print(f"  cluster resuming (attempt {attempt + 1}/12) — waiting 5s ...", flush=True)
            time.sleep(5)
            continue
        return r.stderr.strip()
    return "DatabaseResumingException persisted after 12 attempts"
for s in statements:
    err = run(s)
    if err:
        sys.exit(f"FAILED statement: {s[:80]}\n{err}")
print(f"  {len(statements)} statement(s) ok")
PYEOF
  rds_exec "INSERT INTO _migrations (name) VALUES ('$NAME')" --no-cli-pager >/dev/null
  echo "$NAME applied ✓"
done

# Tables are created by the postgres admin role, but the app connects as the per-env
# role (guito_dev/staging/prod, issue #80) — grant it on everything the migrations
# made, plus default privileges so future migrations need no manual grant (issue #91:
# hit live — bank_accounts was permission-denied for the app role).
echo "Granting app-role privileges on $DB to $DB ..."
rds_exec "GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO $DB" --no-cli-pager >/dev/null
rds_exec "GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO $DB" --no-cli-pager >/dev/null
rds_exec "ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO $DB" --no-cli-pager >/dev/null
rds_exec "ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO $DB" --no-cli-pager >/dev/null

echo "db-apply done ($ENVIRONMENT → $DB)"
