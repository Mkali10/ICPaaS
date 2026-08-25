#!/bin/sh
set -eu
: "${POSTGRES_DB:?POSTGRES_DB is required}"
: "${POSTGRES_USER:?POSTGRES_USER is required}"
: "${POSTGRES_PASSWORD:?POSTGRES_PASSWORD is required}"
export PGPASSWORD="$POSTGRES_PASSWORD"
for migration in /migrations/*.sql; do
  version="$(basename "$migration")"
  applied="$(psql -h postgres -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Atqc "SELECT 1 FROM schema_migrations WHERE version='$version'" 2>/dev/null || true)"
  if [ "$applied" != "1" ]; then
    psql -v ON_ERROR_STOP=1 -h postgres -U "$POSTGRES_USER" -d "$POSTGRES_DB" -f "$migration"
  fi
done
