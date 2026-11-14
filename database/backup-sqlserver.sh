#!/usr/bin/env bash
set -euo pipefail

container="${SQL_CONTAINER:-creolive-sql2025}"
database="${SQL_DATABASE:-CdNewsfeedDb}"
backup_root="${SQL_BACKUP_DIR:-/var/backups/sinhala-news}"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
backup_name="${database}_${timestamp}.bak"
container_path="/var/opt/mssql/data/${backup_name}"

mkdir -p "$backup_root"
chmod 700 "$backup_root"
sa_password="$(docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$container" | sed -nE 's/^(MSSQL_SA_PASSWORD|SA_PASSWORD)=(.*)$/\2/p' | sed -n '1p')"
if [[ -z "$sa_password" ]]; then
  echo "Could not read the SQL Server container password from its environment." >&2
  exit 1
fi

sqlcmd_path="$(docker exec "$container" sh -c 'command -v sqlcmd || test ! -x /opt/mssql-tools18/bin/sqlcmd || echo /opt/mssql-tools18/bin/sqlcmd')"
if [[ -z "$sqlcmd_path" ]]; then
  echo "sqlcmd was not found inside container $container." >&2
  exit 1
fi

run_sqlcmd() {
  local query="$1"
  printf '%s\n' "$sa_password" | docker exec -i "$container" sh -c '
    IFS= read -r SQLCMDPASSWORD
    export SQLCMDPASSWORD
    exec "$@"
  ' sh "$sqlcmd_path" -S localhost -U sa -C -b -Q "$query"
}

run_sqlcmd "BACKUP DATABASE [${database}] TO DISK = N'${container_path}' WITH COPY_ONLY, CHECKSUM, INIT;"
docker cp "${container}:${container_path}" "${backup_root}/${backup_name}"
chmod 600 "${backup_root}/${backup_name}"
run_sqlcmd "RESTORE VERIFYONLY FROM DISK = N'${container_path}' WITH CHECKSUM;"
docker exec "$container" rm -f "$container_path"
unset sa_password
printf 'Database backup created and verified: %s/%s\n' "$backup_root" "$backup_name"
