# Executar o checkout atual

Este guia contém comandos dos projetos disponíveis. A organização Services e os executáveis independentes são acompanhados no [estado de implementação](implementation-status.md). Não execute caminhos futuros como se já existissem.

## Requisitos

- .NET SDK 10.0.402, fixado em `global.json`.
- Visual Studio compatível com .NET 10 e SLNX, se for usar o Visual Studio.
- Docker com Docker Compose, para o PostgreSQL de desenvolvimento; os testes de integração também exigem o Docker em execução.

## Visual Studio e compilação

Abra `Ouroboros.slnx` por **Arquivo → Abrir → Projeto/Solução**. Abrir somente a pasta do repositório usa a visualização de arquivos. `src`, `src/Modules`, `src/Services` e `src/Services/Identity` são pastas organizadoras da solution. Identity é a biblioteca `Ouroboros.Identity` e a API `Ouroboros.Identity.Api`, em `src/Services/Identity/`; Accounts, Customers, Settings e Transactions são projetos em `src/Modules`. Cada projeto de biblioteca é independente e compilável pelo menu de contexto **Compilar**.

Defina `Ouroboros.Identity.Api` como projeto de inicialização para executar Identity (um processo, com os listeners público e administrativo). Os hosts agregadores `Ouroboros.Api` e `Ouroboros.Backoffice.Api` ainda existem ([estado de implementação](implementation-status.md)), mas usam as mesmas portas; não os execute ao mesmo tempo que a API de Identity. As bibliotecas de Identity e dos módulos não são executáveis independentes.

Na raiz do repositório:

```powershell
dotnet restore Ouroboros.slnx
dotnet build Ouroboros.slnx --no-restore
dotnet build src/Services/Identity/Ouroboros.Identity/Ouroboros.Identity.csproj
dotnet build src/Services/Identity/Ouroboros.Identity.Api/Ouroboros.Identity.Api.csproj
dotnet build src/Modules/Ouroboros.Modules.Customers/Ouroboros.Modules.Customers.csproj
```

Os arquivos `.gitkeep` preservam as pastas vazias no Git; os itens `Folder` nos projetos explicitam sua organização para o Visual Studio.

## Testes

Na raiz do repositório:

```powershell
dotnet test --solution Ouroboros.slnx                     # toda a suíte: arquitetura e integração
dotnet test --project tests/Ouroboros.ArchitectureTests   # só arquitetura (sem Docker)
dotnet test --project tests/Ouroboros.IntegrationTests    # só integração (exige Docker)
```

