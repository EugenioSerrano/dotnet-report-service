#!/bin/sh
# Creates the application's own login role and the database it owns. The server
# runs this once, as its bootstrap user, when it initialises an empty data
# directory. The role's password comes from the environment and is never
# written to a file or a log.
if [ -z "${REPORTSERVICE_DB_PASSWORD:-}" ]; then
  echo "application role password (REPORTSERVICE_DB_PASSWORD) is not set" >&2
  exit 1
fi
psql -v ON_ERROR_STOP=1 --no-psqlrc --username "$POSTGRES_USER" --dbname postgres \
  --set=app_password="$REPORTSERVICE_DB_PASSWORD" <<'EOSQL'
CREATE ROLE reportservice LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD :'app_password';
CREATE DATABASE reportservice OWNER reportservice;
REVOKE ALL ON DATABASE reportservice FROM PUBLIC;
EOSQL
