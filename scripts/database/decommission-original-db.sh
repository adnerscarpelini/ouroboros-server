#!/usr/bin/env bash
# Inventaria, faz o backup final verificável, rotaciona/revoga a credencial antiga e desativa de forma
# controlada (sem exclusão) o banco original da instância após a transferência do Identity (#82) e a
# retirada dos hosts agregadores (#96).
#
# Comandos:
#   inventory                  inventaria schemas, tabelas, históricos de migrations (__EFMigrationsHistory)
#                              e dados retidos apenas na origem que não foram para o banco do Identity
#   verify-unused              confirma que nenhum runtime nem role de serviço usa ou alcança o banco original
#   final-backup               exige origem sem sessões ativas, congela escrita, gera dump completo custom,
#                              restaura em banco temporário de verificação e compara o digest integral;
#                              imprime o <id> do pacote de desativação
#   rotate-admin-credential    rotaciona a senha ampla da credencial administrativa (NEW_ADMIN_PASSWORD) com
#                              session-safety.sql (sem vazar nos logs) e, se LEGACY_ROLE for informada,
#                              revoga LOGIN da role legada sem afetar identity_* nem notifications_*
#   deactivate <id>            desativa conexões ao banco original (ALLOW_CONNECTIONS false, CONNECTION LIMIT 0,
#                              somente leitura) sem apagar banco, schemas, backups ou volumes; exige aprovação
#                              explícita (CONFIRM_ORIGINAL_DB_DEACTIVATION=DEACTIVATE:<banco> e RETENTION_DAYS),
#                              backup final verificado (<id>) e confirmação de desuso (verify-unused)
#   reactivate <id>            reabilita conexões em modo somente leitura ao banco original desativado caso
#                              seja necessária inspeção ou recuperação autorizada dentro da janela de retenção
#   help                       mostra esta ajuda
#
# Nenhuma exclusão de banco de dados (DROP DATABASE do original), schema ou volume é executada por este script.

set -euo pipefail
umask 077

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCE_DATABASE="${SOURCE_DATABASE:-${POSTGRES_DB:-}}"
TARGET_DATABASE="${TARGET_DATABASE:-ouroboros_identity}"
NOTIFICATIONS_DATABASE="${NOTIFICATIONS_DATABASE:-ouroboros_notifications}"
DECOMMISSION_DIR="${DECOMMISSION_DIR:-/backups}"

fail() { echo "ERRO: $*" >&2; exit 1; }
info() { echo "$*"; }

[ -n "$SOURCE_DATABASE" ] || fail "defina SOURCE_DATABASE (ou POSTGRES_DB)."
[ "$SOURCE_DATABASE" != "$TARGET_DATABASE" ] || fail "origem e destino do Identity são o mesmo banco."
[ "$SOURCE_DATABASE" != "$NOTIFICATIONS_DATABASE" ] || fail "origem e banco do Notifications são o mesmo banco."
[ "$SOURCE_DATABASE" != "postgres" ] || fail "o banco de manutenção postgres não pode ser alvo de desativação."
[ "$SOURCE_DATABASE" != "template0" ] && [ "$SOURCE_DATABASE" != "template1" ] || fail "bancos template não podem ser alvo de desativação."
# Nomes entram em instruções SQL: aceita apenas identificadores simples.
valid_identifier() { [[ "$1" =~ ^[A-Za-z_][A-Za-z0-9_]{0,62}$ ]]; }
valid_identifier "$SOURCE_DATABASE" || fail "SOURCE_DATABASE inválido: use apenas letras, dígitos e _."
valid_identifier "$TARGET_DATABASE" || fail "TARGET_DATABASE inválido."
valid_identifier "$NOTIFICATIONS_DATABASE" || fail "NOTIFICATIONS_DATABASE inválido."
[ -z "${LEGACY_ROLE:-}" ] || valid_identifier "$LEGACY_ROLE" || fail "LEGACY_ROLE inválida: use apenas letras, dígitos e _."
[ -z "${ADMIN_ROLE:-}" ] || valid_identifier "$ADMIN_ROLE" || fail "ADMIN_ROLE inválida: use apenas letras, dígitos e _."

