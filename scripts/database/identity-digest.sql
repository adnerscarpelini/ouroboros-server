-- Resumo comparável do schema identity: uma linha "faceta|quantidade|digest" por tabela e por aspecto que a
-- transferência precisa preservar. Executado, somente leitura, no banco de origem e no de destino; os dois
-- resultados devem ser idênticos. Cada digest é o md5 da concatenação ordenada das linhas completas (ou da coluna).

\pset format unaligned
\pset tuples_only on
\pset fieldsep '|'
SET default_transaction_read_only = on;

SELECT 'users', count(*), coalesce(md5(string_agg(t::text, E'\n' ORDER BY t.id)), 'vazio') FROM identity.users t
UNION ALL SELECT 'users.external_id', count(*), md5(coalesce(string_agg(external_id::text, ',' ORDER BY id), '')) FROM identity.users
UNION ALL SELECT 'users.password_hash', count(password_hash), md5(coalesce(string_agg(password_hash, ',' ORDER BY id), '')) FROM identity.users
UNION ALL SELECT 'sessions', count(*), coalesce(md5(string_agg(t::text, E'\n' ORDER BY t.id)), 'vazio') FROM identity.sessions t
UNION ALL SELECT 'sessions.external_id', count(*), md5(coalesce(string_agg(external_id::text, ',' ORDER BY id), '')) FROM identity.sessions
UNION ALL SELECT 'refresh_tokens', count(*), coalesce(md5(string_agg(t::text, E'\n' ORDER BY t.id)), 'vazio') FROM identity.refresh_tokens t
UNION ALL SELECT 'refresh_tokens.token_hash', count(token_hash), md5(coalesce(string_agg(encode(token_hash, 'hex'), ',' ORDER BY id), '')) FROM identity.refresh_tokens
UNION ALL SELECT 'email_tokens', count(*), coalesce(md5(string_agg(t::text, E'\n' ORDER BY t.id)), 'vazio') FROM identity.email_tokens t
UNION ALL SELECT 'email_tokens.token_hash', count(token_hash), md5(coalesce(string_agg(encode(token_hash, 'hex'), ',' ORDER BY id), '')) FROM identity.email_tokens
UNION ALL SELECT 'parameters', count(*), coalesce(md5(string_agg(t::text, E'\n' ORDER BY t.id)), 'vazio') FROM identity.parameters t
UNION ALL SELECT 'parameters.revision', count(*), md5(coalesce(string_agg(key || '@' || revision, ',' ORDER BY key), '')) FROM identity.parameters
UNION ALL SELECT 'parameter_history', count(*), coalesce(md5(string_agg(t::text, E'\n' ORDER BY t.id)), 'vazio') FROM identity.parameter_history t
UNION ALL SELECT 'parameter_history.revision', count(*), md5(coalesce(string_agg(parameter_key || '@' || revision, ',' ORDER BY parameter_key, revision), '')) FROM identity.parameter_history
UNION ALL SELECT 'migrations', count(*), md5(coalesce(string_agg(t::text, E'\n' ORDER BY t."MigrationId"), '')) FROM identity."__EFMigrationsHistory" t
UNION ALL SELECT 'structure.columns', count(*), md5(coalesce(string_agg(
    table_name || '.' || column_name || ':' || data_type || ':' || is_nullable || ':' || coalesce(character_maximum_length::text, '') || ':' || coalesce(column_default, '')
    || ':' || is_identity || ':' || coalesce(identity_generation, ''), ',' ORDER BY table_name, column_name), ''))
  FROM information_schema.columns WHERE table_schema = 'identity'
UNION ALL SELECT 'structure.indexes', count(*), md5(coalesce(string_agg(indexdef, ',' ORDER BY indexname), '')) FROM pg_indexes WHERE schemaname = 'identity'
UNION ALL SELECT 'structure.constraints', count(*), md5(coalesce(string_agg(conrelid::regclass::text || '.' || conname || ':' || pg_get_constraintdef(oid), ',' ORDER BY conrelid::regclass::text, conname), ''))
  FROM pg_constraint WHERE connamespace = 'identity'::regnamespace
UNION ALL SELECT 'structure.triggers', count(*), md5(coalesce(string_agg(pg_get_triggerdef(oid), ',' ORDER BY tgname), ''))
  FROM pg_trigger WHERE tgrelid IN (SELECT oid FROM pg_class WHERE relnamespace = 'identity'::regnamespace) AND NOT tgisinternal
UNION ALL SELECT 'structure.comments', count(*), md5(coalesce(string_agg(objoid::regclass::text || '.' || objsubid || ':' || description, ',' ORDER BY objoid::regclass::text, objsubid), ''))
  FROM pg_description WHERE classoid = 'pg_class'::regclass AND objoid IN (SELECT oid FROM pg_class WHERE relnamespace = 'identity'::regnamespace)
UNION ALL SELECT 'structure.sequences', count(*), md5(coalesce(string_agg(sequencename || '=' || coalesce(last_value::text, 'null'), ',' ORDER BY sequencename), ''))
  FROM pg_sequences WHERE schemaname = 'identity';
