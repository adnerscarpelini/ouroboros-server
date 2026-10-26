#!/usr/bin/env bash
# Transfere o schema identity do banco original da instância para o banco próprio do serviço (ouroboros_identity),
# provisionado pelo scripts/database/provision.sql. Procedimento único para todo o Identity, em etapas explícitas:
#
#   freeze           inicia a janela sem escrita na origem (somente leitura por padrão; exige as APIs paradas)
#   backup           backup completo da origem (formato custom), restaurado em um banco de verificação e comparado;
#                    imprime o <id> da transferência; os demais comandos recebem esse <id>
#   copy       <id>  restaura o schema identity do backup verificado no destino e converge os privilégios
#   validate   <id>  compara origem e destino (contagens, identificadores públicos, hashes, sessões, tokens,
#                    parâmetros, revisões, histórico, estrutura) e grava a linha de base das linhas copiadas
#   cutover    <id>  repete a validação, confere os privilégios de runtime e libera a troca da connection string
#   rollback   <id>  avalia o retorno: sem novas escritas no destino, basta voltar a connection string; com novas
#                    escritas, exporta as diferenças (preservadas em disco) para reconciliar
#   reconcile  <id>  aplica na origem as escritas feitas no destino depois da troca (recusa conflitos)
#   unfreeze         encerra a janela sem escrita da origem
#   help             mostra esta ajuda
#
# Nada é excluído: a origem e o destino permanecem; só o banco de verificação criado pelo próprio backup é removido.
# Executar por comando explícito, com a credencial administrativa da instância (veja docs/development.md):
#   docker compose --profile transfer run --rm transfer-identity <comando> [id]
#
# Variáveis: PGHOST/PGPORT/PGUSER/PGPASSWORD (administrativa), SOURCE_DATABASE (padrão POSTGRES_DB), TARGET_DATABASE
# (padrão ouroboros_identity), TARGET_OWNER (padrão identity_migrator), TRANSFER_DIR (padrão /backups),
# FORCE_TERMINATE=1 (encerra sessões ativas na janela), e as senhas por serviço do provisionamento (copy).

set -euo pipefail
# Backups, linhas de base e deltas contêm hashes de senha e de token: só o usuário que executa lê.
umask 077

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCE_DATABASE="${SOURCE_DATABASE:-${POSTGRES_DB:-}}"
TARGET_DATABASE="${TARGET_DATABASE:-ouroboros_identity}"
TARGET_OWNER="${TARGET_OWNER:-identity_migrator}"
TRANSFER_DIR="${TRANSFER_DIR:-/backups}"
# Ordem das chaves estrangeiras: pais antes dos filhos.
TABLES=(parameters parameter_history users sessions refresh_tokens email_tokens)

fail() { echo "ERRO: $*" >&2; exit 1; }
info() { echo "$*"; }

[ -n "$SOURCE_DATABASE" ] || fail "defina SOURCE_DATABASE (ou POSTGRES_DB)."
[ "$SOURCE_DATABASE" != "$TARGET_DATABASE" ] || fail "origem e destino são o mesmo banco."

db() { local database="$1"; shift; psql -X -q -v ON_ERROR_STOP=1 -d "$database" "$@"; }
scalar() { db "$1" -At -c "$2"; }
digest() { db "$1" -f "$SCRIPT_DIR/identity-digest.sql"; }

require_id() { [ -n "${1:-}" ] || fail "informe o id da transferência (impresso pelo backup)."; DIR="$TRANSFER_DIR/identity-$1"; ID="$1"; [ -d "$DIR" ] || fail "transferência $1 não encontrada em $TRANSFER_DIR."; }

database_exists() { [ "$(scalar postgres "SELECT count(*) FROM pg_database WHERE datname = '$1'")" = "1" ]; }

is_frozen() {
  [ "$(scalar postgres "SELECT count(*) FROM pg_db_role_setting s JOIN pg_database d ON d.oid = s.setdatabase WHERE d.datname = '$1' AND s.setrole = 0 AND s.setconfig::text LIKE '%default_transaction_read_only=on%'")" -gt 0 ]
}

other_sessions() { scalar postgres "SELECT count(*) FROM pg_stat_activity WHERE datname = '$1' AND pid <> pg_backend_pid()"; }

