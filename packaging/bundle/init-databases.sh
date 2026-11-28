#!/bin/sh
set -eu

read_secret() {
    path="$1"
    [ -r "$path" ] || {
        echo "A required protected database credential is unavailable." >&2
        exit 1
    }
    value="$(cat "$path")"
    [ -n "$value" ] || {
        echo "A required protected database credential is empty." >&2
        exit 1
    }
    printf '%s' "$value"
}

PGPASSWORD="$(read_secret "$MUNICODE_POSTGRES_ADMIN_PASSWORD_FILE")"
MUNICODE_APP_PASSWORD="$(read_secret "$MUNICODE_APP_DB_PASSWORD_FILE")"
MUNICODE_KRATOS_PASSWORD="$(read_secret "$MUNICODE_KRATOS_DB_PASSWORD_FILE")"
export PGPASSWORD MUNICODE_APP_PASSWORD MUNICODE_KRATOS_PASSWORD

psql -v ON_ERROR_STOP=1 <<'SQL'
\getenv app_password MUNICODE_APP_PASSWORD
\getenv kratos_password MUNICODE_KRATOS_PASSWORD
SELECT pg_advisory_lock(hashtextextended('municode:database-initialize', 0));
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_database WHERE datname = 'municode' AND pg_get_userbyid(datdba) <> 'municode') THEN
        RAISE EXCEPTION 'Existing municode database has an unexpected owner.';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_database WHERE datname = 'kratos' AND pg_get_userbyid(datdba) <> 'kratos') THEN
        RAISE EXCEPTION 'Existing kratos database has an unexpected owner.';
    END IF;
    IF EXISTS (
        SELECT 1 FROM pg_roles
        WHERE rolname IN ('municode', 'kratos')
          AND (NOT rolcanlogin OR rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls)
    ) OR EXISTS (
        SELECT 1 FROM pg_auth_members
        WHERE member IN (SELECT oid FROM pg_roles WHERE rolname IN ('municode', 'kratos'))
    ) THEN
        RAISE EXCEPTION 'Existing service roles have unexpected privileges.';
    END IF;
END
$$;
SELECT format('CREATE ROLE municode LOGIN PASSWORD %L', :'app_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'municode') \gexec
SELECT format('CREATE ROLE kratos LOGIN PASSWORD %L', :'kratos_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'kratos') \gexec
SELECT 'CREATE DATABASE municode OWNER municode'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'municode') \gexec
SELECT 'CREATE DATABASE kratos OWNER kratos'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'kratos') \gexec
SQL