- Os testes de arquitetura não acessam o banco: sobem a API de Identity com uma connection string e uma chave de assinatura fictícias. Os testes não usam a chave dos user-secrets.
- Os testes de listeners em Kestrel real ([`KestrelListenerTests`](../tests/Ouroboros.IntegrationTests/KestrelListenerTests.cs)) abrem portas livres de loopback e exigem Docker pelo banco. O teste de que o listener administrativo não responde por endereço da máquina é ignorado quando a máquina não tem endereço IPv4 fora de loopback.
- Os testes de dependência analisam os assemblies de todos os projetos de `src`, listados em `architecture-assemblies.txt`, gerado no build do projeto de testes. Compile antes (`dotnet build Ouroboros.slnx`); sem o manifesto, o teste falha pedindo a compilação. Um serviço ou executável novo em `src` é analisado automaticamente. Regras e lista de transição em [Dependências entre camadas e serviços](architecture/layer-dependencies.md#verificação).
- Os testes de integração exigem o Docker em execução. Eles sobem o próprio PostgreSQL em container com Testcontainers e não usam o banco do Docker Compose, o `.env` nem os user-secrets. A primeira execução baixa a imagem `postgres:18-alpine`; as seguintes levam poucos segundos.
- Os testes de provisionamento (`DatabaseProvisioningTests`) também exigem o Docker. [ProvisionedInstance](../tests/Ouroboros.IntegrationTests/ProvisionedInstance.cs) sobe uma instância PostgreSQL própria, separada da `PostgresFixture` porque o provisionamento cria roles e revoga `CONNECT`, efeitos de instância inteira. A instância nasce com um banco `ouroboros` com dados legados e com log verboso (`log_statement=all`, `log_min_duration_statement=0`), para provar que as senhas não chegam ao log do servidor. O teste copia `scripts/database` para o container e executa o mesmo `psql` do comando do Compose, com senhas aleatórias geradas a cada execução; não usa o `.env`.
- Os testes da transferência do banco de Identity (`IdentityDatabaseTransferTests` e `IdentityDatabaseTransferHardeningTests`) também exigem o Docker e usam a mesma `ProvisionedInstance`, que copia `scripts/database` para o container e executa `transfer-identity.sh` com a credencial administrativa da instância de teste. Cobrem backup restaurado e comparado, janela sem escrita, equivalência de origem e destino, falha de validação sem troca e com origem intacta, recusas do `copy`, privilégios da role de runtime (sem DDL, sem banco alheio, sem `public`), migrations só pela role de migration, rollback sem e com novas escritas, reconciliação (idempotência, conflito, sequências nunca usadas) e login, renovação e `me` com a API real na role de runtime após a troca. Não substituem o ensaio em cópia do ambiente real.
- `IdentityConnectionStringTests` verifica a precedência de `ConnectionStrings:Identity` sobre `ConnectionStrings:Ouroboros`, o uso da segunda quando a primeira não existe e a falha de subida, citando as duas chaves, quando nenhuma está configurada.
- `CredentialIsolationTests`, nos testes de arquitetura e sem Docker, verifica que `POSTGRES_USER` e `POSTGRES_PASSWORD` não aparecem em arquivos de código ou configuração de `src`, que o `.env.example` traz só senhas de exemplo distintas (prefixo `troque-`) e que `*.env` está no `.gitignore`.

Como funcionam e como escrever um teste: [Testes de integração com PostgreSQL](architecture/integration-tests.md).

### Integração contínua

O workflow [ci.yml](../.github/workflows/ci.yml) roda em runner Linux a cada pull request para a `main` e a cada push nela, com a versão do SDK de `global.json`. Não usa banco, `.env`, user-secrets nem segredos; o token do workflow só lê o repositório. Uma nova execução no mesmo PR cancela a anterior.

| Check (nome do job) | O que executa | Equivale localmente a |
| --- | --- | --- |
| Build e testes de arquitetura | Restore e build da solution em Release, mais os testes de arquitetura | `dotnet build Ouroboros.slnx -c Release` e `dotnet test --project tests/Ouroboros.ArchitectureTests -c Release` |
| Testes de integração | Testes de integração com PostgreSQL em container (Testcontainers) | `dotnet test --project tests/Ouroboros.IntegrationTests -c Release` |

Falha de build, de teste ou de job reprova o check. Os resultados em formato TRX ficam como artefato do workflow por 7 dias. Um passo do primeiro job falha se existir um projeto de teste em `tests/` que não esteja listado no workflow, para que nenhuma suíte deixe de rodar sem aviso. Os nomes dos checks são os que o ruleset da `main` deve exigir.

### Fixtures e adaptadores de teste

O checkout usa [PostgresFixture](../tests/Ouroboros.IntegrationTests/PostgresFixture.cs): um container por suíte e um banco modelo com migrations aplicadas. Cada teste recebe banco clonado, isolado dos demais. [IntegrationTest](../tests/Ouroboros.IntegrationTests/IntegrationTest.cs) cria a API de Identity e remove somente seu banco ao terminar.

[IntegrationApiFactory](../tests/Ouroboros.IntegrationTests/IntegrationApiFactory.cs) inicia a API de Identity (`Ouroboros.Identity.Api`) em Testing, com configuração em memória que sobrepõe connection string e chave fictícia. `CreateClient()` fala com o listener público e `CreateAdminClient()`, com o administrativo; `BackofficeIntegrationApiFactory`, declarada no mesmo arquivo, faz todas as requisições do cliente chegarem ao listener administrativo. Como o `TestServer` não tem sockets, o listener é escolhido pelo cabeçalho de teste `X-Test-Listener` (`admin`), lido só por um resolvedor registrado no projeto de testes; a produção ignora o cabeçalho. A separação por portas é verificada em `KestrelListenerTests`, com Kestrel real. Limites elevados evitam interferência nos testes de negócio; RateLimitingTests usa limites próprios; `ForwardedHeadersTests` usa o cabeçalho de teste `X-Test-Client-IP` para definir o IP da conexão.

Os hosts agregadores têm fábricas próprias, [LegacyExternalHostFactory](../tests/Ouroboros.IntegrationTests/LegacyExternalHostFactory.cs) e [LegacyBackofficeHostFactory](../tests/Ouroboros.IntegrationTests/LegacyBackofficeHostFactory.cs), usadas pela `PostgresFixture` para aplicar as migrations do modelo, pelos testes de migração e de fronteira de dados e pela fumaça [LegacyHostsSmokeTests](../tests/Ouroboros.IntegrationTests/LegacyHostsSmokeTests.cs) (login e consulta do próprio usuário em cada host). O comportamento completo é verificado na API de Identity. Nos testes de arquitetura, `OuroborosApiFactory` e `BackofficeApiFactory` ([OuroborosApiFactory.cs](../tests/Ouroboros.ArchitectureTests/OuroborosApiFactory.cs)) sobem a API de Identity com banco fictício, no listener público e no administrativo. Casos que dependem de tempo ou corridas substituem TimeProvider/hasher por adaptadores controlados. [CapturingEmailSender](../tests/Ouroboros.IntegrationTests/CapturingEmailSender.cs) captura links e simula falha sem envio real; EmailTests verifica o adaptador .eml. Isso não comprova entrega distribuída.

## Banco de dados local

A API de Identity (e os hosts agregadores, que carregam a mesma biblioteca) lê a connection string de `ConnectionStrings:Identity`, o banco próprio do serviço (`ouroboros_identity`, role `identity_runtime`). Quando essa chave não está definida, usa `ConnectionStrings:Ouroboros`, o banco original da instância: é o caso até o operador fazer a troca liberada pelo [cutover da transferência](#transferir-o-banco-de-identity) ou configurar uma instalação nova diretamente no banco próprio. Sem nenhuma das duas, falha ao subir com `OptionsValidationException` e uma mensagem que cita as duas chaves. As migrations nunca são aplicadas na subida; rode-as por comando. Todos os comandos abaixo são executados na raiz do repositório.

1. Crie o `.env` a partir do modelo e troque a senha de `POSTGRES_PASSWORD`, além das quatro senhas por serviço quando for [provisionar os bancos](#provisionar-bancos-e-roles-por-serviço) (o `.env` fica fora do Git):

   ```powershell
   cp .env.example .env
   ```

2. Suba o PostgreSQL e aguarde o healthcheck:

   ```powershell
   docker compose up -d --wait
   ```

3. Grave a connection string do **banco original** nos user-secrets do projeto `Ouroboros.Identity.Api`, com os valores do `.env` (credencial administrativa da instância; uso restrito ao desenvolvimento local). Os hosts agregadores `Ouroboros.Api` e `Ouroboros.Backoffice.Api` usam o mesmo `UserSecretsId`, então leem a mesma connection string e a mesma chave de assinatura; quem já gravou os segredos por um deles não precisa repeti-los. Os hosts agregadores continuam precisando de `ConnectionStrings:Ouroboros` para o contexto legado de Settings, mesmo depois que Identity passar a usar o banco próprio:

   ```powershell
   dotnet user-secrets set "ConnectionStrings:Ouroboros" "Host=localhost;Port=5432;Database=ouroboros;Username=ouroboros;Password=<a senha do .env>" --project src/Services/Identity/Ouroboros.Identity.Api
   ```

   Para usar o banco próprio e a role de runtime, [provisione](#provisionar-bancos-e-roles-por-serviço) a instância e defina `ConnectionStrings:Identity`, que tem precedência sobre `ConnectionStrings:Ouroboros`. Em ambiente com dados, essa troca só vale depois da [transferência](#transferir-o-banco-de-identity); em instalação nova, veja o passo 4.

4. Para **instalação nova**, sem tabelas legadas de parâmetros, restaure a ferramenta local `dotnet-ef` e aplique as migrations de Identity. Para **banco existente**, antes de qualquer atualização siga o [procedimento de backup restaurado, janela sem escrita e migração dos parâmetros](identity/parameters.md#upgrade-de-banco-existente) e, em seguida, a [transferência do banco de Identity](#transferir-o-banco-de-identity). Se migrations antigas de Settings precisarem ser aplicadas, faça isso antes de Identity; nunca depois do corte.

   Com a connection string do banco original (passo 3):

   ```powershell
   dotnet tool restore
   dotnet ef database update --project src/Services/Identity/Ouroboros.Identity --startup-project src/Services/Identity/Ouroboros.Identity.Api --context IdentityDbContext
   ```

   No **banco próprio**, as migrations só rodam por comando explícito com a role de migration, passada em `--connection`; com a role de runtime o comando é recusado pelo PostgreSQL (`42501`). O projeto de inicialização ainda precisa de uma connection string válida na configuração (`ConnectionStrings:Identity` ou `ConnectionStrings:Ouroboros`) para carregar a API:

   ```powershell
   dotnet ef database update --connection "Host=localhost;Port=5432;Database=ouroboros_identity;Username=identity_migrator;Password=<IDENTITY_MIGRATION_PASSWORD>" --project src/Services/Identity/Ouroboros.Identity --startup-project src/Services/Identity/Ouroboros.Identity.Api --context IdentityDbContext
   ```

   Uma senha com `;` ou aspas exige aspas no valor da connection string (formato do Npgsql), por exemplo `Password="a;b"`. Depois de migrar o banco próprio, reexecute o [provisionamento](#provisionar-bancos-e-roles-por-serviço) para que o histórico de migrations seja negado ao runtime.

   `OwnIdentityParameters` cria sementes com `uuidv7()` em instalação limpa, exigindo PostgreSQL 18 (a versão do `compose.yaml`). No upgrade, copia e valida os parâmetros e histórico de Settings na mesma transação. A migration não faz backup automaticamente nem exclui a origem; não ative a versão nova se ela falhar.

   No primeiro `database update` em um banco vazio, o log mostra `Failed executing DbCommand` sobre `__EFMigrationsHistory` antes de criar a tabela. É um comportamento conhecido do EF Core: o comando termina com `Done.`. Repetir o comando não altera nada.

5. Opcionalmente, confira os schemas: `identity` contém `users`, `sessions`, `refresh_tokens`, `email_tokens`, `parameters` e `parameter_history`. Em banco migrado, `settings` continua preservado, sem novas escritas; não é necessário criá-lo em instalação limpa. Ajuste usuário e banco se mudou o `.env`:

   ```powershell
   docker compose exec postgres psql -U ouroboros -d ouroboros -c "\dn"
   ```

Para gerar uma nova migration do Identity:

```powershell
dotnet ef migrations add <Nome> --project src/Services/Identity/Ouroboros.Identity --startup-project src/Services/Identity/Ouroboros.Identity.Api --context IdentityDbContext --output-dir Infrastructure/Persistence/Migrations
```

`docker compose down` para o banco e mantém os dados no volume `postgres-data`. Preserve esse volume e os backups durante a migração. Fora de desenvolvimento, a connection string vem da variável de ambiente `ConnectionStrings__Identity` (banco próprio) ou, enquanto não houver troca, `ConnectionStrings__Ouroboros`. Uma `ConnectionStrings__Identity` esquecida no ambiente continua tendo precedência sobre `ConnectionStrings:Ouroboros`: confira-a ao voltar para o banco original. Detalhes e o padrão para outros módulos estão em [Persistência com PostgreSQL e EF Core](architecture/persistence.md).

## Provisionar bancos e roles por serviço

Cada serviço persistente tem banco e roles próprios na instância PostgreSQL local: Identity (`ouroboros_identity`) e Notifications (`ouroboros_notifications`). O procedimento é versionado em `scripts/database` (`provision.sql`, `provision-service.sql` e `session-safety.sql`) e roda só por comando explícito: nem `docker compose up` nem as APIs o executam. A decisão de roles e privilégios está em [Persistência](architecture/persistence.md#roles-e-privilégios-por-serviço).

1. No `.env`, troque as quatro senhas por serviço, diferentes das de exemplo e entre si:

   | Variável | Role |
   | --- | --- |
   | `IDENTITY_RUNTIME_PASSWORD` | `identity_runtime` |
   | `IDENTITY_MIGRATION_PASSWORD` | `identity_migrator` |
   | `NOTIFICATIONS_RUNTIME_PASSWORD` | `notifications_runtime` |
   | `NOTIFICATIONS_MIGRATION_PASSWORD` | `notifications_migrator` |

   O Compose interpola `$` nos valores do `.env`: uma senha com `$` deve ser escrita com `$$` (por exemplo, `abc$$def` vale `abc$def`). Uma connection string que leve essa senha usa o valor real, com um único `$`.

2. Execute, na raiz do repositório (o Compose sobe o PostgreSQL e espera o healthcheck, se preciso):

   ```powershell
   docker compose --profile provision run --rm provision-databases
   ```

   A saída termina com `Provisionamento concluído.`, precedida de `Serviço provisionado: identity` e `Serviço provisionado: notifications`. O serviço `provision-databases` usa `POSTGRES_USER` e `POSTGRES_PASSWORD` do `.env`, a credencial **administrativa** da instância (superusuário): só o container do PostgreSQL e este comando a recebem, e APIs e workers nunca a usam.

3. Confira roles e bancos (ajuste o usuário se mudou o `.env`):

   ```powershell
   docker compose exec postgres psql -U ouroboros -d postgres -c "\du" -c "\l"
   ```

Comportamento do comando:

- **Idempotente.** Funciona em volume novo e em volume existente (a imagem só executa `docker-entrypoint-initdb.d` com o volume vazio, por isso o procedimento é um comando à parte). Reexecutar cria cada banco e cada role uma única vez, não altera dados e nunca apaga bancos, roles, volumes ou dados.
- **Converge o acesso.** Depois de aplicar migrations, reexecute o comando: tabelas e sequences já criadas passam a ter os privilégios do runtime, e o histórico de migrations (`__EFMigrationsHistory`) é negado ao runtime. Entre a migration e a reexecução, um histórico recém-criado ainda é acessível ao runtime.
- **Rotação de senha.** Troque o valor no `.env` e reexecute: a senha de cada role é reaplicada a cada execução. Atualize também a connection string de quem usa a role.
- **Validação.** Senha vazia ou ausente, ou igual entre as roles de runtime e de migration do mesmo serviço, faz o comando falhar com código diferente de zero, sem criar nada para aquele serviço. Os serviços são processados em ordem (Identity, depois Notifications): se a falha for no segundo, o primeiro já foi provisionado. Senhas iguais entre serviços não são recusadas; use valores distintos. A saída e o log do servidor não contêm as senhas.
- **Banco original e `postgres`.** O banco de `POSTGRES_DB` (`ouroboros` no modelo) e o `postgres` deixam de aceitar `CONNECT` e `TEMPORARY` de `PUBLIC`: nenhuma role de serviço entra neles. A credencial administrativa continua conectando, e nenhum dado ou objeto é alterado.

Formato das connection strings por serviço, com marcadores no lugar das senhas do `.env` (exemplo para Identity; Notifications segue o padrão com `ouroboros_notifications` e `notifications_*`):

```text
# runtime: a única que a API ou o worker do serviço deve receber (DML, sem DDL)
Host=localhost;Port=5432;Database=ouroboros_identity;Username=identity_runtime;Password=<IDENTITY_RUNTIME_PASSWORD>

# migration: só para o comando explícito de migrations (DDL no banco do próprio serviço)
Host=localhost;Port=5432;Database=ouroboros_identity;Username=identity_migrator;Password=<IDENTITY_MIGRATION_PASSWORD>
```

O provisionamento não altera nenhuma API nem host e não aplica migrations: nenhum processo passa a usar as roles novas por causa dele. A API de Identity só lê `ConnectionStrings:Identity` quando o operador a define, o que a [transferência do banco](#transferir-o-banco-de-identity) libera no `cutover`; enquanto ela não existir, vale `ConnectionStrings:Ouroboros`, configurada em [Banco de dados local](#banco-de-dados-local). As migrations do banco próprio rodam só com a role de migration (veja o passo 4 de [Banco de dados local](#banco-de-dados-local)). A disponibilidade de cada etapa está no [estado de implementação](implementation-status.md).

## Transferir o banco de Identity

Move todo o banco de Identity (schema `identity`: `users`, `sessions`, `refresh_tokens`, `email_tokens`, `parameters`, `parameter_history` e `__EFMigrationsHistory`) do banco original da instância (`POSTGRES_DB`) para `ouroboros_identity`, em um único procedimento, sem estado intermediário com dois bancos. Está em `scripts/database/transfer-identity.sh` (com `identity-digest.sql`) e roda só por comando explícito: nem `docker compose up` nem as APIs o executam. A decisão, a validação e os limites do rollback estão em [Persistência](architecture/persistence.md#transferência-do-banco-de-identity).

**Pré-requisitos**

- Banco e roles [provisionados](#provisionar-bancos-e-roles-por-serviço) (sem eles, `copy` recusa o destino) e as quatro senhas por serviço no `.env`: o `copy` reexecuta o provisionamento, que reaplica as quatro senhas.
- Migrations de Identity já aplicadas na origem, inclusive os parâmetros locais (veja o [upgrade de banco existente](identity/parameters.md#upgrade-de-banco-existente)). O histórico de migrations é transferido junto.
- Todos os escritores parados: API de Identity, hosts agregadores, workers, `create-admin` e comandos de migration. O procedimento não os para.
- Docker Compose. Os clientes PostgreSQL (`pg_dump`, `pg_restore`, `psql`) rodam no container `transfer-identity`, com a credencial **administrativa** do `.env`; não há o que instalar na máquina.

**Comando.** Todos têm a forma abaixo, na raiz do repositório; `<id>` é o identificador impresso pelo `backup`:

```powershell
docker compose --profile transfer run --rm transfer-identity <comando> [id]
```

| Comando | O que faz |
| --- | --- |
| `freeze` | Inicia a janela sem escrita na origem. |
| `backup` | Backup completo da origem (formato custom), restaurado em um banco de verificação `identity_transfer_check_<id>` e comparado com a origem. Imprime `Transferência: <id>`. |
| `copy <id>` | Restaura só o schema `identity` no destino `ouroboros_identity`, que deve estar vazio, com a role `identity_migrator` como dona; converge os privilégios reexecutando o provisionamento; executa a validação. |
| `validate <id>` | Compara origem e destino e grava a linha de base das linhas copiadas. Pode ser repetido. |
| `cutover <id>` | Valida de novo, confere os privilégios das roles e libera a troca da connection string. |
| `rollback <id>` | Avalia o retorno e preserva em disco as escritas feitas no destino depois da troca. |
| `reconcile <id>` | Aplica na origem as escritas preservadas pelo `rollback`. |
| `unfreeze` | Encerra a janela sem escrita da origem. |
| `help` | Mostra a ajuda do script. |

**Ordem da transferência**

```powershell
docker compose --profile transfer run --rm transfer-identity freeze
docker compose --profile transfer run --rm transfer-identity backup
docker compose --profile transfer run --rm transfer-identity copy <id>
docker compose --profile transfer run --rm transfer-identity cutover <id>
```

1. `freeze` com as APIs e os workers parados.
2. `backup` imprime o `<id>`. Se a restauração do backup divergir da origem, o comando falha, nada é copiado e a origem segue na janela sem escrita.
3. `copy <id>` e, em seguida, a validação. Em divergência, não troque a connection string.
4. `cutover <id>` repete a validação e confere que a role de runtime lê o destino, não executa DDL, não cria objetos em `public`, não alcança o banco original nem o de Notifications, e que a role de migration conecta. Só então grava a liberação e imprime as instruções da troca.
5. Faça a [troca da connection string](#trocar-a-connection-string-de-identity) e inicie as APIs. A origem permanece intacta e somente leitura até o `unfreeze`.

**Janela sem escrita.** `freeze` exige que não haja sessões ativas no banco (pare as APIs e os workers; `FORCE_TERMINATE=1` as encerra) e executa `ALTER DATABASE ... SET default_transaction_read_only = on`, que vale para as novas sessões. É proteção de padrão de transação: quem executa `SET default_transaction_read_only = off`, como a credencial administrativa, ainda consegue escrever. Para encerrar sessões, passe a variável ao container:

```powershell
docker compose --profile transfer run --rm -e FORCE_TERMINATE=1 transfer-identity freeze
```

**Backups.** Ficam em `./backups/identity-<id>/`, pasta ignorada pelo Git, montada no container como `/backups`: `source.dump`, os resumos comparados em cada etapa, a linha de base (`baseline.tsv`) e as marcas de etapa. O procedimento cria a pasta com permissão `700` e os arquivos com `600`; em host Linux eles pertencem ao `root` do container, e a leitura exige `sudo`. Os arquivos contêm hashes de senha e de token: trate-os como segredo e não os copie para logs, issues nem o repositório. Nada os remove.

**Validação.** O resumo comparado entre origem e destino tem 20 facetas: contagens e linhas inteiras de `users`, `sessions`, `refresh_tokens`, `email_tokens`, `parameters` e `parameter_history`; identificadores públicos de usuários e sessões; hashes de senha; `token_hash` dos refresh tokens e dos tokens de e-mail; revisões dos parâmetros e do histórico; histórico de migrations; e a estrutura (colunas com tipos e defaults, índices, constraints, gatilhos, comentários e sequências). Qualquer diferença reprova a validação.

**Falha e repetição.** A falha de validação impede a troca e a origem fica intacta. Nada é excluído: nem a origem, nem volumes, nem a credencial antiga; o único banco removido é o de verificação criado pelo próprio `backup`. O `copy` recusa destino que já tem o schema `identity`, origem alterada depois do backup e senhas de runtime e de migration iguais; se falhar no meio, remove o schema parcial que ele mesmo criou no destino vazio. Para repetir depois de uma validação que falhou, avalie o destino e, conscientemente, esvazie-o antes de rodar o `copy` de novo (o comando aparece na recusa; ajuste usuário e banco se mudou o `.env`):

```powershell
docker compose exec postgres psql -U ouroboros -d ouroboros_identity -c "DROP SCHEMA identity CASCADE"
```

### Trocar a connection string de Identity

A troca é ação do operador e só é liberada pelo `cutover`. Com as APIs de Identity paradas, defina a connection string da role de runtime, que tem precedência sobre `ConnectionStrings:Ouroboros`, e inicie-as:

```powershell
dotnet user-secrets set "ConnectionStrings:Identity" "Host=localhost;Port=5432;Database=ouroboros_identity;Username=identity_runtime;Password=<IDENTITY_RUNTIME_PASSWORD>" --project src/Services/Identity/Ouroboros.Identity.Api
```

Fora de desenvolvimento, use a variável `ConnectionStrings__Identity`. Os hosts agregadores seguem lendo `ConnectionStrings:Ouroboros` para o contexto legado de Settings. Senha com `;` ou aspas exige aspas no valor da connection string. Depois da troca, sessões, refresh tokens, senhas, tokens de e-mail pendentes e parâmetros com histórico continuam válidos. Migrations do banco próprio: comando explícito com a role de migration, descrito no passo 4 de [Banco de dados local](#banco-de-dados-local). O desligamento do banco original e a revogação da credencial antiga não fazem parte deste procedimento (#97).

### Voltar ao banco original

Pare as APIs de Identity antes de qualquer passo: voltar a imagem ou o executável não recupera dados escritos no destino. A ordem é parar as APIs, `rollback`, `reconcile` (se necessário), remover `ConnectionStrings:Identity` e `unfreeze`:

```powershell
docker compose --profile transfer run --rm transfer-identity rollback <id>
```

- **Sem novas escritas.** A saída informa `Sem novas escritas no destino`. Basta remover `ConnectionStrings:Identity` (inclusive a variável `ConnectionStrings__Identity`, se houver) para a origem voltar a valer e executar `unfreeze`.
- **Com novas escritas.** O `rollback` exporta e preserva em `./backups/identity-<id>/reconcile/*.csv` as linhas novas ou alteradas do destino, que não é alterado. Aplique-as na origem e conclua:

  ```powershell
  docker compose --profile transfer run --rm transfer-identity reconcile <id>
  docker compose --profile transfer run --rm transfer-identity unfreeze
  ```

  O `reconcile` reabre a escrita da origem para aplicar as linhas numa transação, em ordem de chaves estrangeiras, ajusta as sequências (inclusive as nunca usadas) e confere a equivalência com o destino. Mantenha as APIs paradas até remover `ConnectionStrings:Identity`. Se a origem foi escrita depois da linha de base, o `reconcile` recusa o conflito, mantém a janela sem escrita e nada é aplicado; reexecutá-lo depois de reconciliar não altera nada.

Limites: linhas excluídas no destino não são reconciliadas (o `rollback` aborta se linhas copiadas deixaram de existir); há um único ciclo de `rollback` e `reconcile`; se o destino recebeu migrations depois da troca, o `reconcile` recusa e a reconciliação é manual.

O `rollback` exige que não haja sessões ativas no destino, e o `reconcile`, na origem (`FORCE_TERMINATE=1` as encerra, como no `freeze`). Os testes do procedimento estão descritos em [Testes](#testes).

## Criar o primeiro administrador

Administradores não se cadastram sozinhos. Com o banco configurado e as migrations de Identity aplicadas, incluindo seus parâmetros locais, crie o primeiro `Admin` pelo comando `create-admin` da API de Identity, que roda e encerra sem subir o servidor HTTP nem abrir os listeners. A senha vai na variável de ambiente `OUROBOROS_ADMIN_PASSWORD`, nunca em argumento (argumentos ficam no histórico do shell e na lista de processos):

```powershell
$env:OUROBOROS_ADMIN_PASSWORD = '<senha>'
dotnet run --project src/Services/Identity/Ouroboros.Identity.Api -- create-admin --email <e-mail>
Remove-Item Env:OUROBOROS_ADMIN_PASSWORD
```

No bash: `OUROBOROS_ADMIN_PASSWORD='<senha>' dotnet run --project src/Services/Identity/Ouroboros.Identity.Api -- create-admin --email <e-mail>`.

- O e-mail precisa ser do domínio do parâmetro `Identity:AdminInvitations:AllowedEmailDomain` (valor inicial `adnerscarpelini.com`), sem subdomínios.
- A senha tem de 15 a 128 caracteres, sem regras de composição (frases com espaços e acentos valem), e não pode ser comum nem conter `ouroboros` ou a parte local do e-mail ([Política de senha](architecture/password-policy.md)).
- Só funciona enquanto não houver nenhum `Admin`; os próximos entram por convite.
- Código de saída `0`: criado; `1`: recusado, com a mensagem da regra e `Nada foi criado.`; `2`: falta `--email` ou a variável da senha.

Detalhes em [Criar o primeiro administrador](identity/first-admin.md). Com o `Admin` criado, entre pelo login administrativo, no listener administrativo, `POST /api/v1/admin/identity/sessions` ([Fazer login com e-mail e senha](identity/login.md#como-usar)).

## Chave de assinatura dos tokens

A API de Identity (e os hosts agregadores) exige a chave `Authentication:Jwt:SigningKey`, que assina e valida os access tokens JWT (HS256). Sem ela, ou com uma chave vazia ou menor que 32 bytes (256 bits) em UTF-8, falha ao subir com `OptionsValidationException` e uma mensagem que cita a chave. A chave nunca vai para o Git nem para o `appsettings.json`.

1. Gere uma chave aleatória. Por exemplo, com OpenSSL:

   ```powershell
   openssl rand -base64 48
   ```

   Ou, só com PowerShell:

   ```powershell
   $bytes = New-Object byte[] 48; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes); [Convert]::ToBase64String($bytes)
   ```

   Os dois geram 64 caracteres, acima do mínimo de 32 bytes.

2. Grave a chave nos user-secrets do projeto `Ouroboros.Identity.Api` (o mesmo `UserSecretsId` dos hosts agregadores, que leem a mesma chave):

   ```powershell
   dotnet user-secrets set "Authentication:Jwt:SigningKey" "<a chave gerada>" --project src/Services/Identity/Ouroboros.Identity.Api
   ```

Fora de desenvolvimento, a chave vem da variável de ambiente `Authentication__Jwt__SigningKey`, com o mesmo valor em todos os processos que emitem ou validam tokens. Trocar a chave invalida os tokens já emitidos. Emissor, audiências e tempos de vida ficam no `appsettings.json` do host; detalhes em [Autenticação JWT e autorização por políticas](architecture/authentication.md).

Os valores de `Authentication:Jwt` disponíveis são:

| Chave | Valor no checkout |
| --- | --- |
| Issuer | `ouroboros` |
| Audience | `ouroboros-api`: tokens de `Customer`, aceitos no listener público |
| AdminAudience | `ouroboros-backoffice`: tokens de `Admin`, aceitos no listener administrativo. Obrigatória na API de Identity e diferente de `Audience` |
| AccessTokenLifetime | `00:15:00` |
| RefreshTokenLifetime | `7.00:00:00`, contados novamente a cada renovação |
| SigningKey | Externa ao Git, pelo menos 32 bytes UTF-8; Base64 é usado como texto, sem decodificação |

Emissor/audiência ausentes (inclusive `AdminAudience` na API de Identity ou igual a `Audience`) e durações não positivas também impedem a subida. Os hosts agregadores usam só `Audience` (`ouroboros-api` na API externa, `ouroboros-backoffice` no backoffice) e não definem `AdminAudience`. O checkout valida HS256 com tolerância de relógio de 30 segundos e sem consulta à sessão a cada chamada; rotação assimétrica é uma entrega da #84, conforme o [estado de implementação](implementation-status.md).

## Executar a API de Identity

Com o banco e a chave de assinatura configurados, na raiz do repositório:

```powershell
dotnet run --project src/Services/Identity/Ouroboros.Identity.Api
```

Um único processo atende os dois públicos, cada um em seu listener:

| Listener | Público | Perfil `http` (padrão) | Perfil `https` (`--launch-profile https`) |
| --- | --- | --- | --- |
| Público | App do cliente, anônimos e integrações (rotas `/api/v1/identity/...`) | `http://localhost:5141` | `https://localhost:7135` e `http://localhost:5141` |
| Administrativo | Backoffice (rotas `/api/v1/admin/...`), só em loopback por padrão | `http://localhost:5142` | `https://localhost:7136` e `http://localhost:5142` |

Os dois perfis usam o ambiente `Development`. As URLs vêm de `Identity:Listeners:Public:Urls`, `Identity:Listeners:Admin:Urls` e `Identity:Listeners:Admin:AllowNonLoopback` (padrão `false`); o perfil `https` as define por variáveis de ambiente em `launchSettings.json`. `ASPNETCORE_URLS` e `--urls` não alteram os listeners: para mudar portas, use essas chaves (por exemplo, `Identity__Listeners__Admin__Urls__0` nas variáveis de ambiente). O processo não sobe se uma URL for inválida (não absoluta, esquema diferente de http/https, caminho, host que não seja `localhost` nem endereço IP), se uma porta for repetida entre o listener público e o administrativo, ou se o administrativo usar um endereço fora de loopback sem `AllowNonLoopback=true`; a mensagem cita a chave. Detalhes em [APIs e workers](architecture/hosts.md#listeners).

Um token é aceito só no listener do seu público: o de `Customer` (audiência `ouroboros-api`) no listener público e o de `Admin` (audiência `ouroboros-backoffice`) no administrativo; no outro, é recusado com `401`. Uma rota administrativa chamada no listener público (e uma externa no administrativo) responde como uma rota inexistente: `401` sem token e `404` com token válido, para qualquer método HTTP.

Para pôr a API atrás de um proxy, configure `Identity:ForwardedHeaders:KnownProxies` (IPs) ou `KnownNetworks` (CIDR, por exemplo `10.0.0.0/8`). Sem isso, `X-Forwarded-For` é ignorado e o rate limit por IP usa o endereço da conexão; um IP ou CIDR inválido impede a subida.

Os user-secrets (connection string e chave de assinatura) só são lidos no ambiente `Development`; com `--no-launch-profile`, o ambiente passa a ser `Production` e a API não encontra a connection string nem a chave de assinatura, a menos que você defina `ASPNETCORE_ENVIRONMENT=Development` ou as variáveis `ConnectionStrings__Identity` (ou `ConnectionStrings__Ouroboros`, enquanto não houver troca) e `Authentication__Jwt__SigningKey`. Em `Production`, defina também as URLs dos listeners, se os padrões não servirem.

### Hosts agregadores

`Ouroboros.Api` e `Ouroboros.Backoffice.Api` continuam funcionais, sem mudança de contrato, até a retirada pela #96 ([estado de implementação](implementation-status.md)). Usam as mesmas portas de desenvolvimento da API de Identity, então não execute os dois ao mesmo tempo:

```powershell
dotnet run --project src/Ouroboros.Api              # API externa: http://localhost:5141 (https://localhost:7135 no perfil https)
dotnet run --project src/Ouroboros.Backoffice.Api   # backoffice: http://localhost:5142 (https://localhost:7136 no perfil https)
```

### Endpoints e limites

Todo endpoint exige um access token válido da audiência do listener (`Authorization: Bearer <token>`), salvo os marcados com `[AllowAnonymous]`; os endpoints administrativos exigem um token de `Admin`. Os únicos endpoints anônimos são os de login, `POST /api/v1/identity/sessions` no listener público e `POST /api/v1/admin/identity/sessions` no administrativo ([Fazer login com e-mail e senha](identity/login.md)), que devolvem o access token, e os de renovação, `POST /api/v1/identity/sessions/refresh` e `POST /api/v1/admin/identity/sessions/refresh` ([Renovar o access token com rotação do refresh token](identity/refresh-session.md)), que trocam o refresh token por um par novo, e os de logout, `POST /api/v1/identity/sessions/logout` e `POST /api/v1/admin/identity/sessions/logout` ([Encerrar a sessão atual](identity/logout.md)), que encerram a sessão do refresh token. Os de logout-all, `POST /api/v1/identity/sessions/logout-all` e `POST /api/v1/admin/identity/sessions/logout-all` ([Encerrar todas as sessões](identity/logout-all.md)), exigem access token e encerram todas as sessões do usuário do token. Os de consulta do próprio usuário, `GET /api/v1/identity/me` e `GET /api/v1/admin/identity/me` ([Consultar o próprio usuário](identity/me.md)), também exigem access token. Só no listener público, a solicitação e a conclusão do cadastro de cliente, `POST /api/v1/identity/registrations` e `POST /api/v1/identity/registrations/complete` ([Cadastrar cliente por e-mail](identity/registration.md)), e a solicitação e a conclusão da redefinição de senha, `POST /api/v1/identity/password-resets` e `POST /api/v1/identity/password-resets/complete` ([Redefinir a senha por e-mail](identity/password-reset.md)), também são anônimas. Em cada público, o login aceita 10 requisições por minuto de cada IP, a renovação e o logout, 30 cada, a solicitação de cadastro, 5, a conclusão do cadastro, 10, a solicitação de redefinição de senha, 5, e a conclusão da redefinição, 10; acima disso, respondem `429` com `Retry-After`. Os limites ficam em `Identity:RateLimiting:Policies` no `appsettings.json`, com contadores separados por público, e valores ausentes ou não positivos impedem a subida ([Limitar requisições de autenticação por IP](identity/auth-rate-limiting.md)). Fora deles, uma requisição sem token responde 401 em qualquer rota, inclusive inexistente: a resposta não revela quais rotas existem. Com um token válido, a rota inexistente responde 404. As duas respostas saem em ProblemDetails ([Respostas de erro com ProblemDetails](architecture/error-responses.md)), e o 401 não explica por que o token foi recusado. As rotas administrativas (`/api/v1/admin/...`) só existem no listener administrativo, e as demais, só no público; no deploy, o listener administrativo deve ficar só em loopback ou na rede interna ([APIs e workers](architecture/hosts.md)).

## E-mails em desenvolvimento

Não há servidor de e-mail. No ambiente `Development`, a API de Identity (e os hosts agregadores) grava cada e-mail enviado como um arquivo `.eml` em `.local/emails/`, na raiz do repositório (pasta ignorada pelo Git); abra o arquivo em qualquer cliente de e-mail. O log registra só o nome do arquivo, nunca o corpo nem tokens. Hoje, enviam e-mail a solicitação de cadastro de cliente, com o link para concluir o cadastro ([Cadastrar um cliente e entrar](#cadastrar-um-cliente-e-entrar)), e a solicitação de redefinição de senha, com o link para redefinir a senha ([Redefinir a senha por e-mail](identity/password-reset.md)); o convite de administradores vai usar o mesmo envio.

A configuração fica na seção `Identity:Email` dos `appsettings.Development.json`. O adaptador local só é aceito em `Development`: em outro ambiente, o host não sobe. Sem provedor configurado (o padrão fora de `Development`), o host sobe, mas qualquer envio falha com erro explícito. Detalhes em [Envio de e-mail por porta com adaptador local](architecture/email.md).

| Chave em Identity:Email | Configuração disponível |
| --- | --- |
| Provider | `PickupDirectory` em Development; `None` por padrão |
| PickupDirectory | `../../../../.local/emails` na API de Identity (`../../.local/emails` nos hosts agregadores), resolvido a partir do content root do projeto; obrigatório para PickupDirectory |
| From | `nao-responda@ouroboros.local` por padrão |

Cada arquivo usa nome com instante/UUID, texto UTF-8 sem BOM, quebras CRLF, cabeçalhos MIME e assunto não ASCII codificado. O link é segredo: não copie seu conteúdo para logs ou issues. A solicitação responde 202 mesmo se o envio após commit falhar, preservando proteção contra enumeração; o checkout não recupera automaticamente a intenção perdida entre commit e envio.

## Cadastrar um cliente e entrar

Clientes se cadastram sozinhos pelo listener público da API de Identity, em duas etapas: informam o e-mail, recebem um link de uso único e, com ele, definem a senha. Ainda não há front-end; em desenvolvimento, o link chega como `.eml` em `.local/emails/` e o token é copiado dele. Com o banco configurado, as migrations de Identity aplicadas, incluindo seus parâmetros locais, e a API de Identity rodando no ambiente `Development` (`dotnet run --project src/Services/Identity/Ouroboros.Identity.Api`), na raiz do repositório:

1. Solicite o cadastro. A resposta é sempre `202`, com a mesma mensagem, tenha o e-mail conta ou não:

   ```powershell
   Invoke-RestMethod -Method Post -Uri http://localhost:5141/api/v1/identity/registrations `
     -ContentType 'application/json' -Body '{"email":"cliente@example.com"}'
   ```

2. Abra o `.eml` mais recente de `.local/emails/` e copie o token do link `http://localhost:3000/cadastro/concluir?token=<token>`, válido por 24 horas. Ou, no PowerShell:

   ```powershell
   $eml = Get-ChildItem .local/emails/*.eml | Sort-Object LastWriteTime | Select-Object -Last 1
   $token = [regex]::Match((Get-Content $eml -Raw), 'token=([A-Za-z0-9_-]+)').Groups[1].Value
   ```

   Se o e-mail já tem conta, nenhum arquivo novo aparece. Uma nova solicitação gera outro link e invalida o anterior.

3. Conclua o cadastro com o token e a senha. A senha segue a [política de senha](architecture/password-policy.md) (15 a 128 caracteres, sem regras de composição, sem ser comum nem conter `ouroboros` ou a parte local do e-mail):

   ```powershell
   $body = @{ token = $token; password = '<senha>' } | ConvertTo-Json
   Invoke-RestMethod -Method Post -Uri http://localhost:5141/api/v1/identity/registrations/complete `
     -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
   ```

   `204`: a conta foi criada, ativa e com o papel `Customer`, e o link não vale mais. `400` com o `type` `password-rejected`: a senha foi recusada, o `detail` diz por quê, e o mesmo link pode ser usado de novo. `400` com o `type` `invalid-registration-token`: o link é inválido, expirou, já foi usado ou foi substituído por um mais novo; solicite o cadastro outra vez.

4. Entre pelo login do listener público, que devolve o access token e o refresh token (a conclusão não faz login automático):

   ```powershell
   $login = @{ email = 'cliente@example.com'; password = '<senha>' } | ConvertTo-Json
   Invoke-RestMethod -Method Post -Uri http://localhost:5141/api/v1/identity/sessions `
     -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($login))
   ```

Detalhes, regras e erros em [Cadastrar cliente por e-mail](identity/registration.md) e [Fazer login com e-mail e senha](identity/login.md). Para trocar a senha esquecida pelo mesmo caminho (solicitar o link, copiar o token do `.eml` e concluir com a senha nova), veja o [fluxo completo da redefinição de senha](identity/password-reset.md#fluxo-completo-em-desenvolvimento).

## Redefinir a senha em desenvolvimento

Com a API de Identity no ar em `Development` (listener público em `http://localhost:5141` no perfil padrão) e uma conta de cliente criada. Ainda não há front-end; o token é copiado do link do e-mail.

```powershell
# 1. Solicitar a redefinição
Invoke-RestMethod -Method Post -Uri http://localhost:5141/api/v1/identity/password-resets `
  -ContentType 'application/json' -Body '{"email":"cliente@example.com"}'

# 2. Ler o token no .eml mais recente (o link termina em ?token=<token>)
$eml = Get-ChildItem .local/emails/*.eml | Sort-Object LastWriteTime | Select-Object -Last 1
$token = [regex]::Match((Get-Content $eml -Raw), 'token=([A-Za-z0-9_-]+)').Groups[1].Value

# 3. Concluir a redefinição com a senha nova
$body = @{ token = $token; password = 'outra frase longa que só eu conheço' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://localhost:5141/api/v1/identity/password-resets/complete `
  -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))

# 4. Entrar com a senha nova
$login = @{ email = 'cliente@example.com'; password = 'outra frase longa que só eu conheço' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://localhost:5141/api/v1/identity/sessions `
  -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($login))
```

O passo 2 roda na raiz do repositório, como no [fluxo do cadastro](#cadastrar-um-cliente-e-entrar). Depois do passo 3, a senha antiga recebe `401` no login, e os refresh tokens emitidos antes deixam de renovar.

## Exemplos HTTP por funcionalidade

Execute a API de Identity conforme este guia (listener público em `http://localhost:5141`, administrativo em `http://localhost:5142`). Nos exemplos autenticados, substitua `<accessToken>` pelo token do login e `<refreshToken>` pelo refresh token vigente do mesmo público. Senhas e tokens dos exemplos são marcadores; não publique valores reais.

### Fazer login

Contrato e pré-requisitos: [Fazer login](identity/login.md#como-usar).

```powershell
# PowerShell
$body = @{ email = 'admin@example.com'; password = '<senha>' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://localhost:5142/api/v1/admin/identity/sessions -ContentType 'application/json' -Body $body
```

### Encerrar todas as sessões

Contrato e pré-requisitos: [Encerrar todas as sessões](identity/logout-all.md#como-usar).

```powershell
# PowerShell
$headers = @{ Authorization = 'Bearer <access token atual>' }
Invoke-RestMethod -Method Post -Uri http://localhost:5141/api/v1/identity/sessions/logout-all -Headers $headers
```

### Encerrar a sessão

Contrato e pré-requisitos: [Encerrar a sessão](identity/logout.md#como-usar).

```powershell
# PowerShell
$body = @{ refreshToken = '<refresh token atual>' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://localhost:5141/api/v1/identity/sessions/logout -ContentType 'application/json' -Body $body
```

### Consultar o próprio usuário

Contrato e pré-requisitos: [Consultar o próprio usuário](identity/me.md#como-usar).

```powershell
# PowerShell
$headers = @{ Authorization = 'Bearer <access token atual>' }
Invoke-RestMethod -Uri http://localhost:5141/api/v1/identity/me -Headers $headers
```

### Renovar a sessão

Contrato e pré-requisitos: [Renovar a sessão](identity/refresh-session.md#como-usar).

```powershell
# PowerShell
$body = @{ refreshToken = '<refresh token do login>' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://localhost:5142/api/v1/admin/identity/sessions/refresh -ContentType 'application/json' -Body $body
```
