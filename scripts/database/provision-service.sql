-- Provisiona um serviço. Incluído por provision.sql, que define as variáveis psql:
--   svc, svc_db, svc_runtime, svc_migrator, svc_runtime_password, svc_migrator_password

\connect postgres
\ir session-safety.sql

-- Falha cedo, sem criar nada, se faltar alguma senha ou se as duas roles compartilharem a mesma.
\if :{?svc_runtime_password}
\else
  \set svc_runtime_password ''
\endif
\if :{?svc_migrator_password}
\else
  \set svc_migrator_password ''
\endif

SELECT length(:'svc_runtime_password') > 0
   AND length(:'svc_migrator_password') > 0
   AND :'svc_runtime_password' <> :'svc_migrator_password' AS secrets_ok
\gset

\if :secrets_ok
\else
  \echo 'ERRO no serviço:' :svc
  DO $$
  BEGIN
    RAISE EXCEPTION 'Defina senhas distintas e não vazias para as roles de runtime e de migration do serviço (veja .env.example).';
  END
  $$;
\endif

-- Roles de login sem nenhum privilégio de instância. A senha é reaplicada a cada execução, o que
-- também serve à rotação.
SELECT format('CREATE ROLE %I LOGIN', :'svc_runtime')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'svc_runtime')
\gexec

SELECT format('CREATE ROLE %I LOGIN', :'svc_migrator')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'svc_migrator')
\gexec

ALTER ROLE :"svc_runtime" WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD :'svc_runtime_password';
ALTER ROLE :"svc_migrator" WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD :'svc_migrator_password';

-- O banco pertence à role de migration: ela executa DDL, mas só aqui.
SELECT format('CREATE DATABASE %I OWNER %I', :'svc_db', :'svc_migrator')
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'svc_db')
\gexec

ALTER DATABASE :"svc_db" OWNER TO :"svc_migrator";

-- Somente as duas roles do serviço conectam. PUBLIC perde CONNECT, TEMPORARY e CREATE.
REVOKE ALL ON DATABASE :"svc_db" FROM PUBLIC;
REVOKE ALL ON DATABASE :"svc_db" FROM :"svc_runtime";
GRANT CONNECT ON DATABASE :"svc_db" TO :"svc_runtime";

\connect :"svc_db"
\ir session-safety.sql

REVOKE ALL ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON SCHEMA public FROM :"svc_runtime";
GRANT USAGE ON SCHEMA public TO :"svc_runtime";

-- O que a role de migration criar daqui em diante fica acessível, apenas para DML, ao runtime.
ALTER DEFAULT PRIVILEGES FOR ROLE :"svc_migrator" GRANT USAGE ON SCHEMAS TO :"svc_runtime";
ALTER DEFAULT PRIVILEGES FOR ROLE :"svc_migrator" GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :"svc_runtime";
ALTER DEFAULT PRIVILEGES FOR ROLE :"svc_migrator" GRANT USAGE, SELECT ON SEQUENCES TO :"svc_runtime";

-- Converge o que já existe (migrations aplicadas antes desta execução): mesmos privilégios.
SELECT format('GRANT USAGE ON SCHEMA %I TO %I', nspname, :'svc_runtime')
FROM pg_namespace
WHERE nspname !~ '^pg_' AND nspname NOT IN ('information_schema', 'public')
\gexec

SELECT format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %I TO %I', nspname, :'svc_runtime')
FROM pg_namespace
WHERE nspname !~ '^pg_' AND nspname NOT IN ('information_schema')
\gexec

SELECT format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA %I TO %I', nspname, :'svc_runtime')
FROM pg_namespace
WHERE nspname !~ '^pg_' AND nspname NOT IN ('information_schema')
\gexec

-- O histórico de migrations é do comando de migration: o runtime não o lê nem o altera.
SELECT format('REVOKE ALL ON %I.%I FROM %I', schemaname, tablename, :'svc_runtime')
FROM pg_tables
WHERE tablename = '__EFMigrationsHistory'
\gexec

\echo 'Serviço provisionado:' :svc