ensure_no_sessions() {
  local database="$1" count
  count="$(other_sessions "$database")"
  if [ "$count" -gt 0 ]; then
    [ "${FORCE_TERMINATE:-0}" = "1" ] || fail "há $count sessão(ões) ativa(s) em $database. Pare as APIs e os workers da janela, ou use FORCE_TERMINATE=1 para encerrá-las."
    db postgres -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$database' AND pid <> pg_backend_pid()" >/dev/null
    sleep 1
    [ "$(other_sessions "$database")" -eq 0 ] || fail "sessões em $database continuam ativas."
  fi
}

# Linha por linha copiada: tabela, id e md5 da linha. É a linha de base para reconhecer escritas posteriores.
baseline_sql() {
  local first=1 table
  for table in "${TABLES[@]}"; do
    [ "$first" = 1 ] || printf ' UNION ALL '
    printf "SELECT '%s', id, md5(t::text) FROM identity.%s t" "$table" "$table"
    first=0
  done
}

# Remove apenas o banco de verificação criado pela própria execução do backup, inclusive quando ela falha no meio.
CHECK_DATABASE=""
drop_check_database() {
  if [ -n "$CHECK_DATABASE" ]; then
    echo "O backup não pôde ser verificado. A origem continua na janela sem escrita: corrija a causa e refaça o backup, ou encerre a janela com unfreeze." >&2
    db postgres -c "DROP DATABASE IF EXISTS \"$CHECK_DATABASE\"" >/dev/null 2>&1 || true
    CHECK_DATABASE=""
  fi
}

cmd_freeze() {
  database_exists "$SOURCE_DATABASE" || fail "origem $SOURCE_DATABASE não existe."
  is_frozen "$SOURCE_DATABASE" && fail "$SOURCE_DATABASE já está na janela sem escrita."
  ensure_no_sessions "$SOURCE_DATABASE"
  db postgres -c "ALTER DATABASE \"$SOURCE_DATABASE\" SET default_transaction_read_only = on"
  if [ "$(other_sessions "$SOURCE_DATABASE")" -gt 0 ]; then
    db postgres -c "ALTER DATABASE \"$SOURCE_DATABASE\" RESET default_transaction_read_only"
    fail "uma sessão conectou durante o congelamento; a janela foi desfeita. Pare as APIs e repita."
  fi
  info "Janela sem escrita iniciada em $SOURCE_DATABASE (somente leitura para novas sessões, sem sessões ativas)."
}

cmd_unfreeze() {
  db postgres -c "ALTER DATABASE \"$SOURCE_DATABASE\" RESET default_transaction_read_only"
  info "Janela sem escrita encerrada em $SOURCE_DATABASE."
}

cmd_backup() {
  is_frozen "$SOURCE_DATABASE" || fail "inicie a janela sem escrita antes do backup (freeze)."
  ID="$(date -u +%Y%m%dT%H%M%SZ)"
  DIR="$TRANSFER_DIR/identity-$ID"
  mkdir -p "$DIR"
  pg_dump --format=custom --no-owner --no-acl --dbname="$SOURCE_DATABASE" --file="$DIR/source.dump"
  digest "$SOURCE_DATABASE" > "$DIR/source.digest"

  # O backup só vale se for restaurado e passar pela mesma validação.
  local check="identity_transfer_check_$(echo "$ID" | tr 'A-Z' 'a-z')"
  db postgres -c "CREATE DATABASE \"$check\""
  CHECK_DATABASE="$check"
  trap drop_check_database EXIT
  pg_restore --no-owner --no-acl --exit-on-error --dbname="$check" "$DIR/source.dump"
  digest "$check" > "$DIR/restore-check.digest"
  drop_check_database
  trap - EXIT
  if ! diff -u "$DIR/source.digest" "$DIR/restore-check.digest"; then
    fail "o backup restaurado difere da origem. Nada foi copiado. A origem segue na janela sem escrita; corrija a causa e refaça o backup."
  fi
  info "Backup verificado: $DIR/source.dump (restaurado em banco de verificação e idêntico à origem)."
  info "Transferência: $ID"
}

