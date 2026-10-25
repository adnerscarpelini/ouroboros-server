# Executar o checkout atual

Este guia contém comandos dos projetos disponíveis. A organização Services e os executáveis independentes são acompanhados no [estado de implementação](implementation-status.md). Não execute caminhos futuros como se já existissem.

## Requisitos

- .NET SDK 10.0.402, fixado em `global.json`.
- Visual Studio compatível com .NET 10 e SLNX, se for usar o Visual Studio.
- Docker com Docker Compose, para PostgreSQL e RabbitMQ de desenvolvimento; os testes de integração também exigem o Docker em execução.
- PowerShell 7 (`pwsh`) para provisionar o RabbitMQ e para sua fixture de teste, inclusive em Linux.

## Visual Studio e compilação

Abra `Ouroboros.slnx` por **Arquivo → Abrir → Projeto/Solução**. Abrir somente a pasta do repositório usa a visualização de arquivos. `src`, `src/Services` e `src/Services/Identity` são pastas organizadoras da solution. Identity é a biblioteca `Ouroboros.Identity` e a API `Ouroboros.Identity.Api`, em `src/Services/Identity/`; `Ouroboros.BuildingBlocks`, `Ouroboros.Hosting` e `Ouroboros.Messaging` ficam em `src/`. Cada projeto é independente e compilável pelo menu de contexto **Compilar**.

Defina `Ouroboros.Identity.Api` como projeto de inicialização para executar Identity (um processo, com os listeners público e administrativo). Notifications possui o executável separado `Ouroboros.Notifications.Worker`; as bibliotecas não são executáveis independentes.

Na raiz do repositório:

```powershell
dotnet restore Ouroboros.slnx
dotnet build Ouroboros.slnx --no-restore
dotnet build src/Services/Identity/Ouroboros.Identity/Ouroboros.Identity.csproj
dotnet build src/Services/Identity/Ouroboros.Identity.Api/Ouroboros.Identity.Api.csproj
```

## Testes

Na raiz do repositório:

```powershell
dotnet test --solution Ouroboros.slnx                     # toda a suíte: arquitetura e integração
dotnet test --project tests/Ouroboros.ArchitectureTests   # só arquitetura (sem Docker)
dotnet test --project tests/Ouroboros.IntegrationTests    # só integração (exige Docker)
```