db() { local database="$1"; shift; psql -X -q -v ON_ERROR_STOP=1 -d "$database" "$@"; }
scalar() { db "$1" -At -c "$2"; }

require_id() {
  [ -n "${1:-}" ] || fail "informe o id do backup final (impresso por final-backup)."
  ID="$1"
  [[ "$ID" =~ ^[0-9]{8}T[0-9]{6}Z-[A-Za-z0-9]{6}$ ]] || fail "id do pacote inválido."
  DIR="$DECOMMISSION_DIR/original-$ID"
  [ -d "$DIR" ] || fail "pacote original-$ID não encontrado em $DECOMMISSION_DIR."
  [ -f "$DIR/source-database" ] && [ "$(cat "$DIR/source-database")" = "$SOURCE_DATABASE" ] \
    || fail "o pacote não pertence ao banco original $SOURCE_DATABASE."
}

database_exists() {
  [ "$(scalar postgres "SELECT count(*) FROM pg_database WHERE datname = '$1'")" = "1" ]
}

allows_connections() {
  [ "$(scalar postgres "SELECT datallowconn FROM pg_database WHERE datname = '$1'")" = "t" ]
}

is_frozen() {
  [ "$(scalar postgres "SELECT count(*) FROM pg_db_role_setting s JOIN pg_database d ON d.oid = s.setdatabase WHERE d.datname = '$1' AND s.setrole = 0 AND s.setconfig::text LIKE '%default_transaction_read_only=on%'")" -gt 0 ]
}

other_sessions() {
  scalar postgres "SELECT count(*) FROM pg_stat_activity WHERE datname = '$1' AND pid <> pg_backend_pid()"
}

full_digest() {
  local database="$1"
  db "$database" -At <<'EOSQL'
SELECT 'schema|' || nspname
FROM pg_namespace
WHERE nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
  AND nspname NOT LIKE 'pg_temp_%'
  AND nspname NOT LIKE 'pg_toast_temp_%'
ORDER BY nspname;

SELECT 'column|' || table_schema || '.' || table_name || '.' || column_name || '|' || data_type || '|' || is_nullable
FROM information_schema.columns
WHERE table_schema NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
  AND table_schema NOT LIKE 'pg_temp_%'
  AND table_schema NOT LIKE 'pg_toast_temp_%'
ORDER BY table_schema, table_name, ordinal_position;

SELECT 'sequence|' || schemaname || '.' || sequencename || '|' || coalesce(last_value, 0)
FROM pg_sequences
WHERE schemaname NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
ORDER BY schemaname, sequencename;

SELECT format(
  'SELECT %L || ''|'' || count(*) || ''|'' || coalesce(md5(string_agg(md5(row_txt), '','' ORDER BY row_txt)), ''empty'') FROM (SELECT t::text AS row_txt FROM %I.%I t) s;',
  'table_data|' || n.nspname || '.' || c.relname,
  n.nspname,
  c.relname
)
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind = 'r'
  AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
  AND n.nspname NOT LIKE 'pg_temp_%'
  AND n.nspname NOT LIKE 'pg_toast_temp_%'
ORDER BY n.nspname, c.relname
\gexec
EOSQL
}

CHECK_DATABASE=""
drop_check_database() {
  if [ -n "$CHECK_DATABASE" ]; then
    db postgres -c "DROP DATABASE IF EXISTS \"$CHECK_DATABASE\"" >/dev/null 2>&1 || true
    CHECK_DATABASE=""
  fi
}