cmd_copy() {
  require_id "${1:-}"
  # A cópia converge os privilégios com o provisionamento, que exige as senhas por serviço.
  : "${IDENTITY_RUNTIME_PASSWORD:?defina IDENTITY_RUNTIME_PASSWORD}"
  : "${IDENTITY_MIGRATION_PASSWORD:?defina IDENTITY_MIGRATION_PASSWORD}"
  : "${NOTIFICATIONS_RUNTIME_PASSWORD:?defina NOTIFICATIONS_RUNTIME_PASSWORD}"
  : "${NOTIFICATIONS_MIGRATION_PASSWORD:?defina NOTIFICATIONS_MIGRATION_PASSWORD}"
  [ "$IDENTITY_RUNTIME_PASSWORD" != "$IDENTITY_MIGRATION_PASSWORD" ] || fail "as senhas de runtime e de migration do Identity devem ser distintas; nada foi copiado."
  [ "$NOTIFICATIONS_RUNTIME_PASSWORD" != "$NOTIFICATIONS_MIGRATION_PASSWORD" ] || fail "as senhas de runtime e de migration do Notifications devem ser distintas; nada foi copiado."
  is_frozen "$SOURCE_DATABASE" || fail "a origem não está na janela sem escrita."
  [ -f "$DIR/source.dump" ] || fail "backup ausente."
  digest "$SOURCE_DATABASE" | diff -q - "$DIR/source.digest" >/dev/null || fail "a origem mudou depois do backup; refaça o backup."
  database_exists "$TARGET_DATABASE" || fail "destino $TARGET_DATABASE não existe; execute o provisionamento."
  [ "$(scalar "$TARGET_DATABASE" "SELECT count(*) FROM pg_namespace WHERE nspname = 'identity'")" = "0" ] \
    || fail "o destino já tem o schema identity; nada foi alterado. A cópia só roda em destino vazio. Para repetir depois de uma validação que falhou, avalie o destino e, só então, esvazie-o: psql -d $TARGET_DATABASE -c 'DROP SCHEMA identity CASCADE'."

  # Objetos criados com o papel de migration: ele os possui e os privilégios padrão concedem DML ao runtime.
  # O pg_restore --schema não cria o próprio schema, então ele nasce aqui, com o mesmo papel.
  db "$TARGET_DATABASE" -c "SET ROLE \"$TARGET_OWNER\"; CREATE SCHEMA identity"
  if ! pg_restore --schema=identity --role="$TARGET_OWNER" --no-owner --no-acl --exit-on-error --dbname="$TARGET_DATABASE" "$DIR/source.dump"     || ! psql -X -q -v ON_ERROR_STOP=1 -d postgres -f "$SCRIPT_DIR/provision.sql" >/dev/null; then
    # O schema foi criado por esta cópia (o destino estava vazio): desfazê-lo permite repetir sem intervenção manual.
    db "$TARGET_DATABASE" -c "DROP SCHEMA IF EXISTS identity CASCADE" >/dev/null || true
    fail "a cópia falhou e o schema identity parcial foi removido do destino (que estava vazio); a origem e o backup estão intactos."
  fi
  info "Schema identity copiado para $TARGET_DATABASE e privilégios convergidos."
  cmd_validate "$ID"
}

cmd_validate() {
  require_id "${1:-}"
  digest "$SOURCE_DATABASE" > "$DIR/validate-source.digest"
  digest "$TARGET_DATABASE" > "$DIR/validate-target.digest"
  if ! diff -u "$DIR/validate-source.digest" "$DIR/validate-target.digest"; then
    if [ -f "$DIR/cutover-ready" ]; then
      fail "o destino difere da origem (novas escritas depois da troca?). Para voltar, use rollback; a origem está intacta."
    fi
    rm -f "$DIR/validated"
    fail "validação falhou: o destino difere da origem. NÃO troque a connection string; a origem está intacta."
  fi
  info "Validação: origem e destino idênticos em $(wc -l < "$DIR/validate-target.digest") facetas (contagens, identificadores públicos, hashes, sessões, tokens, parâmetros, revisões, histórico e estrutura)."
  if [ ! -f "$DIR/baseline.tsv" ]; then
    db "$TARGET_DATABASE" -At -F $'\t' -c "$(baseline_sql)" > "$DIR/baseline.tsv"
  fi
  date -u +%Y-%m-%dT%H:%M:%SZ > "$DIR/validated"
}