- A API de Identity e o worker de Notifications têm testes próprios; os testes de Notifications incluem processo filho real e RabbitMQ. Os testes de arquitetura não acessam o banco: sobem a API de Identity com uma connection string fictícia e um manifesto externo de chaves RSA efêmeras. Os testes não usam as chaves dos user-secrets.
- Os testes de listeners em Kestrel real ([`KestrelListenerTests`](../tests/Ouroboros.IntegrationTests/KestrelListenerTests.cs)) abrem portas livres de loopback e exigem Docker pelo banco. O teste de que o listener administrativo não responde por endereço da máquina é ignorado quando a máquina não tem endereço IPv4 fora de loopback.
- Os testes de dependência analisam os assemblies de todos os projetos de `src`, listados em `architecture-assemblies.txt`, gerado no build do projeto de testes. Compile antes (`dotnet build Ouroboros.slnx`); sem o manifesto, o teste falha pedindo a compilação. Um serviço ou executável novo em `src` é analisado automaticamente. Regras e lista de transição em [Dependências entre camadas e serviços](architecture/layer-dependencies.md#verificação).
- Os testes de integração exigem o Docker em execução. Eles sobem o próprio PostgreSQL em container com Testcontainers e não usam o banco do Docker Compose, o `.env` nem os user-secrets. A primeira execução baixa a imagem `postgres:18-alpine`. Os testes de mensageria também baixam `rabbitmq:4.4.0-management` e exigem `pwsh`; contratos e topologia estão descritos em [RabbitMQ e contrato de mensagem](architecture/messaging.md#como-verificar).
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
| Testes de integração | Testes de integração com PostgreSQL e RabbitMQ em containers (Testcontainers); confere a disponibilidade de PowerShell para o provisionador | `dotnet test --project tests/Ouroboros.IntegrationTests -c Release` |

Falha de build, de teste ou de job reprova o check. Os resultados em formato TRX ficam como artefato do workflow por 7 dias. Um passo do primeiro job falha se existir um projeto de teste em `tests/` que não esteja listado no workflow, para que nenhuma suíte deixe de rodar sem aviso. Os nomes dos checks são os que o ruleset da `main` deve exigir.

### Fixtures e adaptadores de teste

O checkout usa [PostgresFixture](../tests/Ouroboros.IntegrationTests/PostgresFixture.cs): um container por suíte e um banco modelo com migrations aplicadas. Cada teste recebe banco clonado, isolado dos demais. [IntegrationTest](../tests/Ouroboros.IntegrationTests/IntegrationTest.cs) cria a API de Identity e remove somente seu banco ao terminar. A [RabbitMqFixture](../tests/Ouroboros.IntegrationTests/RabbitMqFixture.cs) cria broker e volume exclusivos de teste, gera credenciais efêmeras e executa o mesmo `provision-rabbitmq.ps1` da operação local; remove somente seus recursos. Os testes verificam contrato, permissões, reinício e reprovisionamento, sem serviço de e-mail conectado.

[IntegrationApiFactory](../tests/Ouroboros.IntegrationTests/IntegrationApiFactory.cs) inicia a API de Identity (`Ouroboros.Identity.Api`) em Testing, com configuração em memória que sobrepõe connection string e chave fictícia. `CreateClient()` fala com o listener público e `CreateAdminClient()`, com o administrativo; `BackofficeIntegrationApiFactory`, declarada no mesmo arquivo, faz todas as requisições do cliente chegarem ao listener administrativo. Como o `TestServer` não tem sockets, o listener é escolhido pelo cabeçalho de teste `X-Test-Listener` (`admin`), lido só por um resolvedor registrado no projeto de testes; a produção ignora o cabeçalho. A separação por portas é verificada em `KestrelListenerTests`, com Kestrel real. Limites elevados evitam interferência nos testes de negócio; RateLimitingTests usa limites próprios; `ForwardedHeadersTests` usa o cabeçalho de teste `X-Test-Client-IP` para definir o IP da conexão.

A `PostgresFixture` aplica as migrations de Identity no banco modelo por meio dessa fábrica; os `DbContext` do serviço são descobertos por reflexão nos assemblies `Ouroboros.*` referenciados pela API ([ModuleDbContexts](../tests/Ouroboros.IntegrationTests/ModuleDbContexts.cs)), de modo que um serviço novo entra sem alterar os testes. O schema legado `settings` é reproduzido pelo SQL versionado [settings-legacy-schema.sql](../tests/Ouroboros.IntegrationTests/Resources/settings-legacy-schema.sql), aplicado por `IntegrationTest.ApplyLegacySettingsSchemaAsync` e pelos testes da migration `OwnIdentityParameters` ([IdentityParameterMigrationTests](../tests/Ouroboros.IntegrationTests/IdentityParameterMigrationTests.cs)), sem depender de um projeto Settings. Nos testes de arquitetura, `OuroborosApiFactory` e `BackofficeApiFactory` ([OuroborosApiFactory.cs](../tests/Ouroboros.ArchitectureTests/OuroborosApiFactory.cs)) sobem a API de Identity com banco fictício, no listener público e no administrativo. Casos que dependem de tempo ou corridas substituem TimeProvider/hasher por adaptadores controlados. [CapturingEmailSender](../tests/Ouroboros.IntegrationTests/CapturingEmailSender.cs) captura links e simula falha sem envio real; EmailTests verifica o adaptador .eml. Isso não comprova entrega distribuída.

## RabbitMQ local

O broker é infraestrutura separada da API e do PostgreSQL. O Compose usa `rabbitmq:4.4.0-management`, hostname estável, vhost `ouroboros` e volume `rabbitmq-data`. Identity ainda não publica nesta etapa. O worker da #72 tem consumo direto restrito a Development; o fluxo confiável depende das #85–#87. [Topologia, contratos e limites](architecture/messaging.md).

1. Se ainda não existe `.env`, copie `.env.example` uma vez, sem sobrescrever um arquivo existente. Troque as quatro senhas do broker por valores aleatórios **distintos**, de pelo menos 32 caracteres; placeholders `troque-` são recusados pelo provisionador. Use, por exemplo, quatro valores hexadecimais gerados pelo gerenciador de segredos. Hexadecimal evita diferenças de interpretação de `$`/aspas entre o Compose e o leitor literal de `-EnvFile`. Não imprima nem registre senhas em histórico, transcript ou argumentos.

   | Variável | Uso |
   | --- | --- |
   | `RABBITMQ_ADMIN_USER` | Operador do broker; padrão de exemplo `broker_operator`, nunca `guest` |
   | `RABBITMQ_ADMIN_PASSWORD` | Bootstrap do volume novo e autenticação do provisionador; somente operação |
   | `IDENTITY_BROKER_PASSWORD` | `identity_publisher`, publica somente no exchange de solicitações |
   | `NOTIFICATIONS_BROKER_PASSWORD` | `notifications_consumer`, lê somente a fila de e-mails |
   | `NOTIFICATIONS_FAILURES_BROKER_PASSWORD` | `notifications_failures`, publica somente referências no exchange de falhas |

   Proteja o `.env` contra leitura ampla: ACL exclusiva da operação no Windows, `0600` no Linux. O provisionador roda na máquina do operador, não recebe connection strings de banco e nunca entrega a credencial administrativa aos serviços. Em volume existente, mudar `RABBITMQ_DEFAULT_*` não altera o administrador já criado: use a credencial vigente e o procedimento administrativo próprio de rotação. O provisionador converge as três credenciais de runtime; não redefine o administrador nem remove usuários alheios.

2. Suba apenas o broker e aguarde o healthcheck:

   ```powershell
   docker compose up -d --wait rabbitmq
   ```

   AMQP usa `127.0.0.1:5672` e gestão `http://127.0.0.1:15672`, alteráveis por `RABBITMQ_PORT`/`RABBITMQ_MANAGEMENT_PORT`. Nenhuma porta de gestão é exposta fora da máquina. HTTP/AMQP sem TLS se restringem ao desenvolvimento local; outros ambientes exigem TLS e certificados, sem desabilitar validação.

3. Provisione explicitamente o vhost, usuários, exchanges, filas e bindings:

   ```powershell
   pwsh -NoProfile -File scripts/messaging/provision-rabbitmq.ps1 -EnvFile .env -LocalDevelopment
   ```

   Com porta de gestão alterada, acrescente `-ManagementUrl http://127.0.0.1:<porta>`. Sem `-EnvFile`, as cinco variáveis de usuário/senhas devem vir do ambiente, carregadas pelo gerenciador de segredos. Variáveis já definidas no processo têm precedência sobre o arquivo; confira sua origem ao trocar credenciais. O leitor de arquivo usa somente a allowlist da tabela, ignora comentários/outros nomes, remove um par de aspas externas e não expande variáveis nem executa o conteúdo. Não use dot-source, `Invoke-Expression`, `-Verbose`, `-Debug` ou transcript para carregar/exibir os segredos.

   A saída de sucesso é `Topologia RabbitMQ e permissões convergidas; nenhum dado removido.` Uma falha interrompe o script com indicação sanitizada da operação; não repita a senha em logs para investigar. Reexecute o mesmo comando após corrigir a causa: ele não apaga mensagens. Uma incompatibilidade de argumentos de uma fila existente exige análise do operador, sem apagá-la para fazer a declaração passar.

4. Para testar reinício local sem descartar persistência, use `docker compose restart rabbitmq` e reexecute o provisionador. Para parar somente o broker, use `docker compose stop rabbitmq`. `docker compose down` mantém os volumes, mas para todos os containers do projeto. Preserve `rabbitmq-data`, `postgres-data` e backups; os comandos documentados não removem volumes.

A fila principal tem 64 MiB e rejeita novas publicações quando cheia, sem TTL/DLX do corpo original. A fila de falhas tem 8 MiB e TTL máximo de 30 dias, somente para referências sanitizadas. O consumidor inicial da #72 só funciona em Development e reconhece expiração sem persistência; limpeza/classificação durável dependem da #87 antes de habilitar o fluxo operacional. A recuperação por outbox é a #86.

Testes focados, com Docker e `pwsh` disponíveis, sem usar esse broker ou `.env`:

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class '*MessageContractTests'
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class '*RabbitMqTopologyTests'
```

## Banco de dados local

A API de Identity lê a connection string de `ConnectionStrings:Identity`, o banco próprio do serviço (`ouroboros_identity`, role `identity_runtime`). Quando essa chave não está definida, usa `ConnectionStrings:Ouroboros`, o banco original da instância: é o caso até o operador fazer a troca liberada pelo [cutover da transferência](#transferir-o-banco-de-identity) ou configurar uma instalação nova diretamente no banco próprio. Sem nenhuma das duas, falha ao subir com `OptionsValidationException` e uma mensagem que cita as duas chaves. As migrations nunca são aplicadas na subida; rode-as por comando. Todos os comandos abaixo são executados na raiz do repositório.

1. Crie o `.env` a partir do modelo e troque a senha de `POSTGRES_PASSWORD`, além das quatro senhas por serviço quando for [provisionar os bancos](#provisionar-bancos-e-roles-por-serviço) (o `.env` fica fora do Git):

   ```powershell
   cp .env.example .env
   ```

2. Suba o PostgreSQL e aguarde o healthcheck:

   ```powershell
   docker compose up -d --wait postgres
   ```

3. Grave a connection string do **banco original** nos user-secrets do projeto `Ouroboros.Identity.Api`, com os valores do `.env` (credencial administrativa da instância; uso restrito ao desenvolvimento local). Os segredos ficam no `UserSecretsId` do `Ouroboros.Identity.Api.csproj`:

   ```powershell
   dotnet user-secrets set "ConnectionStrings:Ouroboros" "Host=localhost;Port=5432;Database=ouroboros;Username=ouroboros;Password=<a senha do .env>" --project src/Services/Identity/Ouroboros.Identity.Api
   ```

   Para usar o banco próprio e a role de runtime, [provisione](#provisionar-bancos-e-roles-por-serviço) a instância e defina `ConnectionStrings:Identity`, que tem precedência sobre `ConnectionStrings:Ouroboros`. Em ambiente com dados, essa troca só vale depois da [transferência](#transferir-o-banco-de-identity); em instalação nova, veja o passo 4.

4. Para **instalação nova**, sem tabelas legadas de parâmetros, restaure a ferramenta local `dotnet-ef` e aplique as migrations de Identity. Para **banco existente**, antes de qualquer atualização siga o [procedimento de backup restaurado, janela sem escrita e migração dos parâmetros](identity/parameters.md#upgrade-de-banco-existente) e, em seguida, a [transferência do banco de Identity](#transferir-o-banco-de-identity). O projeto Settings não existe mais: o banco legado conserva o schema `settings`, que a migration `OwnIdentityParameters` copia, e este checkout não aplica migrations de Settings. Ambientes que nunca aplicaram as migrations antigas de Settings usam a instalação limpa ou, para aplicá-las, o histórico do Git anterior à #96.

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

   `OwnIdentityParameters` cria sementes com `uuidv7()` em instalação limpa, exigindo PostgreSQL 18 (a versão do `compose.yaml`). No upgrade, copia e valida os parâmetros e o histórico do schema `settings` na mesma transação. A migration não faz backup automaticamente nem exclui a origem; não ative a versão nova se ela falhar.

   No primeiro `database update` em um banco vazio, o log mostra `Failed executing DbCommand` sobre `__EFMigrationsHistory` antes de criar a tabela. É um comportamento conhecido do EF Core: o comando termina com `Done.`. Repetir o comando não altera nada.

5. Opcionalmente, confira os schemas: `identity` contém `users`, `sessions`, `refresh_tokens`, `email_tokens`, `parameters` e `parameter_history`. Em banco migrado, `settings` continua retido, sem novas escritas e sem projeto que o acesse (seu destino é a #97); não é necessário criá-lo em instalação limpa. Ajuste usuário e banco se mudou o `.env`:

   ```powershell
   docker compose exec postgres psql -U ouroboros -d ouroboros -c "\dn"
   ```

Para gerar uma nova migration do Identity:

```powershell
dotnet ef migrations add <Nome> --project src/Services/Identity/Ouroboros.Identity --startup-project src/Services/Identity/Ouroboros.Identity.Api --context IdentityDbContext --output-dir Infrastructure/Persistence/Migrations
```

`docker compose down` para os containers do projeto, incluindo RabbitMQ se iniciado, e mantém os dados nos volumes `postgres-data` e `rabbitmq-data`. Para parar somente o banco, use `docker compose stop postgres`. Preserve os volumes e os backups durante a migração. Fora de desenvolvimento, a connection string vem da variável de ambiente `ConnectionStrings__Identity` (banco próprio) ou, enquanto não houver troca, `ConnectionStrings__Ouroboros`. Uma `ConnectionStrings__Identity` esquecida no ambiente continua tendo precedência sobre `ConnectionStrings:Ouroboros`: confira-a ao voltar para o banco original. Detalhes e o padrão para outros serviços estão em [Persistência com PostgreSQL e EF Core](architecture/persistence.md).

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
- Todos os escritores parados: API de Identity, workers, `create-admin` e comandos de migration. O procedimento não os para.
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

Fora de desenvolvimento, use a variável `ConnectionStrings__Identity`. Senha com `;` ou aspas exige aspas no valor da connection string. Depois da troca, sessões, refresh tokens, senhas, tokens de e-mail pendentes e parâmetros com histórico continuam válidos. Migrations do banco próprio: comando explícito com a role de migration, descrito no passo 4 de [Banco de dados local](#banco-de-dados-local). O desligamento do banco original e a revogação da credencial antiga não fazem parte deste procedimento (#97).

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

A API de Identity exige `Authentication:Jwt:KeyRingPath`, caminho absoluto de um manifesto JSON externo. Ela emite RS256, exige RSA de pelo menos 2048 bits e valida o par público/privado na subida. Configuração ausente, manifesto inválido, chave ilegível ou par divergente impedem iniciar. Não há geração automática ou fallback para a configuração antiga `SigningKey`.

O diretório deve ficar fora do checkout, de qualquer contexto de build e das imagens. Só a conta de operação pode escrever o manifesto e as chaves; só o processo de Identity deve ler a privada. Em produção, o operador monta os arquivos como segredos somente de leitura no emissor, com caminhos absolutos válidos dentro dele. Outros serviços recebem apenas a URL HTTPS de JWKS. Não copie o diretório para Dockerfile, volume público, artefato de CI ou backup sem proteção e acesso restrito. A aplicação não configura ACL nem apaga segredos por conta própria.

### Gerar a primeira chave

O exemplo usa PowerShell 7 e .NET, sem imprimir a privada. Informe um diretório **novo**, externo ao código; em desenvolvimento, execute a geração com a conta que executará Identity. Para outra conta de runtime, o operador concede a ela leitura e travessia, preservando escrita exclusiva da operação.

```powershell
$keyDirectory = [IO.Path]::GetFullPath((Read-Host 'Diretório externo novo para as chaves'))
if (Test-Path -LiteralPath $keyDirectory) { throw 'Escolha um diretório novo' }
$ancestor = [IO.DirectoryInfo]::new($keyDirectory)
while ($null -ne $ancestor) {
    if (Test-Path -LiteralPath (Join-Path $ancestor.FullName '.git')) { throw 'Diretório dentro de um checkout' }
    $ancestor = $ancestor.Parent
}
[void][IO.Directory]::CreateDirectory($keyDirectory)
if ($IsWindows) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = Get-Acl -LiteralPath $keyDirectory
    $acl.SetAccessRuleProtection($true, $false)
    $rule = [Security.AccessControl.FileSystemAccessRule]::new(
        $sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($rule)
    Set-Acl -LiteralPath $keyDirectory -AclObject $acl
} else {
    [IO.File]::SetUnixFileMode($keyDirectory, [IO.UnixFileMode]::UserRead -bor
        [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
}

function New-RsaSigningPair([string] $directory, [string] $kid) {
    if ($kid -notmatch '^[a-zA-Z0-9-]{1,80}$') { throw 'kid inválido' }
    $privatePath = Join-Path $directory "$kid.pem"
    if (Test-Path -LiteralPath $privatePath) { throw 'Chave já existe' }
    $rsa = [Security.Cryptography.RSA]::Create(3072)
    try {
        [IO.File]::WriteAllText($privatePath, $rsa.ExportPkcs8PrivateKeyPem(), [Text.UTF8Encoding]::new($false))
        if (-not $IsWindows) {
            [IO.File]::SetUnixFileMode($privatePath, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
        }
        $public = $rsa.ExportParameters($false)
        return @{
            kid = $kid
            n = [Convert]::ToBase64String($public.Modulus).TrimEnd('=').Replace('+', '-').Replace('/', '_')
            e = [Convert]::ToBase64String($public.Exponent).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        }
    } finally { $rsa.Dispose() }
}

$kid = 'identity-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddHHmmss')
$publicEntry = New-RsaSigningPair $keyDirectory $kid
$manifestPath = Join-Path $keyDirectory 'ring.json'
$manifest = @{
    activeKid = $kid
    privateKeyPath = Join-Path $keyDirectory "$kid.pem"
    keys = @($publicEntry)
}
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
if (-not $IsWindows) {
    [IO.File]::SetUnixFileMode($manifestPath, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
}
dotnet user-secrets set 'Authentication:Jwt:KeyRingPath' $manifestPath --project src/Services/Identity/Ouroboros.Identity.Api
```

No Windows, confira `Get-Acl -LiteralPath $keyDirectory` e os dois arquivos: sem herança de leitura ampla, com acesso apenas da operação e da conta de Identity. No Linux, confira `stat`: diretório `0700`, privada e manifesto `0600`, com dono correto. Se as contas forem diferentes, configure grupo/ACL específico: runtime lê e atravessa, mas não escreve. Não amplie acesso para `Everyone`/`Users` ou `other` para resolver erro de leitura.

Fora de Development, defina `Authentication__Jwt__KeyRingPath` com o caminho do manifesto montado. Emissor, audiências e tempos permanecem no `appsettings.json`; [decisão e validação](architecture/authentication.md).

Os valores de `Authentication:Jwt` disponíveis são:

| Chave | Valor no checkout |
| --- | --- |
| Issuer | `ouroboros` |
| Audience | `ouroboros-api`: tokens de `Customer`, aceitos no listener público |
| AdminAudience | `ouroboros-backoffice`: tokens de `Admin`, aceitos no listener administrativo. Obrigatória na API de Identity e diferente de `Audience` |
| AccessTokenLifetime | `00:15:00` |
| RefreshTokenLifetime | `7.00:00:00`, contados novamente a cada renovação |
| KeyRingPath | Externo ao Git, caminho absoluto do manifesto; privada PEM PKCS#8 apontada por `privateKeyPath` |

Emissor/audiência ausentes (inclusive `AdminAudience` na API de Identity ou igual a `Audience`) e durações não positivas também impedem a subida. A tolerância de relógio continua em 30 segundos; não há consulta à sessão em cada validação de access token. Refresh token e logout preservam os contratos anteriores.

### Rotacionar uma chave RSA

Use `kid` novo para cada par, sem reaproveitá-lo. O manifesto admite até 16 públicas, tem limite de 64 KiB e não contém PEM privado. A operação abaixo usa as variáveis e a função da geração inicial, com `ring.json` já existente; recarregue `$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json -AsHashtable` antes de editar se abriu outra sessão.

1. Gere o próximo par com `New-RsaSigningPair`, no mesmo diretório protegido. Acrescente sua entrada pública a `keys`, mantendo `activeKid` e `privateKeyPath` anteriores. Para gravar cada nova versão, escreva JSON num arquivo temporário **no mesmo diretório**, aplique a mesma ACL/`0600` e substitua o manifesto atomicamente com `[IO.File]::Move($temporaryPath, $manifestPath, $true)`. A troca exige filesystem que suporte rename atômico; não edite o arquivo em uso diretamente. O runtime relê o manifesto a cada operação; não é necessário reiniciar consumidores.
2. Confira `GET /api/v1/identity/keys` e aguarde pelo menos cinco minutos para os caches receberem a próxima pública. A resposta é anônima, só no listener externo, com cache de 300 s e limite inicial de 60 requisições/minuto por IP.
3. Troque `activeKid` e `privateKeyPath` para o próximo par e substitua o manifesto atomicamente. Na entrada anterior, defina `retiredAt` como o instante UTC em que a última emissão antiga cessou e `acceptUntil` pelo menos **20 min 30 s** depois (15 min de access token + 30 s + 5 min de cache). Um intervalo de 21 minutos atende aos valores atuais. Em múltiplos emissores, calcule a janela a partir do último a trocar, sem retirar a pública antes disso. Confira que novos tokens trazem o novo `kid` e que um token antigo ainda válido continua aceito.
4. Depois que todos os emissores usarem o próximo par, remova a privada antiga do armazenamento/montagem de emissão e suas cópias operacionais que não sejam necessárias para recuperação protegida. A pública anterior permanece até `acceptUntil`; nesse instante, Identity para de resolvê-la e de publicá-la no JWKS. Depois do prazo, retire a entrada do manifesto. Não reduza a janela para compensar atraso de operação.

Exemplo da mudança de estado da entrada anterior, sem material de chave:

```powershell
$retiredAt = [DateTimeOffset]::UtcNow
$previousEntry.retiredAt = $retiredAt.ToString('O')
$previousEntry.acceptUntil = $retiredAt.AddMinutes(21).ToString('O')
```

O consumidor deve manter cache de cinco minutos e limitar novas buscas a uma por 30 segundos, inclusive para `kid` desconhecido. Sem uma chave válida em cache e com JWKS indisponível, deve recusar acesso, sem fallback ou cache vencido. O host de prova dos testes demonstra essa política; não é uma biblioteca cliente implantada. Consulte [chaves públicas e cache](architecture/authentication.md#chaves-públicas-e-cache).

### Transição de HS256

Instalação nova começa diretamente com RSA, sem `legacy`. Para preservar access tokens HS256 existentes, o operador faz uma única janela de transição, coordenada com a interrupção de todas as emissões antigas:

1. Copie o **valor exato** da antiga `Authentication:Jwt:SigningKey` para um arquivo de texto externo protegido, sem quebra de linha adicional. Não decodifique Base64: o emissor antigo usava os bytes UTF-8 do texto, com pelo menos 32 bytes. Faça a cópia pelo gerenciador de segredos, sem imprimir a chave ou colocá-la em argumento, histórico do shell ou transcript. Aplique as mesmas ACLs/`0600` da privada RSA.
2. Acrescente ao manifesto `legacy: { keyPath, startsAt, endsAt }`, com caminho absoluto do arquivo e instantes UTC absolutos. `startsAt` é a troca para emissão RSA; `endsAt` deve ser no máximo 15 minutos depois, conforme `AccessTokenLifetime`. Exemplo para compor apenas metadados, usando `$legacyPath` já definido pelo operador:

   ```powershell
   $startsAt = [DateTimeOffset]::UtcNow
   $manifest.legacy = @{
       keyPath = $legacyPath
       startsAt = $startsAt.ToString('O')
       endsAt = $startsAt.AddMinutes(15).ToString('O')
   }
   ```

3. Publique o manifesto atomicamente conforme a rotação e inicie a emissão RSA. Durante `startsAt <= agora < endsAt`, ambos os algoritmos são aceitos por Identity, cada um com sua própria chave. Não estenda nem recrie o prazo num reinício; mantenha o mesmo limite absoluto em todos os emissores. Consumidores novos recebem somente as públicas RSA e não validam HS256.
4. Ao atingir `endsAt`, HS256 é recusado mesmo se o arquivo ainda existir, e Identity não o abre mais. Retire `legacy` do manifesto, remova o arquivo legado pelo caminho absoluto conferido e elimine o segredo de todas as fontes antigas. Remova o user-secret com `dotnet user-secrets remove 'Authentication:Jwt:SigningKey' --project src/Services/Identity/Ouroboros.Identity.Api`; retire também `Authentication__Jwt__SigningKey` da configuração do processo e do gerenciador de segredos. Não basta retirar uma fonte enquanto outra ainda guarda o segredo. A aplicação não apaga esses arquivos/configurações automaticamente.

O prazo HS256 não inclui tolerância extra para transição: tokens antigos ainda dentro da tolerância de relógio podem exigir renovação ao atingir o limite. Uma implantação com versões antigas e novas emitindo simultaneamente precisa cessar a emissão HS256 antes de iniciar a janela, para não truncar a vida dos últimos tokens. Não use rollback para reativar o prazo ou voltar a emitir HS256.

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

Os user-secrets (connection string e caminho do manifesto de assinatura) só são lidos no ambiente `Development`; com `--no-launch-profile`, o ambiente passa a ser `Production` e a API não encontra essa configuração, a menos que você defina `ASPNETCORE_ENVIRONMENT=Development` ou as variáveis `ConnectionStrings__Identity` (ou `ConnectionStrings__Ouroboros`, enquanto não houver troca) e `Authentication__Jwt__KeyRingPath`. Em `Production`, defina também as URLs dos listeners, se os padrões não servirem.

### Endpoints e limites

Todo endpoint exige um access token válido da audiência do listener (`Authorization: Bearer <token>`), salvo os marcados com `[AllowAnonymous]`; os endpoints administrativos exigem um token de `Admin`. O JWKS `GET /api/v1/identity/keys` é anônimo no listener público, com 60 requisições/minuto por IP e cache de cinco minutos. Os demais endpoints anônimos são os de login, `POST /api/v1/identity/sessions` no listener público e `POST /api/v1/admin/identity/sessions` no administrativo ([Fazer login com e-mail e senha](identity/login.md)), que devolvem o access token, e os de renovação, `POST /api/v1/identity/sessions/refresh` e `POST /api/v1/admin/identity/sessions/refresh` ([Renovar o access token com rotação do refresh token](identity/refresh-session.md)), que trocam o refresh token por um par novo, e os de logout, `POST /api/v1/identity/sessions/logout` e `POST /api/v1/admin/identity/sessions/logout` ([Encerrar a sessão atual](identity/logout.md)), que encerram a sessão do refresh token. Os de logout-all, `POST /api/v1/identity/sessions/logout-all` e `POST /api/v1/admin/identity/sessions/logout-all` ([Encerrar todas as sessões](identity/logout-all.md)), exigem access token e encerram todas as sessões do usuário do token. Os de consulta do próprio usuário, `GET /api/v1/identity/me` e `GET /api/v1/admin/identity/me` ([Consultar o próprio usuário](identity/me.md)), também exigem access token. Só no listener público, a solicitação e a conclusão do cadastro de cliente, `POST /api/v1/identity/registrations` e `POST /api/v1/identity/registrations/complete` ([Cadastrar cliente por e-mail](identity/registration.md)), e a solicitação e a conclusão da redefinição de senha, `POST /api/v1/identity/password-resets` e `POST /api/v1/identity/password-resets/complete` ([Redefinir a senha por e-mail](identity/password-reset.md)), também são anônimas. Em cada público, o login aceita 10 requisições por minuto de cada IP, a renovação e o logout, 30 cada, a solicitação de cadastro, 5, a conclusão do cadastro, 10, a solicitação de redefinição de senha, 5, e a conclusão da redefinição, 10; acima disso, respondem `429` com `Retry-After`. Os limites ficam em `Identity:RateLimiting:Policies` no `appsettings.json`, com contadores separados por público, e valores ausentes ou não positivos impedem a subida ([Limitar requisições de autenticação por IP](identity/auth-rate-limiting.md)). Fora deles, uma requisição sem token responde 401 em qualquer rota, inclusive inexistente: a resposta não revela quais rotas existem. Com um token válido, a rota inexistente responde 404. As duas respostas saem em ProblemDetails ([Respostas de erro com ProblemDetails](architecture/error-responses.md)), e o 401 não explica por que o token foi recusado. As rotas administrativas (`/api/v1/admin/...`) só existem no listener administrativo, e as demais, só no público; no deploy, o listener administrativo deve ficar só em loopback ou na rede interna ([APIs e workers](architecture/hosts.md)).

## Executar o worker de Notifications

`Ouroboros.Notifications.Worker` é um processo separado, sem API ou banco nesta etapa. O consumidor vem desligado. A entrega direta só pode ser habilitada em `Development`, com `PickupDirectory`; aceitação durável e deduplicação são a #87. Identity continua usando seu adaptador local, sem publicar no broker ainda. [Comportamento e limites do worker](notifications/worker.md).

### Chaves de proteção de Notifications

Gere um par próprio, diferente da assinatura JWT, num diretório **novo**, externo ao checkout e ao contexto de build. O script não sobrescreve chaves nem aceita diretório existente/dentro de checkout e não imprime material de chave:

```powershell
$protectionDirectory = [IO.Path]::GetFullPath((Read-Host 'Diretório externo novo para proteção de Notifications'))
pwsh -NoProfile -File scripts/messaging/new-notification-protection-key.ps1 -Directory $protectionDirectory
```

O resultado é RSA3072, um PEM privado, `public.json` (`activeKeyId`, `keys` com `keyId`, `n`, `e`) e `private.json` (`keys` com `keyId`, `privateKeyPath`). No Windows, o diretório perde herança ampla e recebe ACL da conta que executou o gerador; no Linux, diretório `0700` e arquivos `0600`. Confira dono e permissões. Só Notifications recebe o manifesto/PEM privados; a operação entregará **somente `public.json`** ao produtor na #85. Para runtime sob outra conta, conceda leitura e travessia específicas, sem escrita ou acesso amplo. Nenhuma chave entra na imagem, Git ou logs.

Rotação é operacional: gere um próximo par em outro diretório novo, com `keyId` novo, distribua a pública antes de ativá-la na origem e conserve no manifesto privado as entradas ainda necessárias para mensagens válidas. Não retire uma privada enquanto houver mensagem recuperável que precise dela, considerando prazos e a futura retenção das #85/#87. Não reutilize chaves de assinatura JWT. O leitor aceita até 16 entradas únicas e relê o manifesto; não há rotação, distribuição ou remoção automática de chaves. Os manifests atualizados devem ser substituídos atomicamente, com as mesmas permissões; o gerador não mescla arquivos existentes.

### Configurar e iniciar

O worker tem `UserSecretsId` próprio, `ouroboros-notifications-development`. Não recebe a configuração de Identity. Opções do [`appsettings.json`](../src/Services/Notifications/Ouroboros.Notifications.Worker/appsettings.json):

| Opção em `Notifications` | Padrão e efeito |
| --- | --- |
| `Consumer:Enabled` | `false`; habilitar exige Development, provedor e proteção/broker válidos |
| `Email:Provider` | `None`; `PickupDirectory` é aceito somente em Development; valor desconhecido é recusado |
| `Email:From` | `nao-responda@ouroboros.local`; endereço único, sem CR/LF |
| `Email:PickupDirectory` | `.local/notifications-emails`, relativa ao diretório compilado/publicado do worker; prefira pasta absoluta externa e restrita |
| `Broker:Host` / `Broker:Port` | `localhost` / `5672` para processo local |
| `Broker:User` / `Broker:Password` | `notifications_consumer` / externa; não usar operador nem credencial de falhas |
| `PrivateKeyRingPath` | Caminho absoluto de `private.json`, externo e legível somente pelo receptor autorizado |

`Identity:Email` no worker é erro de configuração. `None` não envia nem simula sucesso. A configuração é validada na subida; senha de broker ausente/curta, usuário incorreto ou caminho privado não absoluto impedem habilitar consumo. Arquivo privado ilegível ou conteúdo inválido suspendem a entrega quando necessários, sem expor o conteúdo.

Com RabbitMQ [iniciado e provisionado](#rabbitmq-local), chaves geradas e uma pasta de pickup externa já protegida com ACL/permissões da conta do worker:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$env:Notifications__Consumer__Enabled = 'true'
$env:Notifications__Email__Provider = 'PickupDirectory'
$env:Notifications__Email__PickupDirectory = [IO.Path]::GetFullPath((Read-Host 'Pasta externa restrita para os e-mails'))
$env:Notifications__PrivateKeyRingPath = Join-Path $protectionDirectory 'private.json'
# O gerenciador de segredos pode definir esta variável; a leitura abaixo não grava a senha no histórico.
$env:Notifications__Broker__Password = [Net.NetworkCredential]::new('', (Read-Host 'Senha de notifications_consumer' -AsSecureString)).Password
dotnet run --project src/Services/Notifications/Ouroboros.Notifications.Worker
```

O worker não publica mensagens de teste nem solicitações de Identity. Ele aguarda o contrato protegido na fila `notifications.email.v1`. `.eml` preserva link secreto em claro como artefato de entrega: não copie seu conteúdo a logs/issues, restrinja acesso e remova os arquivos quando o ensaio terminar. O arquivo estável `<messageId sem hífens>.eml` evita sobrescrita local; não constitui a deduplicação persistente futura. Após o processo encerrar, retire a senha do ambiente da sessão, se foi definida para o ensaio: `Remove-Item Env:Notifications__Broker__Password`.

### Imagem Linux de Notifications

Na raiz do repositório, com Docker disponível:

```powershell
docker build -f src/Services/Notifications/Ouroboros.Notifications.Worker/Dockerfile -t ouroboros-notifications-local:dev .
docker run --rm --entrypoint sh ouroboros-notifications-local:dev -c 'id; test -w /app/.local/notifications-emails'
docker run --rm --name ouroboros-notifications-local ouroboros-notifications-local:dev
```

O último comando inicia o host com consumo desabilitado; encerre com Ctrl+C. O build usa SDK `10.0.402` e runtime `aspnet:10.0.13-alpine3.24`; o processo executa como UID 1654, com pickup padrão gravável. O Dockerfile tem ignore próprio para excluir `.env`, PEM, chaves, `.local`, `bin` e `obj`. Mantenha os segredos fora do contexto, mesmo com essas exclusões.

Para habilitar o consumo da imagem em Development, forneça configuração de broker acessível pelo container e monte chaves externas somente de leitura. **`private.json` gerado no host contém caminhos absolutos do host**: se montar em `/keys`, prepare uma cópia operacional do manifesto com `privateKeyPath=/keys/<keyId>.pem`, um caminho absoluto válido dentro do container. O gerador não reescreve isso automaticamente. Conceda leitura dos arquivos e travessia do diretório ao UID 1654, preservando escrita exclusiva da operação; somente Notifications recebe essa montagem. Pickup externo também precisa de permissão de escrita desse UID e acesso restrito. Não há Compose de serviços ou probes nesta entrega, nem publicação automática da imagem.

Teste reproduzível do worker com broker real e processo filho, sem alterar o `.env` ou broker local:

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class '*NotificationsWorkerTests'
```

## E-mails em desenvolvimento

Esta seção descreve o adaptador legado ainda usado pela API de Identity. O worker separado tem configuração e pickup próprios, descritos [acima](#executar-o-worker-de-notifications); não recebe `Identity:Email`. A integração da origem com o broker depende das #85/#86.

Não há servidor de e-mail. No ambiente `Development`, a API de Identity grava cada e-mail enviado como um arquivo `.eml` em `.local/emails/`, na raiz do repositório (pasta ignorada pelo Git); abra o arquivo em qualquer cliente de e-mail. O log registra só o nome do arquivo, nunca o corpo nem tokens. Hoje, enviam e-mail a solicitação de cadastro de cliente, com o link para concluir o cadastro ([Cadastrar um cliente e entrar](#cadastrar-um-cliente-e-entrar)), e a solicitação de redefinição de senha, com o link para redefinir a senha ([Redefinir a senha por e-mail](identity/password-reset.md)); o convite de administradores vai usar o mesmo envio.

A configuração fica na seção `Identity:Email` dos `appsettings.Development.json`. O adaptador local só é aceito em `Development`: em outro ambiente, o host não sobe. Sem provedor configurado (o padrão fora de `Development`), o host sobe, mas qualquer envio falha com erro explícito. Detalhes em [Envio de e-mail por porta com adaptador local](architecture/email.md).

| Chave em Identity:Email | Configuração disponível |
| --- | --- |
| Provider | `PickupDirectory` em Development; `None` por padrão |
| PickupDirectory | `../../../../.local/emails`, resolvido a partir do content root do projeto; obrigatório para PickupDirectory |
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
