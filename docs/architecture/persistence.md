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

O procedimento `scripts/database`, executado pela credencial administrativa da instância (`POSTGRES_USER`), cria para cada serviço persistente (Identity e Notifications) um banco e duas roles de login. Customers, Accounts e Transactions não têm banco, pois não existem como serviços; cada um receberá banco, credencial, connection string e migrations próprios quando for criado ([estrutura dos serviços](module-structure.md#criação-de-novos-serviços)). Comando e operação: [Provisionar bancos e roles por serviço](../development.md#provisionar-bancos-e-roles-por-serviço).

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

Comandos disponíveis: [desenvolvimento](../development.md). Provisionamento de bancos e roles: [#92](https://github.com/adnerscarpelini/ouroboros-server/issues/92). Parâmetros: [#81](https://github.com/adnerscarpelini/ouroboros-server/issues/81). Transferência do banco de Identity: [#82](https://github.com/adnerscarpelini/ouroboros-server/issues/82). Destino do banco original: #97.

## Connection strings de Identity

A API de Identity lê `ConnectionStrings:Identity`, a connection string do banco próprio com a role de runtime (`identity_runtime`). `ConnectionStrings:Ouroboros`, a do banco original da instância, só vale quando a primeira não está definida (ou está em branco). Remover `ConnectionStrings:Identity` é, portanto, o retorno à origem; uma variável de ambiente `ConnectionStrings__Identity` esquecida continua valendo e deve ser conferida. Sem nenhuma das duas, a API não sobe e a mensagem cita as duas chaves. O schema `settings` do banco original fica retido, sem projeto que o acesse; seu destino é a #97.

A troca de uma para a outra é ação do operador, liberada pelo `cutover` da transferência. As migrations do banco próprio usam outra connection string, a da role de migration, passada ao comando `dotnet ef database update --connection`; a role de runtime não executa DDL, e o comando é recusado pelo PostgreSQL (`42501`). Passo a passo em [desenvolvimento](../development.md#banco-de-dados-local).

## Transações e recuperação

Transações locais protegem parâmetro e histórico, senha e sessões, estado e outbox. Não usar transação distribuída.

## Transferência do banco de Identity

**Decisão.** O banco de Identity é transferido uma única vez, inteiro, do banco original para `ouroboros_identity`, mantendo o schema `identity` dentro do banco novo. Não há estado intermediário com dois bancos em uso: ou a API usa a origem, ou o destino. O procedimento é `scripts/database/transfer-identity.sh`, versionado, executado por comando explícito com a credencial administrativa da instância e dividido em etapas: janela sem escrita, backup restaurado e comparado, cópia, validação, `cutover`, e `rollback` e `reconcile` quando for preciso voltar. Comandos e ordem em [Transferir o banco de Identity](../development.md#transferir-o-banco-de-identity).

**Janela sem escrita.** O banco original recebe `default_transaction_read_only = on` para novas sessões, sob a condição de não haver sessões ativas. É proteção de padrão de transação, não de privilégio: quem tem `SET` (a credencial administrativa) consegue escrever. A garantia depende de as APIs e os workers estarem parados.

**Backup verificável.** O backup completo da origem, em formato custom, é restaurado num banco de verificação e comparado com a origem pelo mesmo resumo da validação. Só então a cópia é permitida, e a cópia recusa uma origem alterada depois do backup. O banco de verificação, criado pelo `backup`, é o único removido pelo procedimento. Os arquivos ficam em `./backups`, fora do Git, com permissões restritas, e contêm hashes de senha e de token.

**Cópia e validação.** A cópia restaura só o schema `identity`, num destino vazio, com a role de migration como dona dos objetos, e reexecuta o provisionamento para convergir privilégios. A validação compara origem e destino em 20 facetas (contagens e linhas inteiras, identificadores públicos, hashes de senha, `token_hash`, sessões, tokens de e-mail, parâmetros, revisões, histórico e histórico de migrations, e a estrutura: colunas, defaults, índices, constraints, gatilhos, comentários e sequências) e grava a linha de base (id e hash de cada linha copiada). O `cutover` repete a validação e confere que a role de runtime lê, não executa DDL, não cria objetos em `public`, não alcança o banco original nem o de Notifications, e que a role de migration conecta. Só então libera a troca. A falha de qualquer verificação impede a troca e deixa a origem intacta.

**Sem exclusão.** Nenhum comando exclui a origem, volumes, bancos ou credenciais antigas. Para repetir a cópia depois de uma validação que falhou, o operador esvazia o destino de forma consciente. O destino do banco original e a revogação da credencial antiga são decisão da #97.

**Rollback e reconciliação.** Voltar a imagem ou o executável não recupera dados escritos no destino. Por isso a ordem é parar as APIs, `rollback`, `reconcile` e, só então, remover `ConnectionStrings:Identity` (e liberar a origem com `unfreeze`). Antes de novas escritas, o `rollback` apenas informa que não há diferença e basta remover a chave. Depois delas, o `rollback` compara o destino com a linha de base, exporta e preserva as linhas novas ou alteradas em `./backups/identity-<id>/reconcile/*.csv`, sem alterar o destino, e o `reconcile` as aplica na origem numa única transação, em ordem de chaves estrangeiras, ajustando as sequências (inclusive as nunca usadas) e verificando a equivalência com o destino. Ele recusa o conflito, quando a origem foi escrita depois da linha de base, mantendo a janela, e é idempotente.

**Limites.**

- Linhas excluídas no destino não são reconciliadas: o `rollback` aborta se linhas copiadas deixaram de existir.
- Há um único ciclo de `rollback` e `reconcile`; depois dele a origem difere da linha de base, e novas escritas no destino exigem avaliação manual dos arquivos preservados.
- Se o destino recebeu migrations depois da troca, o `reconcile` recusa e a reconciliação é manual.
- A janela sem escrita não impede quem tem a credencial administrativa; o `reconcile` reabre a escrita da origem para aplicar as linhas.
- Os backups e os arquivos de reconciliação contêm hashes e são dados sensíveis; o procedimento não os apaga.

**Alternativa descartada.** Transferir por partes (por exemplo, os parâmetros antes do restante) deixaria Identity dividida entre dois bancos durante a transição, com transações locais e consistência entre tabelas comprometidas. A transferência única do banco inteiro evita esse estado.

## Testes

Usar PostgreSQL real para constraints, locks e concorrência. Verificar acesso negado a banco alheio, permissões runtime, migrations e restauração. `DatabaseProvisioningTests` cobre o provisionamento em PostgreSQL real: idempotência sobre dados existentes, conexão negada a banco alheio, DDL e administração negados ao runtime, DDL da migration restrito ao próprio banco, histórico de migrations negado após reexecução e ausência das senhas na saída e no log. [`IdentityDatabaseTransferTests`](../../tests/Ouroboros.IntegrationTests/IdentityDatabaseTransferTests.cs) e [`IdentityDatabaseTransferHardeningTests`](../../tests/Ouroboros.IntegrationTests/IdentityDatabaseTransferHardeningTests.cs) cobrem a transferência com a API real na role de runtime: equivalência, backup restaurado, falha de validação sem troca, privilégios, migrations pela role de migration, rollback com e sem novas escritas e reconciliação. [`IdentityConnectionStringTests`](../../tests/Ouroboros.IntegrationTests/IdentityConnectionStringTests.cs) cobre a precedência das connection strings.

[Voltar à arquitetura](README.md)