cmd_cutover() {
  require_id "${1:-}"
  cmd_validate "$ID"
  : "${IDENTITY_RUNTIME_PASSWORD:?defina IDENTITY_RUNTIME_PASSWORD}"
  : "${IDENTITY_MIGRATION_PASSWORD:?defina IDENTITY_MIGRATION_PASSWORD}"
  local host="${PGHOST:-}" args=() output

  [ -z "$host" ] || args=(-h "$host")
  # A role de runtime lê e escreve dados no destino, mas não altera estrutura nem alcança o banco original.
  PGPASSWORD="$IDENTITY_RUNTIME_PASSWORD" psql -X -q -At "${args[@]}" -U identity_runtime -d "$TARGET_DATABASE" -c "SELECT count(*) FROM identity.users" >/dev/null \
    || fail "a role de runtime não lê o destino; a troca não foi liberada."
  if PGPASSWORD="$IDENTITY_RUNTIME_PASSWORD" psql -X -q "${args[@]}" -U identity_runtime -d "$TARGET_DATABASE" -c "CREATE TABLE identity.cutover_probe (id int)" >/dev/null 2>&1; then
    fail "a role de runtime consegue executar DDL no destino; a troca não foi liberada (revise o provisionamento)."
  fi
  if PGPASSWORD="$IDENTITY_RUNTIME_PASSWORD" psql -X -q "${args[@]}" -U identity_runtime -d "$SOURCE_DATABASE" -c "SELECT 1" >/dev/null 2>&1; then
    fail "a role de runtime alcança o banco original; a troca não foi liberada (revise o provisionamento)."
  fi
  if PGPASSWORD="$IDENTITY_RUNTIME_PASSWORD" psql -X -q "${args[@]}" -U identity_runtime -d "$TARGET_DATABASE" -c "CREATE TABLE public.cutover_probe (id int)" >/dev/null 2>&1; then
    fail "a role de runtime consegue criar objetos no schema public do destino; a troca não foi liberada (revise o provisionamento)."
  fi
  if PGPASSWORD="$IDENTITY_RUNTIME_PASSWORD" psql -X -q "${args[@]}" -U identity_runtime -d ouroboros_notifications -c "SELECT 1" >/dev/null 2>&1; then
    fail "a role de runtime alcança o banco do Notifications; a troca não foi liberada (revise o provisionamento)."
  fi
  PGPASSWORD="$IDENTITY_MIGRATION_PASSWORD" psql -X -q -At "${args[@]}" -U identity_migrator -d "$TARGET_DATABASE" -c "SELECT 1" >/dev/null \
    || fail "a role de migration não conecta ao destino; a troca não foi liberada."

  date -u +%Y-%m-%dT%H:%M:%SZ > "$DIR/cutover-ready"
  output="Troca liberada. Com as APIs de Identity paradas, defina a connection string própria (role de runtime) e inicie-as:
  ConnectionStrings:Identity = Host=<host>;Port=<porta>;Database=$TARGET_DATABASE;Username=identity_runtime;Password=<IDENTITY_RUNTIME_PASSWORD>
Migrations só por comando explícito, com a role de migration:
  dotnet ef database update --connection \"Host=<host>;Port=<porta>;Database=$TARGET_DATABASE;Username=identity_migrator;Password=<IDENTITY_MIGRATION_PASSWORD>\" ...
Senhas com ; ou aspas na connection string exigem aspas no valor (formato do Npgsql).
A origem permanece intacta e somente leitura. Para voltar: 1) pare as APIs de Identity; 2) rollback $ID (diz se houve novas escritas);
3) se houve, reconcile $ID; 4) remova ConnectionStrings:Identity (a origem volta a valer); 5) unfreeze."
  info "$output"
}

# Linhas do destino que não estão na linha de base (novas ou alteradas) e linhas da linha de base ausentes.
cmd_rollback() {
  require_id "${1:-}"
  [ -f "$DIR/baseline.tsv" ] || fail "a transferência não foi validada; não há linha de base."
  ensure_matching_migrations
  ensure_no_sessions "$TARGET_DATABASE"
  local delta="$DIR/reconcile" table script total_delta=0 total_missing=0 count
  rm -rf "$delta"
  rm -f "$DIR/reconciled"
  mkdir -p "$delta"

  script="CREATE TEMP TABLE baseline (tbl text, id bigint, h text);
\\copy baseline FROM '$DIR/baseline.tsv'
"
  for table in "${TABLES[@]}"; do
    script+="\\copy (SELECT x.* FROM identity.$table x WHERE NOT EXISTS (SELECT 1 FROM baseline b WHERE b.tbl = '$table' AND b.id = x.id AND b.h = md5(x::text)) ORDER BY x.id) TO '$delta/$table.csv' WITH (FORMAT csv, HEADER true)
SELECT '$table', 'delta', count(*) FROM identity.$table x WHERE NOT EXISTS (SELECT 1 FROM baseline b WHERE b.tbl = '$table' AND b.id = x.id AND b.h = md5(x::text));
SELECT '$table', 'ausentes', count(*) FROM baseline b WHERE b.tbl = '$table' AND NOT EXISTS (SELECT 1 FROM identity.$table x WHERE x.id = b.id);
"
  done
  printf '%s' "$script" | db "$TARGET_DATABASE" -At -F '|' > "$delta/report.txt"
  cat "$delta/report.txt"
  total_delta="$(awk -F'|' '$2 == "delta" { s += $3 } END { print s + 0 }' "$delta/report.txt")"
  total_missing="$(awk -F'|' '$2 == "ausentes" { s += $3 } END { print s + 0 }' "$delta/report.txt")"
  [ "$total_missing" -eq 0 ] || fail "linhas copiadas deixaram de existir no destino ($total_missing); a reconciliação automática não as remove da origem. Avalie $delta/report.txt."

  if [ "$total_delta" -eq 0 ]; then
    : > "$DIR/rollback-clean"
    info "Sem novas escritas no destino. Basta remover ConnectionStrings:Identity (a origem volta a valer) e executar unfreeze."
  else
    rm -f "$DIR/rollback-clean"
    info "$total_delta linha(s) novas ou alteradas no destino foram exportadas e preservadas em $delta (o destino não foi alterado)."
    info "Execute reconcile para aplicá-las na origem antes de voltar a connection string."
  fi
}