cmd_inventory() {
  database_exists "$SOURCE_DATABASE" || fail "banco original $SOURCE_DATABASE não existe."
  allows_connections "$SOURCE_DATABASE" || fail "banco original $SOURCE_DATABASE está com conexões desativadas (use reactivate para inspecionar)."
  database_exists "$TARGET_DATABASE" || fail "banco $TARGET_DATABASE não existe."

  local read_only="false"
  if is_frozen "$SOURCE_DATABASE"; then
    read_only="true"
  fi

  info "=== INVENTÁRIO DO BANCO ORIGINAL ($SOURCE_DATABASE) ==="
  info "database|$SOURCE_DATABASE|read_only=$read_only|active_sessions=$(other_sessions "$SOURCE_DATABASE")"

  local schemas
  schemas="$(scalar "$SOURCE_DATABASE" "SELECT string_agg(nspname, ',' ORDER BY nspname) FROM pg_namespace WHERE nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast') AND nspname NOT LIKE 'pg_temp_%' AND nspname NOT LIKE 'pg_toast_temp_%'")"
  info "schemas|$schemas"

  local target_tables
  target_tables="$(scalar "$TARGET_DATABASE" "SELECT coalesce(string_agg(n.nspname || '.' || c.relname, ',' ORDER BY n.nspname, c.relname), '') FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind = 'r' AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast')")"

  local rows
  rows="$(db "$SOURCE_DATABASE" -At <<'EOSQL'
SELECT format(
  'SELECT %L || ''|'' || %L || ''|'' || count(*) FROM %I.%I;',
  n.nspname,
  c.relname,
  n.nspname,
  c.relname
)
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind = 'r'
  AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
  AND n.nspname NOT LIKE 'pg_temp_%'
  AND n.nspname NOT LIKE 'pg_toast_temp_%'
ORDER BY n.nspname, c.relname
\gexec
EOSQL
)"

  while IFS='|' read -r schema_name table_name row_count; do
    [ -n "$schema_name" ] || continue
    local qualified="${schema_name}.${table_name}"
    local category="retained_only_in_source"
    if [ "$table_name" = "__EFMigrationsHistory" ]; then
      local migrations
      migrations="$(scalar "$SOURCE_DATABASE" "SELECT coalesce(string_agg(\"MigrationId\", ',' ORDER BY \"MigrationId\"), 'none') FROM \"${schema_name}\".\"__EFMigrationsHistory\"")"
      if [[ ",$target_tables," == *",$qualified,"* ]]; then
        category="migration_history_transferred"
      else
        category="migration_history_retired_in_source"
      fi
      info "table|${schema_name}|${table_name}|${category}|rows=${row_count}|migrations=${migrations}"
    else
      if [[ ",$target_tables," == *",$qualified,"* ]]; then
        category="transferred_to_target"
      fi
      info "table|${schema_name}|${table_name}|${category}|rows=${row_count}"
    fi
  done <<< "$rows"
}

cmd_verify_unused() {
  database_exists "$SOURCE_DATABASE" || fail "banco original $SOURCE_DATABASE não existe."
  database_exists "$TARGET_DATABASE" || fail "banco $TARGET_DATABASE não existe."
  database_exists "$NOTIFICATIONS_DATABASE" || fail "banco $NOTIFICATIONS_DATABASE não existe."

  local active
  active="$(other_sessions "$SOURCE_DATABASE")"
  [ "$active" -eq 0 ] || fail "há $active sessão(ões) ativa(s) conectadas ao banco original $SOURCE_DATABASE."

  local public_connect
  public_connect="$(scalar postgres "SELECT has_database_privilege('public', '$SOURCE_DATABASE', 'CONNECT')")"
  [ "$public_connect" = "f" ] || fail "PUBLIC ainda possui privilégio CONNECT em $SOURCE_DATABASE (execute provision.sql)."

  local role
  for role in identity_runtime identity_migrator notifications_runtime notifications_migrator; do
    local role_exists
    role_exists="$(scalar postgres "SELECT count(*) FROM pg_roles WHERE rolname = '$role'")"
    [ "$role_exists" = "1" ] || fail "role de serviço $role não existe."

    local can_connect
    can_connect="$(scalar postgres "SELECT has_database_privilege('$role', '$SOURCE_DATABASE', 'CONNECT')")"
    [ "$can_connect" = "f" ] || fail "a role de serviço $role ainda possui privilégio CONNECT no banco original $SOURCE_DATABASE."
  done

  local target_has_identity
  target_has_identity="$(scalar "$TARGET_DATABASE" "SELECT count(*) FROM pg_namespace WHERE nspname = 'identity'")"
  [ "$target_has_identity" = "1" ] || fail "o banco próprio $TARGET_DATABASE ainda não contém o schema identity."

  info "Verificação concluída: zero sessões ativas em $SOURCE_DATABASE e nenhuma role de serviço (nem PUBLIC) possui acesso ao banco original."
}

