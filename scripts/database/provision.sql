-- Provisiona, por serviço persistente, banco, role de runtime e role de migration na instância
-- PostgreSQL compartilhada. Idempotente: pode ser executado quantas vezes for preciso, em volume novo
-- ou existente, e nunca remove bancos, roles, volumes ou dados.
--
-- Executar por comando explícito, com a credencial administrativa da instância (nunca pelas APIs):
--   docker compose --profile provision run --rm provision-databases
--
-- As senhas vêm de variáveis de ambiente (arquivo .env), nunca de argumentos. Veja docs/development.md.

\set ON_ERROR_STOP on
\set QUIET on
\ir session-safety.sql

-- Um serviço persistente por bloco. Para incluir outro, repita o bloco com nomes e variáveis próprios.
-- O Notifications ainda não tem código; o banco e as roles existem para a #87. Customers, Accounts e
-- Transactions não são provisionados: não existem como serviços.

\set svc identity
\set svc_db ouroboros_identity
\set svc_runtime identity_runtime
\set svc_migrator identity_migrator
\unset svc_runtime_password
\unset svc_migrator_password
\getenv svc_runtime_password IDENTITY_RUNTIME_PASSWORD
\getenv svc_migrator_password IDENTITY_MIGRATION_PASSWORD
\ir provision-service.sql

\set svc notifications
\set svc_db ouroboros_notifications
\set svc_runtime notifications_runtime
\set svc_migrator notifications_migrator
\unset svc_runtime_password
\unset svc_migrator_password
\getenv svc_runtime_password NOTIFICATIONS_RUNTIME_PASSWORD
\getenv svc_migrator_password NOTIFICATIONS_MIGRATION_PASSWORD
\ir provision-service.sql

-- Bancos que não são de nenhum serviço (o banco original da instância e o de manutenção) deixam de
-- aceitar conexão de PUBLIC: nenhuma role de serviço entra neles. A credencial administrativa
-- (superusuário) continua conectando. Nenhum dado ou objeto é alterado.
\connect postgres
\ir session-safety.sql
\unset initial_db
\getenv initial_db POSTGRES_DB
\if :{?initial_db}
\else
  \set initial_db postgres
\endif

SELECT format('REVOKE CONNECT, TEMPORARY ON DATABASE %I FROM PUBLIC', datname)
FROM pg_database
WHERE datname IN ('postgres', :'initial_db')
\gexec

\echo 'Provisionamento concluído.'