# Posição de cada sequência (0 quando nunca foi usada), "nome|valor".
sequence_positions() {
  scalar "$1" "SELECT sequencename || '|' || coalesce(last_value, 0) FROM pg_sequences WHERE schemaname = 'identity' ORDER BY sequencename"
}

ensure_matching_migrations() {
  [ "$(scalar "$SOURCE_DATABASE" "SELECT count(*) FROM pg_class WHERE relnamespace = 'identity'::regnamespace AND relname = '__EFMigrationsHistory'")" = "1" ] || fail "origem sem histórico de migrations."
  diff -q <(scalar "$SOURCE_DATABASE" "SELECT string_agg(\"MigrationId\", ',' ORDER BY \"MigrationId\") FROM identity.\"__EFMigrationsHistory\"") \
          <(scalar "$TARGET_DATABASE" "SELECT string_agg(\"MigrationId\", ',' ORDER BY \"MigrationId\") FROM identity.\"__EFMigrationsHistory\"") >/dev/null \
    || fail "o destino recebeu migrations depois da transferência; reconcilie manualmente antes de voltar a connection string."
}

cmd_reconcile() {
  require_id "${1:-}"
  ensure_matching_migrations
  local delta="$DIR/reconcile" table columns script drift target_value was_frozen=0
  [ -f "$delta/report.txt" ] || fail "execute rollback antes de reconciliar."
  if [ -f "$DIR/reconciled" ] && [ "$DIR/reconciled" -nt "$delta/report.txt" ]; then
    info "As escritas do destino já foram aplicadas na origem (reconciliação anterior); nada a fazer. Depois de reconciliar não há um segundo ciclo de rollback e reconcile: a origem já difere da linha de base; para novas escritas no destino, avalie manualmente com os arquivos preservados."
    return 0
  fi
  ensure_no_sessions "$SOURCE_DATABASE"

  # Conflito: a origem mudou depois da linha de base (alguém escreveu nela durante ou depois da troca).
  # A checagem só lê, então a janela sem escrita continua até haver certeza de que a reconciliação pode ser aplicada.
  script="CREATE TEMP TABLE baseline (tbl text, id bigint, h text);
\copy baseline FROM '$DIR/baseline.tsv'
"
  for table in "${TABLES[@]}"; do
    script+="SELECT '$table', count(*) FROM identity.$table x WHERE NOT EXISTS (SELECT 1 FROM baseline b WHERE b.tbl = '$table' AND b.id = x.id AND b.h = md5(x::text));
"
  done
  # A tabela temporária da checagem exige uma sessão que aceite escrita; nada além dela é escrito na origem.
  drift="$(printf '%s' "$script" | PGOPTIONS="-c default_transaction_read_only=off" db "$SOURCE_DATABASE" -At -F '|' | awk -F'|' '{ s += $2 } END { print s + 0 }')"
  [ "$drift" -eq 0 ] || fail "a origem recebeu $drift escrita(s) depois da linha de base: conflito. Nada foi aplicado; resolva manualmente com os arquivos preservados em $delta."

  script="BEGIN;
"
  for table in "${TABLES[@]}"; do
    columns="$(scalar "$SOURCE_DATABASE" "SELECT string_agg(format('%I = EXCLUDED.%I', column_name, column_name), ', ' ORDER BY ordinal_position) FROM information_schema.columns WHERE table_schema = 'identity' AND table_name = '$table' AND column_name <> 'id'")"
    # A sequência da origem precisa passar do maior id e da posição do destino, mesmo que nunca tenha sido usada.
    target_value="$(scalar "$TARGET_DATABASE" "SELECT coalesce((SELECT last_value FROM pg_sequences WHERE schemaname = 'identity' AND sequencename = split_part(pg_get_serial_sequence('identity.$table', 'id'), '.', 2)), 0)")"
    script+="CREATE TEMP TABLE d_$table (LIKE identity.$table INCLUDING DEFAULTS) ON COMMIT DROP;
\copy d_$table FROM '$delta/$table.csv' WITH (FORMAT csv, HEADER true)
INSERT INTO identity.$table OVERRIDING SYSTEM VALUE SELECT * FROM d_$table ON CONFLICT (id) DO UPDATE SET $columns;
DO \$\$
DECLARE sequence_name text := pg_get_serial_sequence('identity.$table', 'id'); wanted bigint; current bigint; called boolean;
BEGIN
  SELECT greatest(coalesce(max(id), 0), $target_value) INTO wanted FROM identity.$table;
  EXECUTE format('SELECT last_value, is_called FROM %s', sequence_name) INTO current, called;
  IF wanted > 0 AND (NOT called OR wanted > current) THEN PERFORM setval(sequence_name, wanted, true); END IF;
END \$\$;
"
  done
  script+="COMMIT;
"

  if is_frozen "$SOURCE_DATABASE"; then
    was_frozen=1
    cmd_unfreeze
  fi
  if ! printf '%s' "$script" | db "$SOURCE_DATABASE" >/dev/null; then
    [ "$was_frozen" = 0 ] || db postgres -c "ALTER DATABASE \"$SOURCE_DATABASE\" SET default_transaction_read_only = on"
    fail "a reconciliação falhou e nada foi aplicado (transação desfeita); a janela sem escrita da origem foi restabelecida. Avalie os arquivos preservados em $delta."
  fi

  # Depois de aplicar, a origem deve ser idêntica ao destino e suas sequências não podem estar atrás das do destino.
  digest "$SOURCE_DATABASE" | grep -v '^structure.sequences|' > "$DIR/reconcile-source.digest"
  digest "$TARGET_DATABASE" | grep -v '^structure.sequences|' > "$DIR/reconcile-target.digest"
  diff -u "$DIR/reconcile-source.digest" "$DIR/reconcile-target.digest" || fail "a origem reconciliada difere do destino; restaure do backup $DIR/source.dump se necessário."
  sequence_positions "$SOURCE_DATABASE" > "$DIR/reconcile-source.sequences"
  sequence_positions "$TARGET_DATABASE" > "$DIR/reconcile-target.sequences"
  awk -F'|' 'NR == FNR { target[$1] = $2 + 0; next } ($1 in target) && ($2 + 0) < target[$1] { print "sequência " $1 " na origem (" $2 ") atrás do destino (" target[$1] ")"; bad = 1 } END { exit bad }'     "$DIR/reconcile-target.sequences" "$DIR/reconcile-source.sequences"     || fail "alguma sequência da origem ficou atrás do destino; o próximo cadastro poderia repetir um id."
  date -u +%Y-%m-%dT%H:%M:%SZ > "$DIR/reconciled"
  info "Escritas do destino aplicadas na origem e verificadas. Agora remova ConnectionStrings:Identity para voltar à origem."
}

# A outbox só existe após a #85. Preservá-la em linha de base/rollback sem impedir transferir uma origem antiga.
case "${1:-}" in
  copy|validate|cutover|rollback|reconcile)
    if [ "$(scalar "$SOURCE_DATABASE" "SELECT to_regclass('identity.email_intents') IS NOT NULL")" = "t" ]; then
      TABLES+=(email_intents)
    fi ;;
esac

case "${1:-}" in
  freeze) cmd_freeze "${2:-}" ;;
  unfreeze) cmd_unfreeze ;;
  backup) cmd_backup ;;
  copy) cmd_copy "${2:-}" ;;
  validate) cmd_validate "${2:-}" ;;
  cutover) cmd_cutover "${2:-}" ;;
  rollback) cmd_rollback "${2:-}" ;;
  reconcile) cmd_reconcile "${2:-}" ;;
  help|-h|--help) sed -n '2,/^$/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' ;;
  *) fail "uso: transfer-identity.sh {freeze|backup|copy|validate|cutover|rollback|reconcile|unfreeze|help} [id]" ;;
esac