cmd_final_backup() {
  cmd_verify_unused
  allows_connections "$SOURCE_DATABASE" || fail "o banco original $SOURCE_DATABASE já está com conexões desativadas."

  if ! is_frozen "$SOURCE_DATABASE"; then
    db postgres -c "ALTER DATABASE \"$SOURCE_DATABASE\" SET default_transaction_read_only = on"
    if [ "$(other_sessions "$SOURCE_DATABASE")" -gt 0 ]; then
      db postgres -c "ALTER DATABASE \"$SOURCE_DATABASE\" RESET default_transaction_read_only"
      fail "uma sessão conectou durante o congelamento de $SOURCE_DATABASE."
    fi
  fi

  mkdir -p "$DECOMMISSION_DIR"
  DIR="$(mktemp -d "$DECOMMISSION_DIR/original-$(date -u +%Y%m%dT%H%M%SZ)-XXXXXX")"
  ID="${DIR##*/original-}"
  printf '%s\n' "$SOURCE_DATABASE" > "$DIR/source-database"

  cmd_inventory > "$DIR/inventory.txt"
  pg_dump --format=custom --no-owner --no-acl --dbname="$SOURCE_DATABASE" --file="$DIR/final.dump"
  (cd "$DIR" && sha256sum "final.dump" > "final.dump.sha256")
  full_digest "$SOURCE_DATABASE" > "$DIR/source-inventory.digest"

  local check="original_decommission_check_$(echo "$ID" | tr 'A-Z' 'a-z')"
  db postgres -c "CREATE DATABASE \"$check\""
  CHECK_DATABASE="$check"
  trap drop_check_database EXIT
  pg_restore --no-owner --no-acl --exit-on-error --dbname="$check" "$DIR/final.dump"
  full_digest "$check" > "$DIR/restore-check.digest"
  drop_check_database
  trap - EXIT

  if ! diff -u "$DIR/source-inventory.digest" "$DIR/restore-check.digest"; then
    fail "o backup final restaurado difere do banco original $SOURCE_DATABASE."
  fi

  date -u +%Y-%m-%dT%H:%M:%SZ > "$DIR/final-backup-verified"
  info "Backup final verificado: $DIR/final.dump (restaurado e idêntico em todos os schemas e tabelas)."
  info "Pacote: $ID"
}

