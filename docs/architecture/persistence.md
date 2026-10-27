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

Notifications possui `NotificationsDbContext` e migrations em `src/Services/Notifications/Ouroboros.Notifications/Infrastructure/Persistence/Migrations`, começando por `20261026081751_InitialNotificationsInbox`. Usa somente `ConnectionStrings:Notifications`, schema `notifications` e roles `notifications_runtime`/`notifications_migrator`, sem fallback para Identity. A factory design-time exige a variável `ConnectionStrings__Notifications` da operação de migration; runtime não migra, não faz DDL nem lê o histórico. [Execução e recuperação](../development.md#banco-e-migrations-de-notifications).

Histórico pertence ao serviço. Aplicar migrations explicitamente por comando, script ou bundle, sem execução automática na subida da API. Classes geradas podem ser públicas para descoberta do EF; não são contratos de integração.

As migrations de Identity ficam em `src/Services/Identity/Ouroboros.Identity/Infrastructure/Persistence/Migrations`. O modelo de dados não depende do caminho, do assembly nem do namespace da biblioteca: `IdentityDbContext`, o schema `identity`, a tabela de histórico, os ids das migrations e o snapshot são os mesmos de bancos já migrados, e o EF não encontra mudanças pendentes no modelo. [`IdentityModelStabilityTests`](../../tests/Ouroboros.IntegrationTests/IdentityModelStabilityTests.cs) verifica ids, schema e ausência de mudanças pendentes; reorganizar pastas ou namespaces não deve gerar migration.

Comandos disponíveis: [desenvolvimento](../development.md). Provisionamento de bancos e roles: [#92](https://github.com/adnerscarpelini/ouroboros-server/issues/92). Parâmetros: [#81](https://github.com/adnerscarpelini/ouroboros-server/issues/81). Transferência do banco de Identity: [#82](https://github.com/adnerscarpelini/ouroboros-server/issues/82). Destino e desativação do banco original: [#97](https://github.com/adnerscarpelini/ouroboros-server/issues/97).

## Connection strings de Identity

A API de Identity lê `ConnectionStrings:Identity`, a connection string do banco próprio com a role de runtime (`identity_runtime`). `ConnectionStrings:Ouroboros`, a do banco original da instância, só vale quando a primeira não está definida (ou está em branco). Remover `ConnectionStrings:Identity` é, portanto, o retorno à origem; uma variável de ambiente `ConnectionStrings__Identity` esquecida continua valendo e deve ser conferida. Sem nenhuma das duas, a API não sobe e a mensagem cita as duas chaves. O schema `settings` do banco original fica retido, sem projeto que o acesse; seu inventário, backup final e desativação controlada estão em [Destino e desativação do banco original (#97)](#destino-e-desativação-do-banco-original-97).

A troca de uma para a outra é ação do operador, liberada pelo `cutover` da transferência. As migrations do banco próprio usam outra connection string, a da role de migration, passada ao comando `dotnet ef database update --connection`; a role de runtime não executa DDL, e o comando é recusado pelo PostgreSQL (`42501`). Passo a passo em [desenvolvimento](../development.md#banco-de-dados-local).

## Transações e recuperação

Transações locais protegem parâmetro e histórico, senha e sessões, estado e outbox. Não usar transação distribuída.

Identity inclui `email_intents` desde `20261026015350_AddEmailIntents`: token/intenção e cancelamento de substituídas confirmam juntos, no mesmo contexto e banco. Expiração elimina conteúdo protegido e dados de associação; tombstones terminais sem payload são purgados após o prazo absoluto + 30 dias, em lotes limitados. A #86 acrescenta a migration `AddEmailPublicationLeases`, com reivindicação autocommit/lease e confirmação local condicionada ao proprietário; conteúdo é descartado somente após confirm positivo ou estado terminal. [Outbox e retenção](../identity/email-outbox.md).

### Backups com intenções de e-mail

Backups que incluam a outbox precisam de criptografia e retenção limitada mesmo que o envelope interno já seja cifrado. O wrapper `protected-email-backup.ps1` protege `pg_dump` custom de até 64 MiB com AES256-GCM e chave externa própria, autentica metadados e recusa restauração após 30 dias. Identity só purga arquivos gerenciados quando `Identity:EmailIntents:BackupDirectory` aponta para diretório dedicado marcado pelo wrapper; essa tentativa independe da disponibilidade do banco. Sem a configuração, não há purga automática de arquivos. [Operação, auditoria e limites](../development.md#backup-protegido-das-intenções-de-e-mail).

Esse mecanismo não cifra/adota/apaga os dumps antigos de transferência ou CSVs de reconciliação. O operador deve restringir, proteger e retirar essas cópias pela política, sem exclusão incidental de dados. O wrapper aceita somente dump custom, não CSV. Seu auditlog cobre Protect/Restore pelo wrapper, sem conteúdo/caminhos; acessos diretos ao filesystem exigem controles/auditoria operacionais. Ao restaurar, mantenha processamento parado e reaplique expiração e estados terminais antes de habilitá-lo.

## Transferência do banco de Identity

**Decisão.** O banco de Identity é transferido uma única vez, inteiro, do banco original para `ouroboros_identity`, mantendo o schema `identity` dentro do banco novo. Não há estado intermediário com dois bancos em uso: ou a API usa a origem, ou o destino. O procedimento é `scripts/database/transfer-identity.sh`, versionado, executado por comando explícito com a credencial administrativa da instância e dividido em etapas: janela sem escrita, backup restaurado e comparado, cópia, validação, `cutover`, e `rollback` e `reconcile` quando for preciso voltar. Comandos e ordem em [Transferir o banco de Identity](../development.md#transferir-o-banco-de-identity).

**Janela sem escrita.** O banco original recebe `default_transaction_read_only = on` para novas sessões, sob a condição de não haver sessões ativas. É proteção de padrão de transação, não de privilégio: quem tem `SET` (a credencial administrativa) consegue escrever. A garantia depende de as APIs e os workers estarem parados.

**Backup verificável.** O backup completo da origem, em formato custom, é restaurado num banco de verificação e comparado com a origem pelo mesmo resumo da validação. Só então a cópia é permitida, e a cópia recusa uma origem alterada depois do backup. O banco de verificação, criado pelo `backup`, é o único removido pelo procedimento. Os arquivos ficam em `./backups`, fora do Git, com permissões restritas, e contêm hashes de senha e de token.

**Cópia e validação.** A cópia restaura só o schema `identity`, num destino vazio, com a role de migration como dona dos objetos, e reexecuta o provisionamento para convergir privilégios. A validação compara origem e destino em 20 facetas (contagens e linhas inteiras, identificadores públicos, hashes de senha, `token_hash`, sessões, tokens de e-mail, parâmetros, revisões, histórico e histórico de migrations, e a estrutura: colunas, defaults, índices, constraints, gatilhos, comentários e sequências) e grava a linha de base (id e hash de cada linha copiada). O `cutover` repete a validação e confere que a role de runtime lê, não executa DDL, não cria objetos em `public`, não alcança o banco original nem o de Notifications, e que a role de migration conecta. Só então libera a troca. A falha de qualquer verificação impede a troca e deixa a origem intacta.

**Sem exclusão.** Nenhum comando exclui a origem, volumes, bancos ou credenciais antigas. Para repetir a cópia depois de uma validação que falhou, o operador esvazia o destino de forma consciente. O destino do banco original e a revogação/rotação da credencial antiga estão em [Destino e desativação do banco original (#97)](#destino-e-desativação-do-banco-original-97).

**Rollback e reconciliação.** Voltar a imagem ou o executável não recupera dados escritos no destino. Por isso a ordem é parar as APIs, `rollback`, `reconcile` e, só então, remover `ConnectionStrings:Identity` (e liberar a origem com `unfreeze`). Antes de novas escritas, o `rollback` apenas informa que não há diferença e basta remover a chave. Depois delas, o `rollback` compara o destino com a linha de base, exporta e preserva as linhas novas ou alteradas em `./backups/identity-<id>/reconcile/*.csv`, sem alterar o destino, e o `reconcile` as aplica na origem numa única transação, em ordem de chaves estrangeiras, ajustando as sequências (inclusive as nunca usadas) e verificando a equivalência com o destino. Ele recusa o conflito, quando a origem foi escrita depois da linha de base, mantendo a janela, e é idempotente.

**Limites.**

- Linhas excluídas no destino não são reconciliadas: o `rollback` aborta se linhas copiadas deixaram de existir.
- Há um único ciclo de `rollback` e `reconcile`; depois dele a origem difere da linha de base, e novas escritas no destino exigem avaliação manual dos arquivos preservados.
- Se o destino recebeu migrations depois da troca, o `reconcile` recusa e a reconciliação é manual.
- A tabela `email_intents`, quando existe na origem, participa de resumo, linha de base e reconciliação. `rollback` e `reconcile` conferem igualdade de históricos de migrations antes de continuar: destino com #85 e origem pré-#85 não permitem simples retorno da connection string. Preserve e reconcilie schema/escritas manualmente antes de voltar; não descarte a outbox.
- A janela sem escrita não impede quem tem a credencial administrativa; o `reconcile` reabre a escrita da origem para aplicar as linhas.
- Os backups e os arquivos de reconciliação contêm hashes e são dados sensíveis; o procedimento não os apaga.

**Alternativa descartada.** Transferir por partes (por exemplo, os parâmetros antes do restante) deixaria Identity dividida entre dois bancos durante a transição, com transações locais e consistência entre tabelas comprometidas. A transferência única do banco inteiro evita esse estado.

## Destino e desativação do banco original (#97)

**Decisão aprovada.** Conservar o banco original e o backup final por 30 dias a partir do cutover validado. O operador registra esse marco; nenhuma exclusão é automática. A operação permanece manual e não foi executada no ambiente existente nesta entrega. Após o `cutover` da transferência do banco de Identity ([#82](https://github.com/adnerscarpelini/ouroboros-server/issues/82)) e a retirada dos hosts agregadores e do projeto `Ouroboros.Settings` ([#81](https://github.com/adnerscarpelini/ouroboros-server/issues/81), [#96](https://github.com/adnerscarpelini/ouroboros-server/issues/96)), o banco original da instância (`POSTGRES_DB`, padrão `ouroboros`) não é excluído por nenhum script, container ou comando automatizado. O procedimento [`scripts/database/decommission-original-db.sh`](../../scripts/database/decommission-original-db.sh) (executado no profile explícito `decommission` do [`compose.yaml`](../../compose.yaml)) inventaria o conteúdo remanescente, verifica sessões e privilégios na origem, com confirmação manual das configurações de runtime, gera e valida o backup final, rotaciona/revoga a credencial ampla antiga e desativa conexões ao banco original de forma não destrutiva e reversível, exigindo aprovação explícita do dono do projeto. Passo a passo e comandos em [Desativar o banco original e rotacionar a credencial antiga](../development.md#desativar-o-banco-original-e-rotacionar-a-credencial-antiga).

**Inventário do que resta no banco original (`inventory`).** O comando `inventory` compara os schemas e tabelas do banco original com `ouroboros_identity` e classifica cada objeto remanescente:

- **Schema `identity` (`transferred_to_target` / `migration_history_transferred`):** conserva a cópia congelada do instante do `cutover` (`users`, `sessions`, `refresh_tokens`, `email_tokens`, `email_intents`, `parameters`, `parameter_history` e `identity.__EFMigrationsHistory`), transferida integralmente para `ouroboros_identity` na [#82](https://github.com/adnerscarpelini/ouroboros-server/issues/82).
- **Schema `settings` (`retained_only_in_source` / `migration_history_retired_in_source`):** conserva as tabelas `settings.parameters`, `settings.parameter_history` e `settings.__EFMigrationsHistory` (incluindo `20260322014949_InitialSettings`), retidas exclusivamente no banco original. Seus parâmetros vigentes e todo o histórico foram copiados e validados em `identity.parameters` e `identity.parameter_history` pela migration `OwnIdentityParameters` ([#81](https://github.com/adnerscarpelini/ouroboros-server/issues/81)), e o projeto `Ouroboros.Settings` foi retirado na [#96](https://github.com/adnerscarpelini/ouroboros-server/issues/96); `ouroboros_identity` não recebe o schema `settings`.
- **Schema `public` (`retained_only_in_source`):** eventuais objetos legados pré-migração permanecem retidos apenas na origem e são preservados no backup final antes do bloqueio de conexões.

**Critério de desuso por runtime (`verify-unused`).** Antes de gerar o backup final ou desativar o banco original, `verify-unused` verifica sessões, privilégios de conexão e presença do schema de destino:

- zero sessões ativas conectadas ao banco original em `pg_stat_activity` (`datname = <SOURCE_DATABASE>`);
- `PUBLIC` sem privilégio `CONNECT` no banco original;
- nenhuma role de serviço (`identity_runtime`, `identity_migrator`, `notifications_runtime` e `notifications_migrator`) com privilégio `CONNECT` no banco original;
- `ouroboros_identity` contendo o schema `identity`. Separadamente, o operador deve confirmar ConnectionStrings:Identity em todos os runtimes e retirar ConnectionStrings:Ouroboros: o script não inspeciona configurações de processos. No [`compose.yaml`](../../compose.yaml), o healthcheck do container `postgres` consulta `-d postgres`, sem abrir conexões no banco original.

**Janela de retenção e decisão do dono do projeto.** A desativação do banco original é uma ação operacional manual condicionada ao encerramento da janela de rollback pós-`cutover` e exige duas variáveis explícitas de aprovação do dono do projeto:

- RETENTION_DAYS=30 registra a política aprovada de 30 dias a partir do cutover validado. O instante desse cutover deve ser registrado e acompanhado pelo operador; o manifesto de desativação não substitui esse marco. O script não agenda exclusão nem controla automaticamente o fim da retenção;
- `CONFIRM_ORIGINAL_DB_DEACTIVATION=DEACTIVATE:<banco>`: confirmação nominal do banco alvo (por exemplo, `DEACTIVATE:ouroboros`). Sem ambas as variáveis, `deactivate` falha sem alterar o banco.

**Backup final verificável (`final-backup`).** Executa `verify-unused`, garante que o banco original esteja congelado em modo somente leitura (`default_transaction_read_only = on`) e sem sessões concorrentes, cria o pacote `./backups/original-<id>/` (com permissões `0700`/`0600`), registra `inventory.txt`, gera o dump completo em formato custom de todos os schemas remanescentes (`identity`, `settings` e `public`) em `final.dump` com checksum SHA-256 (`final.dump.sha256`) e calcula o digest da origem para schemas, nomes/tipos/nulabilidade de colunas, sequências e linhas das tabelas comuns (`source-inventory.digest`). Em seguida, restaura `final.dump` num banco temporário `original_decommission_check_<id>`, calcula `restore-check.digest`, compara os dois digests com `diff -u`, remove exclusivamente o banco temporário de verificação e grava o marcador `final-backup-verified`.

O dump contém o banco completo, mas a comparação dos digests não abrange funções, views, índices ou constraints. A restauração sem erro e a igualdade dessas facetas são evidências complementares; não comprovam equivalência estrutural absoluta. O pacote recebe ID com timestamp e sufixo aleatório e registra source-database; deactivate e reactivate recusam pacote de outra origem ou ID inválido. Bancos de manutenção, templates e bancos dos serviços não são alvos permitidos.

**Revogação e rotação da credencial antiga ampla (`rotate-admin-credential`).** Ao encerrar a janela de rollback:

- ADMIN_ROLE deve ser uma role superusuária e não pode ser uma das roles de serviço. As senhas de runtime de Identity e Notifications são obrigatórias para verificar a conectividade após a operação;
- a senha da credencial administrativa ampla da instância (`ADMIN_ROLE`, padrão `POSTGRES_USER`) é rotacionada para `NEW_ADMIN_PASSWORD` (recusando valor vazio ou igual a qualquer senha de `identity_*` ou `notifications_*`) sob `session-safety.sql`, de modo que a nova senha nunca aparece em stdout, stderr nem nos logs do PostgreSQL;
- se houver uma role legada ampla (`LEGACY_ROLE`) usada pelos antigos hosts antes do `cutover`, o comando encerra suas sessões ativas, revoga `CONNECT` e `TEMPORARY` em todos os bancos e aplica `ALTER ROLE ... WITH NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD NULL`, recusando operar sobre roles ativas de serviço (`identity_runtime`, `identity_migrator`, `notifications_runtime`, `notifications_migrator`) ou sobre o próprio superusuário;
- ao final, valida que `identity_runtime` e `notifications_runtime` continuam conectando aos seus respectivos bancos (`ouroboros_identity` e `ouroboros_notifications`).

**Desativação manual sem exclusão (`deactivate <id>`) e reativação (`reactivate <id>`).**

- `deactivate <id>` valida `CONFIRM_ORIGINAL_DB_DEACTIVATION`, `RETENTION_DAYS`, a presença de `final-backup-verified`, a integridade do SHA-256 de `final.dump`, o critério `verify-unused` e a igualdade entre o estado atual do banco (`deactivate-precheck.digest`) e `source-inventory.digest` (recusando desativar se a origem difere nas facetas comparadas após o backup final). Em seguida, revoga `CONNECT` e `TEMPORARY` de `PUBLIC`, mantém `default_transaction_read_only = on`, grava comentário de auditoria no catálogo (`COMMENT ON DATABASE`) e aplica `ALTER DATABASE ... WITH ALLOW_CONNECTIONS false CONNECTION LIMIT 0`, bloqueando novas conexões com SQLSTATE `55000` e gravando `./backups/original-<id>/deactivated.manifest`. Nenhum comando executa `DROP DATABASE` sobre o banco original, `DROP SCHEMA` ou exclusão de volumes.
- `reactivate <id>` reabilita conexões ao banco original em modo somente leitura (`ALLOW_CONNECTIONS true CONNECTION LIMIT -1` e `default_transaction_read_only = on`) e registra `./backups/original-<id>/reactivated` para inspeção ou recuperação autorizada durante a janela de retenção. Caso um rollback tardio para o banco original precise ser acionado após novas escritas em `ouroboros_identity`, o operador deve primeiro executar `reactivate <id>` e então executar `transfer-identity rollback <transfer_id>`, `transfer-identity reconcile <transfer_id>` e `transfer-identity unfreeze` antes de remover `ConnectionStrings:Identity`.

## Testes

Usar PostgreSQL real para constraints, locks e concorrência. Verificar acesso negado a banco alheio, permissões runtime, migrations e restauração. `DatabaseProvisioningTests` cobre o provisionamento em PostgreSQL real: idempotência sobre dados existentes, conexão negada a banco alheio, DDL e administração negados ao runtime, DDL da migration restrito ao próprio banco, histórico de migrations negado após reexecução e ausência das senhas na saída e no log. [`IdentityDatabaseTransferTests`](../../tests/Ouroboros.IntegrationTests/IdentityDatabaseTransferTests.cs) e [`IdentityDatabaseTransferHardeningTests`](../../tests/Ouroboros.IntegrationTests/IdentityDatabaseTransferHardeningTests.cs) cobrem a transferência com a API real na role de runtime: equivalência, backup restaurado, falha de validação sem troca, privilégios, migrations pela role de migration, rollback com e sem novas escritas e reconciliação. [`IdentityConnectionStringTests`](../../tests/Ouroboros.IntegrationTests/IdentityConnectionStringTests.cs) cobre a precedência das connection strings. [`OriginalDatabaseArchitectureTests`](../../tests/Ouroboros.ArchitectureTests/OriginalDatabaseArchitectureTests.cs) e [`OriginalDatabaseDecommissioningTests`](../../tests/Ouroboros.IntegrationTests/OriginalDatabaseDecommissioningTests.cs) cobrem o inventário do banco original (`identity`, `settings` e `public`), a verificação de desuso por runtime, o backup final restaurado e verificado por digest e SHA-256, a exigência de aprovação explícita e `RETENTION_DAYS`, o bloqueio não destrutivo de conexões (`ALLOW_CONNECTIONS false`), a reativação em somente leitura e a rotação/revogação da credencial antiga ampla sem afetar `Identity` nem `Notifications` e sem vazar senhas nos logs.

[Voltar à arquitetura](README.md)
