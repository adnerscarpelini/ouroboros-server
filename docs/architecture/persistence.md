# Persistência com PostgreSQL e EF Core

## Decisão

Cada módulo que constitui um serviço persistente possui banco PostgreSQL, credencial, connection string, DbContext e migrations próprios. Schemas podem organizar um banco internamente, mas não substituem bancos separados entre serviços.

## Instância local compartilhada

Por decisão consciente de aprendizado, todos os bancos ficam em uma única instância PostgreSQL, em um único container Docker. Isso economiza recursos e administração local. Não precisamos de um servidor por serviço nesta etapa.

Disponibilidade, CPU, memória e armazenamento são compartilhados: parada, atualização, saturação ou perda do volume podem afetar todos os bancos. Separação lógica não oferece isolamento físico ou alta disponibilidade. APIs, workers e broker executam separadamente do PostgreSQL.

## Acesso e configuração

Runtime tem credencial exclusiva, sem superuser, criação de roles ou bancos. Acessos públicos inadequados são revogados e a negação entre proprietários é testada. Provisionamento e migrations usam permissões separadas.

Não usar JOIN, FDW, dblink ou credenciais alheias para contornar a fronteira. Cada serviço aceita endpoint próprio, permitindo mover seu banco sem alterar regras de negócio.

## Roles e privilégios por serviço

O procedimento `scripts/database`, executado pela credencial administrativa da instância (`POSTGRES_USER`), cria para cada serviço persistente (Identity e Notifications) um banco e duas roles de login. Customers, Accounts e Transactions não têm banco, pois não existem como serviços. Comando e operação: [Provisionar bancos e roles por serviço](../development.md#provisionar-bancos-e-roles-por-serviço).

| Serviço | Banco | Role de runtime | Role de migration |
| --- | --- | --- | --- |
| Identity | `ouroboros_identity` | `identity_runtime` | `identity_migrator` |
| Notifications | `ouroboros_notifications` | `notifications_runtime` | `notifications_migrator` |

**Todas as roles de serviço** são `LOGIN` e `NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS`. A credencial administrativa não é usada por APIs nem workers; um teste de arquitetura impede que `POSTGRES_USER` e `POSTGRES_PASSWORD` apareçam em `src`.

**Runtime** é a única credencial entregue à API ou ao worker do serviço:

- conecta somente ao banco do próprio serviço, com `CONNECT` e `USAGE` nos schemas, sem `TEMPORARY` nem `CREATE`;
- recebe `SELECT`, `INSERT`, `UPDATE` e `DELETE` nas tabelas, e `USAGE` e `SELECT` nas sequences. Para objetos futuros, o privilégio vem de `ALTER DEFAULT PRIVILEGES FOR ROLE <migrator>`: o que a role de migration criar passa a ser acessível ao runtime. Não há `TRUNCATE`, DDL, criação de banco ou de role;
- não acessa a tabela `__EFMigrationsHistory`: o acesso é revogado quando o procedimento é executado depois das migrations.

**Migration** é a dona do banco do serviço e executa DDL somente nele. Seu uso se restringe ao comando explícito de migrations; a API e o worker não a recebem.

**Isolamento entre bancos.** `CONNECT`, `TEMPORARY` e `CREATE` de `PUBLIC` são revogados nos bancos dos serviços e no schema `public` deles. Uma role de serviço não conecta ao banco de outro serviço nem aos bancos que não pertencem a serviço algum: o banco original da instância (`POSTGRES_DB`) e o `postgres` também perdem `CONNECT` e `TEMPORARY` de `PUBLIC`. A credencial administrativa continua conectando.

**Idempotência.** Reexecutar cria banco e roles uma vez, reaplica atributos, senhas (o que permite rotação) e privilégios, converge o acesso a objetos já migrados e nunca remove bancos, roles, volumes ou dados. Senhas vazias ou iguais entre runtime e migration do mesmo serviço interrompem o procedimento antes de criar qualquer coisa para ele.

**Senhas fora dos logs.** As senhas passam por instruções SQL, que um servidor com `log_statement` ou `log_min_duration_statement` ativos registraria. Cada sessão aberta pelo procedimento neutraliza esses registros (`log_statement`, `log_min_error_statement`, `log_min_duration_statement`, `log_min_duration_sample` e `log_statement_sample_rate`) por `SET` próprio, aplicado de novo a cada `\connect`. As senhas chegam por variáveis de ambiente, nunca por argumento. Um teste com a instância em log verboso confirma que elas não aparecem na saída nem no log.

### Limitações

- **Janela entre migration e reexecução.** Tabelas criadas pela role de migration recebem os privilégios de runtime por padrão, inclusive uma nova `__EFMigrationsHistory`. O histórico só é negado ao runtime na próxima execução do procedimento, por isso ele deve ser reexecutado depois de aplicar migrations.
- **Catálogo e `template1`.** Os privilégios padrão de `PUBLIC` sobre o catálogo do PostgreSQL permanecem: qualquer role lê os nomes de bancos e roles, e o procedimento não altera o banco `template1`, ao qual `PUBLIC` mantém `CONNECT`. Isso não dá acesso aos dados de outro serviço.
- **Large objects.** O procedimento não restringe funções padrão de `PUBLIC`, como `lo_create`; o runtime pode criar large objects no próprio banco.
- **Instância compartilhada.** Roles e bancos separados são isolamento lógico. CPU, memória, conexões, disco, parada e perda do volume continuam compartilhados, e quem tem a credencial administrativa acessa todos os bancos.
- **Segredos.** O procedimento não gerencia segredos de produção nem rotaciona sozinho: a senha vem do `.env`.

## Migrations geradas

Histórico pertence ao serviço. Aplicar migrations explicitamente por comando, script ou bundle, sem execução automática na subida da API. Classes geradas podem ser públicas para descoberta do EF; não são contratos de integração.

As migrations de Identity ficam em `src/Services/Identity/Ouroboros.Identity/Infrastructure/Persistence/Migrations`. O modelo de dados não depende do caminho, do assembly nem do namespace da biblioteca: `IdentityDbContext`, o schema `identity`, a tabela de histórico, os ids das migrations e o snapshot são os mesmos de bancos já migrados, e o EF não encontra mudanças pendentes no modelo. [`IdentityModelStabilityTests`](../../tests/Ouroboros.IntegrationTests/IdentityModelStabilityTests.cs) verifica ids, schema e ausência de mudanças pendentes; reorganizar pastas ou namespaces não deve gerar migration.

Comandos disponíveis: [desenvolvimento](../development.md). Provisionamento de bancos e roles: [#92](https://github.com/adnerscarpelini/ouroboros-server/issues/92). Parâmetros: #81. Transferência do banco de Identity: #82. Destino do banco original: #97.

## Transações e recuperação

Transações locais protegem parâmetro e histórico, senha e sessões, estado e outbox. Não usar transação distribuída.

Transferência exige backup, janela sem escrita, cópia e validação de identificadores, hashes, sessões e histórico antes da troca. Não excluir origem ou volume automaticamente. Depois de novas escritas, rollback precisa preservá-las ou reconciliá-las; voltar apenas a imagem não basta.

## Testes

Usar PostgreSQL real para constraints, locks e concorrência. Verificar acesso negado a banco alheio, permissões runtime, migrations e restauração. `DatabaseProvisioningTests` cobre o provisionamento em PostgreSQL real: idempotência sobre dados existentes, conexão negada a banco alheio, DDL e administração negados ao runtime, DDL da migration restrito ao próprio banco, histórico de migrations negado após reexecução e ausência das senhas na saída e no log.

[Voltar à arquitetura](README.md)