cmd_rotate_admin_credential() {
  : "${NEW_ADMIN_PASSWORD:?defina NEW_ADMIN_PASSWORD para rotacionar a credencial ampla}"
  local admin_role="${ADMIN_ROLE:-${PGUSER:-postgres}}"
  valid_identifier "$admin_role" || fail "ADMIN_ROLE inválida."
  case "$admin_role" in
    identity_runtime|identity_migrator|notifications_runtime|notifications_migrator)
      fail "ADMIN_ROLE não pode ser uma role ativa de serviço."
      ;;
  esac
  [ "$(scalar postgres "SELECT rolsuper FROM pg_roles WHERE rolname = '$admin_role'")" = "t" ] \
    || fail "ADMIN_ROLE deve identificar a credencial administrativa ampla da instância."
  cmd_verify_unused
  : "${IDENTITY_RUNTIME_PASSWORD:?defina IDENTITY_RUNTIME_PASSWORD para verificar o serviço}"
  : "${NOTIFICATIONS_RUNTIME_PASSWORD:?defina NOTIFICATIONS_RUNTIME_PASSWORD para verificar o serviço}"

  for svc_pw_var in IDENTITY_RUNTIME_PASSWORD IDENTITY_MIGRATION_PASSWORD NOTIFICATIONS_RUNTIME_PASSWORD NOTIFICATIONS_MIGRATION_PASSWORD; do
    if [ -n "${!svc_pw_var:-}" ] && [ "$NEW_ADMIN_PASSWORD" = "${!svc_pw_var}" ]; then
      fail "a nova senha administrativa não pode ser igual a $svc_pw_var."
    fi
  done

  if [ -n "${LEGACY_ROLE:-}" ]; then
    case "$LEGACY_ROLE" in
      "$admin_role"|postgres|identity_runtime|identity_migrator|notifications_runtime|notifications_migrator)
        fail "LEGACY_ROLE ($LEGACY_ROLE) não pode ser uma role ativa de serviço nem o superusuário da instância."
        ;;
    esac

    local legacy_exists
    legacy_exists="$(scalar postgres "SELECT count(*) FROM pg_roles WHERE rolname = '$LEGACY_ROLE'")"
    [ "$legacy_exists" = "1" ] || fail "role legada $LEGACY_ROLE não encontrada."

    db postgres -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE usename = '$LEGACY_ROLE' AND pid <> pg_backend_pid()" >/dev/null
    db postgres <<EOSQL
\set ON_ERROR_STOP on
\set QUIET on
\ir $SCRIPT_DIR/session-safety.sql
SELECT format('REVOKE CONNECT, TEMPORARY ON DATABASE %I FROM %I', datname, '$LEGACY_ROLE') FROM pg_database \gexec
ALTER ROLE "$LEGACY_ROLE" WITH NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD NULL;
EOSQL
    info "Credencial legada '$LEGACY_ROLE' revogada (NOLOGIN e sem acesso aos bancos)."
  fi

  ADMIN_ROLE="$admin_role" NEW_ADMIN_PASSWORD="$NEW_ADMIN_PASSWORD" psql -X -q -v ON_ERROR_STOP=1 -d postgres <<EOSQL
\set ON_ERROR_STOP on
\set QUIET on
\ir $SCRIPT_DIR/session-safety.sql
\unset target_role
\unset new_password
\getenv target_role ADMIN_ROLE
\getenv new_password NEW_ADMIN_PASSWORD
SELECT format('ALTER ROLE %I WITH PASSWORD %L', :'target_role', :'new_password') \gexec
EOSQL

  local host="${PGHOST:-127.0.0.1}"
  if [ -n "${IDENTITY_RUNTIME_PASSWORD:-}" ]; then
    PGPASSWORD="$IDENTITY_RUNTIME_PASSWORD" psql -X -q -At -h "$host" -U identity_runtime -d "$TARGET_DATABASE" -c "SELECT 1" >/dev/null \
      || fail "identity_runtime falhou ao conectar em $TARGET_DATABASE após rotação da credencial antiga."
  fi
  if [ -n "${NOTIFICATIONS_RUNTIME_PASSWORD:-}" ]; then
    PGPASSWORD="$NOTIFICATIONS_RUNTIME_PASSWORD" psql -X -q -At -h "$host" -U notifications_runtime -d "$NOTIFICATIONS_DATABASE" -c "SELECT 1" >/dev/null \
      || fail "notifications_runtime falhou ao conectar em $NOTIFICATIONS_DATABASE após rotação da credencial antiga."
  fi

  info "Credencial administrativa '$admin_role' rotacionada com sucesso sem afetar os serviços."
}

cmd_deactivate() {
  require_id "${1:-}"
  local expected_confirmation="DEACTIVATE:${SOURCE_DATABASE}"
  if [ "${CONFIRM_ORIGINAL_DB_DEACTIVATION:-}" != "$expected_confirmation" ]; then
    fail "a desativação exige aprovação explícita do dono do projeto: defina CONFIRM_ORIGINAL_DB_DEACTIVATION=$expected_confirmation."
  fi

  if ! [[ "${RETENTION_DAYS:-}" =~ ^[0-9]+$ ]] || [ "${RETENTION_DAYS:-0}" -lt 1 ]; then
    fail "defina o período de retenção aprovado em RETENTION_DAYS (inteiro >= 1 dia) antes de desativar o banco original."
  fi

  [ -f "$DIR/final-backup-verified" ] || fail "backup final ($ID) não foi verificado."
  [ -f "$DIR/final.dump" ] || fail "arquivo de backup final ($DIR/final.dump) ausente."
  [ -f "$DIR/final.dump.sha256" ] || fail "checksum ($DIR/final.dump.sha256) ausente."
  (cd "$DIR" && sha256sum -c "final.dump.sha256" >/dev/null) || fail "checksum do backup final falhou."

  cmd_verify_unused
  allows_connections "$SOURCE_DATABASE" || fail "o banco original $SOURCE_DATABASE já está desativado."
  is_frozen "$SOURCE_DATABASE" || fail "o banco original $SOURCE_DATABASE não está em modo somente leitura."

  full_digest "$SOURCE_DATABASE" > "$DIR/deactivate-precheck.digest"
  if ! diff -u "$DIR/source-inventory.digest" "$DIR/deactivate-precheck.digest"; then
    fail "o banco original $SOURCE_DATABASE sofreu alterações após o backup final; gere um novo final-backup."
  fi

  local timestamp
  timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  local dump_sha
  dump_sha="$(awk '{print $1}' "$DIR/final.dump.sha256")"

  db postgres -c "REVOKE CONNECT, TEMPORARY ON DATABASE \"$SOURCE_DATABASE\" FROM PUBLIC"
  db postgres -c "ALTER DATABASE \"$SOURCE_DATABASE\" SET default_transaction_read_only = on"
  db postgres -c "COMMENT ON DATABASE \"$SOURCE_DATABASE\" IS 'Desativado em $timestamp (pacote $ID, retencao ${RETENTION_DAYS}d, sha256 $dump_sha). Nao excluir sem aprovacao explicita.'"
  db postgres -c "ALTER DATABASE \"$SOURCE_DATABASE\" WITH ALLOW_CONNECTIONS false CONNECTION LIMIT 0"

  cat > "$DIR/deactivated.manifest" <<EOF
source_database=$SOURCE_DATABASE
package_id=$ID
deactivated_at=$timestamp
retention_days=$RETENTION_DAYS
final_dump=$DIR/final.dump
final_dump_sha256=$dump_sha
approval=$CONFIRM_ORIGINAL_DB_DEACTIVATION
EOF

  info "Banco original $SOURCE_DATABASE desativado com segurança (ALLOW_CONNECTIONS=false, CONNECTION LIMIT=0, somente leitura)."
  info "Nenhum dado, banco ou volume foi excluído. Manifesto gravado em $DIR/deactivated.manifest."
}

cmd_reactivate() {
  require_id "${1:-}"
  database_exists "$SOURCE_DATABASE" || fail "banco original $SOURCE_DATABASE não existe."

  db postgres -c "ALTER DATABASE \"$SOURCE_DATABASE\" WITH ALLOW_CONNECTIONS true CONNECTION LIMIT -1"
  db postgres -c "ALTER DATABASE \"$SOURCE_DATABASE\" SET default_transaction_read_only = on"
  date -u +%Y-%m-%dT%H:%M:%SZ > "$DIR/reactivated"
  info "Banco original $SOURCE_DATABASE reabilitado em modo somente leitura (default_transaction_read_only=on)."
}

case "${1:-}" in
  inventory) cmd_inventory ;;
  verify-unused) cmd_verify_unused ;;
  final-backup) cmd_final_backup ;;
  rotate-admin-credential) cmd_rotate_admin_credential ;;
  deactivate) cmd_deactivate "${2:-}" ;;
  reactivate) cmd_reactivate "${2:-}" ;;
  help|-h|--help) sed -n '2,/^$/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' ;;
  *) fail "uso: decommission-original-db.sh {inventory|verify-unused|final-backup|rotate-admin-credential|deactivate|reactivate|help} [id]" ;;
esac
