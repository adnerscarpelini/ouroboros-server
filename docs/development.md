# Executar o checkout atual

Este guia contém comandos dos projetos disponíveis. A organização Services e os executáveis independentes são acompanhados no [estado de implementação](implementation-status.md). Não execute caminhos futuros como se já existissem.

## Requisitos

- .NET SDK 10.0.402, fixado em `global.json`.
- Visual Studio compatível com .NET 10 e SLNX, se for usar o Visual Studio.
- Docker com Docker Compose, para PostgreSQL, RabbitMQ e o coletor de diagnósticos de desenvolvimento; os testes de integração também exigem o Docker em execução.
- PowerShell 7 (`pwsh`) para provisionar o RabbitMQ e para sua fixture de teste, inclusive em Linux.

## Visual Studio e compilação

Abra `Ouroboros.slnx` por **Arquivo → Abrir → Projeto/Solução**. Abrir somente a pasta do repositório usa a visualização de arquivos. `src`, `src/Services`, `src/Services/Identity` e `src/Gateways` são pastas organizadoras da solution. Identity é a biblioteca `Ouroboros.Identity` e a API `Ouroboros.Identity.Api`, em `src/Services/Identity/`; os gateways finos `Ouroboros.ExternalGateway.Api` e `Ouroboros.AdminGateway.Api` ficam em `src/Gateways/`; `Ouroboros.BuildingBlocks`, `Ouroboros.Hosting`, `Ouroboros.Messaging` e `Ouroboros.Observability` ficam em `src/`. Cada projeto é independente e compilável pelo menu de contexto **Compilar**.

Defina `Ouroboros.Identity.Api` como projeto de inicialização para executar Identity diretamente (um processo, com os listeners público e administrativo), ou inicie também `Ouroboros.ExternalGateway.Api` e `Ouroboros.AdminGateway.Api` quando quiser exercitar o encaminhamento de borda ([Executar os gateways externo e administrativo](#executar-os-gateways-externo-e-administrativo)). Notifications possui o executável separado `Ouroboros.Notifications.Worker`; as bibliotecas não são executáveis independentes.

Na raiz do repositório:

```powershell
dotnet restore Ouroboros.slnx
dotnet build Ouroboros.slnx --no-restore
dotnet build src/Services/Identity/Ouroboros.Identity/Ouroboros.Identity.csproj
dotnet build src/Services/Identity/Ouroboros.Identity.Api/Ouroboros.Identity.Api.csproj
dotnet build src/Gateways/Ouroboros.ExternalGateway.Api/Ouroboros.ExternalGateway.Api.csproj
dotnet build src/Gateways/Ouroboros.AdminGateway.Api/Ouroboros.AdminGateway.Api.csproj
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
- Os testes do pipeline de diagnósticos (`DiagnosticsPipelineTests`) também exigem o Docker e baixam `otel/opentelemetry-collector-contrib:0.162.0` e `busybox:1.37.0`. Sobem coletor e destino reais com os arquivos de `config/observability`, em rede e volume próprios de teste; não usam os serviços nem o volume do Compose ([cenários](architecture/diagnostics-pipeline.md#como-verificar)).
- `IdentityConnectionStringTests` verifica a precedência de `ConnectionStrings:Identity` sobre `ConnectionStrings:Ouroboros`, o uso da segunda quando a primeira não existe e a falha de subida, citando as duas chaves, quando nenhuma está configurada.
- `CredentialIsolationTests`, nos testes de arquitetura e sem Docker, verifica que `POSTGRES_USER` e `POSTGRES_PASSWORD` não aparecem em arquivos de código ou configuração de `src`, que o `.env.example` traz só senhas de exemplo distintas (prefixo `troque-`) e que `*.env` está no `.gitignore`.
- [`HealthAndContainerArchitectureTests`](../tests/Ouroboros.ArchitectureTests/HealthAndContainerArchitectureTests.cs), nos testes de arquitetura e sem Docker, verifica que `/health/live`, `/health/ready` e `/health` são anônimos e nunca expõem segredos, connection strings ou mensagens de exceção, que os Dockerfiles usam `APP_VERSION`, `SOURCE_REVISION`, usuário não-root (`$APP_UID`) e contextos isolados por serviço, que o `compose.yaml` mantém o listener administrativo (`5142`) não publicado no host e migrations no profile explícito `migrate`, que a CI constrói as duas imagens versionadas e que nenhum comando documentado em `docs/`, `scripts/` ou `compose.yaml` remove volumes do Docker.
- [`ServiceReadinessAndContainerTests`](../tests/Ouroboros.IntegrationTests/ServiceReadinessAndContainerTests.cs), nos testes de integração com Docker, verifica que os serviços não aplicam migrations na subida e só ficam prontos após o comando explícito `migrate` com suas roles de migration e convergência de privilégios, que com o RabbitMQ parado Identity permanece pronto (`200`) e realiza login enquanto Notifications permanece vivo (`/health/live` `200`) e reporta não pronto (`/health/ready` `503`), que as imagens construídas executam como UID `1654` com o rótulo OCI `<version>-<commit>` sem assemblies do outro serviço ou `.env`, e que `scripts/messaging/new-compose-keys.ps1` gera diretórios externos isolados para Identity e Notifications sem imprimir segredos.
- [`ServiceUpdateAndRecoveryTests`](../tests/Ouroboros.IntegrationTests/ServiceUpdateAndRecoveryTests.cs), nos testes de integração com Docker ([#89](https://github.com/adnerscarpelini/ouroboros-server/issues/89)), valida a atualização e reversão (rollback) de `Ouroboros.Notifications.Worker` entre versões compatíveis do contrato v1 sem reimplantar ou reiniciar `Identity`, a continuidade do login e recuperação de intenções na parada de `Notifications` e do `RabbitMQ`, a reentrega idempotente/deduplicada após falha do publicador ou do consumidor, o impacto comum e recuperação na parada da instância `PostgreSQL` compartilhada e a preservação de dados em migrations aditivas durante rollback de binário ([procedimento e comandos](#validação-de-atualização-independente-rollback-e-recuperação-dos-serviços)).
- [`ReadinessPoolIsolationTests`](../tests/Ouroboros.IntegrationTests/ReadinessPoolIsolationTests.cs) (#116) invoca diretamente as probes com PostgreSQL/RabbitMQ reais: preserva o mesmo `pg_backend_pid()` ao devolver e reutilizar uma conexão saudável do pool após falhas de endpoint, credencial, acesso ao banco, parada/retomada do broker e cancelamento. Identity continua Healthy sem broker; Notifications passa de Unhealthy a Healthy após a retomada. O QA independente aprovou 9/9 casos no Windows; não foram chamadas HTTP nesta cobertura. Para reproduzir com os pré-requisitos locais:

  ```powershell
  dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class "*ReadinessPoolIsolationTests"
  ```
- [`GatewayArchitectureTests`](../tests/Ouroboros.ArchitectureTests/GatewayArchitectureTests.cs), nos testes de arquitetura e sem Docker ([#83](https://github.com/adnerscarpelini/ouroboros-server/issues/83)), verifica que `Ouroboros.ExternalGateway.Api` e `Ouroboros.AdminGateway.Api` são executáveis fora de `src/Services` sem `DbContext`, `ControllerBase` ou referência a `Identity`, `Notifications` e `BuildingBlocks`, que o gateway administrativo recusa hosts fora de loopback quando `Gateway:AllowNonLoopback=false`, que os dois gateways isolam os prefixos `/api/v1/identity/*` e `/api/v1/admin/identity/*` com `404` na borda e que indisponibilidade ou lentidão do `Identity` produzem ProblemDetails `502 Bad Gateway` e `504 Gateway Timeout` sanitizados dentro do timeout configurado.
- [`GatewaySocketTests`](../tests/Ouroboros.IntegrationTests/GatewaySocketTests.cs) (#117) executa os hosts reais dos dois gateways e um downstream Kestrel controlado em portas livres de loopback, com cliente HTTP/1.1 e invoker padrão. Cobre headers, streaming e falhas antes/depois do corpo começar ([escopo e evidências](architecture/hosts.md#verificação-dos-gateways-por-sockets-reais)). Os 12 casos não usam Identity como downstream; ainda exigem Docker pela fixture PostgreSQL do assembly de integração. As portas são atribuídas pelo sistema, sem depender das portas fixas dos serviços locais:

  ```powershell
  dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class "*GatewaySocketTests"
  ```
- [`GatewayIntegrationTests`](../tests/Ouroboros.IntegrationTests/GatewayIntegrationTests.cs), nos testes de integração com Docker ([#83](https://github.com/adnerscarpelini/ouroboros-server/issues/83)), verifica o encaminhamento das rotas públicas e administrativas de `Identity` pelos dois gateways finos, a preservação da autenticação, audiência e papel (`Admin` vs. `Customer`) no `Identity` tanto via gateway quanto em acesso direto, a prontidão (`/health/ready`) do gateway refletindo o `Identity` e o rate limiting por IP real do cliente (`X-Forwarded-For` aceito apenas das réplicas de gateway listadas em `Identity:ForwardedHeaders:KnownProxies`, bloqueando falsificação de cabeçalho em chamadas via gateway ou diretas).
- [`OriginalDatabaseArchitectureTests`](../tests/Ouroboros.ArchitectureTests/OriginalDatabaseArchitectureTests.cs), nos testes de arquitetura e sem Docker ([#97](https://github.com/adnerscarpelini/ouroboros-server/issues/97)), verifica que `scripts/database/decommission-original-db.sh` existe com finais de linha LF, exige `CONFIRM_ORIGINAL_DB_DEACTIVATION`, `RETENTION_DAYS`, `final-backup-verified` e `session-safety.sql`, aplica bloqueio não destrutivo (`ALLOW_CONNECTIONS false CONNECTION LIMIT 0`) sem nunca executar `DROP DATABASE` sobre o banco original (apenas sobre o banco temporário de verificação `$CHECK_DATABASE`) e que o `compose.yaml` expõe `decommission-original-db` exclusivamente no profile `decommission`.
- [`OriginalDatabaseDecommissioningTests`](../tests/Ouroboros.IntegrationTests/OriginalDatabaseDecommissioningTests.cs), nos testes de integração com Docker ([#97](https://github.com/adnerscarpelini/ouroboros-server/issues/97)), valida em PostgreSQL real o inventário (`inventory`) dos schemas remanescentes (`identity` transferido, `settings` e `public` retidos apenas na origem), o `final-backup` com restore e comparação de digest/SHA-256, a recusa de `deactivate` sem confirmação explícita, sem `RETENTION_DAYS`, com sessão ativa ou com role de serviço tendo `CONNECT`, o bloqueio de conexões sem exclusão, a continuidade do login em `ouroboros_identity`, a reativação em somente leitura (`reactivate`) e a rotação/revogação da credencial antiga ampla (`rotate-admin-credential`) sem afetar `Identity` e `Notifications` e sem vazar senhas nos logs.

Como funcionam e como escrever um teste: [Testes de integração com PostgreSQL](architecture/integration-tests.md).

### Integração contínua

O workflow [ci.yml](../.github/workflows/ci.yml) roda em runner Linux a cada pull request para a `main` e a cada push nela, com a versão do SDK de `global.json`. Não usa banco, `.env`, user-secrets nem segredos; o token do workflow só lê o repositório. Uma nova execução no mesmo PR cancela a anterior.

| Check (nome do job) | O que executa | Equivale localmente a |
| --- | --- | --- |
| Build e testes de arquitetura | Restore e build da solution em Release, testes de arquitetura e construção das imagens Linux versionadas (`<version>-<commit>`) de Identity, Notifications e dos dois gateways (`ExternalGateway` e `AdminGateway`) | `dotnet build Ouroboros.slnx -c Release`, `dotnet test --project tests/Ouroboros.ArchitectureTests -c Release` e `docker build` dos quatro Dockerfiles com `--build-arg APP_VERSION=0.1.0 --build-arg SOURCE_REVISION=<commit>` |
| Testes de integração | Testes de integração com PostgreSQL e RabbitMQ em containers (Testcontainers); confere a disponibilidade de PowerShell para o provisionador | `dotnet test --project tests/Ouroboros.IntegrationTests -c Release` |

Falha de build, de imagem, de teste ou de job reprova o check; publicação em registro e deploy ficam fora da CI. Os resultados em formato TRX ficam como artefato do workflow por 7 dias. Um passo do primeiro job falha se existir um projeto de teste em `tests/` que não esteja listado no workflow, para que nenhuma suíte deixe de rodar sem aviso. Os nomes dos checks são os que o ruleset da `main` deve exigir.

### Fixtures e adaptadores de teste

O checkout usa [PostgresFixture](../tests/Ouroboros.IntegrationTests/PostgresFixture.cs): um container por suíte e um banco modelo com migrations aplicadas. Cada teste recebe banco clonado, isolado dos demais. [IntegrationTest](../tests/Ouroboros.IntegrationTests/IntegrationTest.cs) cria a API de Identity e remove somente seu banco ao terminar. A [RabbitMqFixture](../tests/Ouroboros.IntegrationTests/RabbitMqFixture.cs) cria broker e volume exclusivos de teste, gera credenciais efêmeras e executa o mesmo `provision-rabbitmq.ps1` da operação local; remove somente seus recursos. Os testes verificam contrato, permissões, reinício e reprovisionamento, sem serviço de e-mail conectado.

[IntegrationApiFactory](../tests/Ouroboros.IntegrationTests/IntegrationApiFactory.cs) inicia a API de Identity (`Ouroboros.Identity.Api`) em Testing, com configuração em memória que sobrepõe connection string e chave fictícia. `CreateClient()` fala com o listener público e `CreateAdminClient()`, com o administrativo; `BackofficeIntegrationApiFactory`, declarada no mesmo arquivo, faz todas as requisições do cliente chegarem ao listener administrativo. Como o `TestServer` não tem sockets, o listener é escolhido pelo cabeçalho de teste `X-Test-Listener` (`admin`), lido só por um resolvedor registrado no projeto de testes; a produção ignora o cabeçalho. A separação por portas é verificada em `KestrelListenerTests`, com Kestrel real. Limites elevados evitam interferência nos testes de negócio; RateLimitingTests usa limites próprios; `ForwardedHeadersTests` usa o cabeçalho de teste `X-Test-Client-IP` para definir o IP da conexão.

A `PostgresFixture` aplica as migrations de Identity no banco modelo por meio dessa fábrica; os `DbContext` do serviço são descobertos por reflexão nos assemblies `Ouroboros.*` referenciados pela API ([ModuleDbContexts](../tests/Ouroboros.IntegrationTests/ModuleDbContexts.cs)), de modo que um serviço novo entra sem alterar os testes. O schema legado `settings` é reproduzido pelo SQL versionado [settings-legacy-schema.sql](../tests/Ouroboros.IntegrationTests/Resources/settings-legacy-schema.sql), aplicado por `IntegrationTest.ApplyLegacySettingsSchemaAsync` e pelos testes da migration `OwnIdentityParameters` ([IdentityParameterMigrationTests](../tests/Ouroboros.IntegrationTests/IdentityParameterMigrationTests.cs)), sem depender de um projeto Settings. Nos testes de arquitetura, `OuroborosApiFactory` e `BackofficeApiFactory` ([OuroborosApiFactory.cs](../tests/Ouroboros.ArchitectureTests/OuroborosApiFactory.cs)) sobem a API de Identity com banco fictício, no listener público e no administrativo. Casos que dependem de tempo ou corridas substituem TimeProvider/hasher por adaptadores controlados. [TestOutbox](../tests/Ouroboros.IntegrationTests/TestOutbox.cs) lê intenções e decifra conteúdo somente com chaves efêmeras de teste; NotificationsWorkerTests verifica o adaptador `.eml` no receptor. Isso não comprova entrega distribuída.

## Executar os serviços com Docker Compose

O arquivo [`compose.yaml`](../compose.yaml) e o modelo [`.env.example`](../.env.example) permitem subir localmente a instância PostgreSQL, o RabbitMQ, a API de Identity (`identity-api`), o worker de Notifications (`notifications-worker`) e o coletor de diagnósticos (`otel-collector`), cada um em seu próprio container. Uma única instância PostgreSQL abriga os bancos separados `ouroboros_identity` e `ouroboros_notifications`; nenhum serviço recebe credencial administrativa nem acessa banco ou chaves do outro.

### 1. Gerar chaves externas e configurar o `.env`

Gere os diretórios externos de chaves criptográficas fora de qualquer checkout Git com [`scripts/messaging/new-compose-keys.ps1`](../scripts/messaging/new-compose-keys.ps1), que cria as subpastas `identity` e `notifications` sem imprimir material secreto:

```powershell
$keysRoot = [IO.Path]::GetFullPath((Read-Host 'Diretório externo novo para as chaves do Compose (fora do repositório)'))
pwsh -NoProfile -File scripts/messaging/new-compose-keys.ps1 -Directory $keysRoot
```

O script recusa diretório já existente ou dentro de repositório Git e grava os manifestos já apontando para os caminhos internos montados nos containers (`/run/ouroboros-keys/identity/...` e `/run/ouroboros-keys/notifications/...`):

- `<caminho-fora-do-repo>/identity`: `ring.json` e PEM privado RSA-3072 de assinatura JWT, `public.json` (somente chave pública RSA-3072 de proteção de e-mail) e `supersession.key` (HMAC de 32 bytes de substituição).
- `<caminho-fora-do-repo>/notifications`: `private.json` e PEM privado RSA-3072 de proteção de e-mail, `fingerprint.json` e chave HMAC de 32 bytes de deduplicação.

Se ainda não existir `.env`, copie `.env.example` uma única vez (`cp .env.example .env`), aponte `IDENTITY_KEYS_DIR` e `NOTIFICATIONS_KEYS_DIR` para as duas subpastas criadas (ou defina uma variável base `OUROBOROS_KEYS_ROOT` para compor os dois caminhos no `.env`) e troque todas as senhas `troque-*` por valores aleatórios distintos (pelo menos 32 caracteres nas senhas do broker):

| Variável no `.env` | Finalidade |
| --- | --- |
| `APP_VERSION`, `SOURCE_REVISION`, `IMAGE_TAG` | Versão e revisão imutáveis das imagens (`<APP_VERSION>-<SOURCE_REVISION>`, padrão `0.1.0-local`) |
| `IDENTITY_PUBLIC_PORT` | Porta de loopback do host para o listener público de Identity (padrão `5141`) |
| `NOTIFICATIONS_HEALTH_PORT` | Porta de loopback do host para o endpoint de saúde de Notifications (padrão `5143`) |
| `EXTERNAL_GATEWAY_PORT`, `ADMIN_GATEWAY_PORT` | Portas de loopback do host para os containers opcionais `external-gateway` (padrão `5151`) e `admin-gateway` (padrão `5152`), no profile `gateways` |
| EXTERNAL_GATEWAY_IP, GATEWAY_NETWORK_SUBNET | IP estático exato do proxy externo e subnet da rede Compose (padrões 172.30.230.10 e 172.30.230.0/24). O IP deve pertencer à subnet; ajuste ambos em caso de sobreposição. Identity confia no IP exato e em loopback, sem confiar em toda a rede |
| `IDENTITY_KEYS_DIR` | Caminho absoluto externo de `<caminho-fora-do-repo>/identity`, montado somente em `identity-api` como `:ro` |
| `NOTIFICATIONS_KEYS_DIR` | Caminho absoluto externo de `<caminho-fora-do-repo>/notifications`, montado somente em `notifications-worker` como `:ro` |
| `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` | Credencial administrativa da instância PostgreSQL; usada apenas por `postgres`, `provision-databases`, `transfer-identity` e `decommission-original-db` |
| `IDENTITY_RUNTIME_PASSWORD`, `IDENTITY_MIGRATION_PASSWORD` | Senhas distintas das roles `identity_runtime` e `identity_migrator` no banco `ouroboros_identity` |
| `NOTIFICATIONS_RUNTIME_PASSWORD`, `NOTIFICATIONS_MIGRATION_PASSWORD` | Senhas distintas das roles `notifications_runtime` e `notifications_migrator` no banco `ouroboros_notifications` |
| `RABBITMQ_ADMIN_USER`, `RABBITMQ_ADMIN_PASSWORD` | Credencial administrativa do RabbitMQ; usada apenas por `rabbitmq` e `provision-rabbitmq` |
| `IDENTITY_BROKER_PASSWORD`, `NOTIFICATIONS_BROKER_PASSWORD`, `NOTIFICATIONS_FAILURES_BROKER_PASSWORD` | Senhas distintas de pelo menos 32 caracteres para `identity_publisher`, `notifications_consumer` e `notifications_failures` |

### 2. Subir infraestrutura e provisionar bancos e broker

Suba o PostgreSQL e o RabbitMQ e execute os jobs explícitos e idempotentes do profile `provision`:

```powershell
docker compose up -d --wait postgres rabbitmq
docker compose --profile provision run --rm provision-databases
docker compose --profile provision run --rm provision-rabbitmq
```

### 3. Aplicar migrations explicitamente por serviço

As migrations nunca rodam na subida da API ou do worker. Aplique-as pelos jobs explícitos do profile `migrate`, que usam exclusivamente as roles `identity_migrator` e `notifications_migrator`, e reexecute `provision-databases` em seguida para convergir os privilégios das tabelas criadas para as roles de runtime e negar acesso ao `__EFMigrationsHistory`:

```powershell
docker compose --profile migrate run --rm migrate-identity
docker compose --profile migrate run --rm migrate-notifications
docker compose --profile provision run --rm provision-databases
```

Fora do container do Compose, o mesmo comando explícito `migrate` pode ser executado diretamente no CLI de cada serviço, passando a connection string da role de migration na variável de ambiente correspondente (roles de runtime são recusadas pelo comando):

```powershell
$env:ConnectionStrings__Identity = "Host=localhost;Port=5432;Database=ouroboros_identity;Username=identity_migrator;Password=<IDENTITY_MIGRATION_PASSWORD>"
dotnet run --project src/Services/Identity/Ouroboros.Identity.Api -- migrate
Remove-Item Env:ConnectionStrings__Identity

$env:ConnectionStrings__Notifications = "Host=localhost;Port=5432;Database=ouroboros_notifications;Username=notifications_migrator;Password=<NOTIFICATIONS_MIGRATION_PASSWORD>"
dotnet run --project src/Services/Notifications/Ouroboros.Notifications.Worker -- migrate
Remove-Item Env:ConnectionStrings__Notifications
```

### 4. Subir os serviços e verificar health e readiness

Com bancos provisionados, migrations aplicadas, privilégios convergidos e topologia do RabbitMQ pronta, construa e inicie os serviços:

```powershell
docker compose up -d --build identity-api notifications-worker
# Opcionalmente, suba também os gateways externo (5151) e administrativo (5152) pelo profile `gateways`:
docker compose --profile gateways up -d --build external-gateway admin-gateway
docker compose ps
```

- **Imagens versionadas e usuário não-root:** [`src/Services/Identity/Ouroboros.Identity.Api/Dockerfile`](../src/Services/Identity/Ouroboros.Identity.Api/Dockerfile), [`src/Services/Notifications/Ouroboros.Notifications.Worker/Dockerfile`](../src/Services/Notifications/Ouroboros.Notifications.Worker/Dockerfile), [`src/Gateways/Ouroboros.ExternalGateway.Api/Dockerfile`](../src/Gateways/Ouroboros.ExternalGateway.Api/Dockerfile) e [`src/Gateways/Ouroboros.AdminGateway.Api/Dockerfile`](../src/Gateways/Ouroboros.AdminGateway.Api/Dockerfile) recebem `APP_VERSION` e `SOURCE_REVISION`, gravam `/p:Version`, `/p:SourceRevisionId` e os rótulos OCI (`org.opencontainers.image.version="${APP_VERSION}-${SOURCE_REVISION}"`), copiam somente o próprio executável e bibliotecas técnicas (com `.dockerignore` excluindo `.env`, `*.pem`, `*.key`, `.local` e `backups`) e executam sob `USER $APP_UID` (UID `1654`).
- **Isolamento administrativo:** o listener 5142 de Identity continua em loopback interno, sem publicação. O gateway administrativo compartilha o namespace de rede de identity-api para acessá-lo e escuta em 0.0.0.0:5152 (AllowNonLoopback=true); a publicação dessa porta fica no serviço identity-api, restrita a 127.0.0.1 no host. O gateway externo publica sua própria porta de loopback. Veja [Execução dos gateways](#executar-os-gateways-externo-e-administrativo).
- **Health e readiness sanitizados ([`OuroborosHealthExtensions`](../src/Ouroboros.Hosting/OuroborosHealthExtensions.cs)):**
  - `GET /health/live` (liveness, anônimo e sem rate limit) verifica apenas se o processo está ativo (`Predicate = _ => false`) e responde `200 OK` com `{"status":"Healthy"}`.
  - `GET /health/ready` e `GET /health` (readiness, anônimos e sem rate limit) executam as verificações marcadas com a tag `ready` e respondem `200 OK` com `{"status":"Healthy"}` ou `503 Service Unavailable` com `{"status":"Unhealthy"}` (`Cache-Control: no-store, no-cache`). O writer fixo nunca serializa descrições, mensagens de exceção, nomes de roles, senhas ou connection strings.
  - Em **Identity** ([`IdentityReadinessHealthCheck`](../src/Services/Identity/Ouroboros.Identity/Infrastructure/Persistence/IdentityReadinessHealthCheck.cs)), a prontidão depende exclusivamente do banco próprio (`ouroboros_identity`, validando conexão e as tabelas migradas `identity.parameters`, `identity.email_intents`, `identity.admin_invitations` e `identity.api_clients` por `SELECT 1`; essa verificação não valida genericamente todas as colunas ou versões de schema) e **não** depende do RabbitMQ: se o broker parar (`docker compose stop rabbitmq`), `http://127.0.0.1:5141/health/ready` continua respondendo `200 OK` e o login funciona normalmente, enquanto a outbox retém as intenções de e-mail para publicação posterior.
  - Em **Notifications** ([`NotificationsReadinessHealthCheck`](../src/Services/Notifications/Ouroboros.Notifications/Infrastructure/NotificationsReadinessHealthCheck.cs)), os endpoints são expostos pelo listener técnico configurado em `Notifications:Health:Urls` (padrão `http://127.0.0.1:5143` no `appsettings.json`, publicado no host em `127.0.0.1:${NOTIFICATIONS_HEALTH_PORT:-5143}:5143`). A prontidão exige o banco próprio (`ouroboros_notifications`, tabela `notifications.inbox`), os manifestos de chaves quando `Consumer:Enabled=true` e a conectividade com o RabbitMQ (`notifications_consumer` e declaração passiva da fila `notifications.email.v1`). Com o broker parado, `http://127.0.0.1:5143/health/live` continua `200 OK`, mas `http://127.0.0.1:5143/health/ready` responde `503 Service Unavailable` com `{"status":"Unhealthy"}` até o broker voltar.

Consulte os endpoints de saúde a partir do host:

```powershell
Invoke-RestMethod -Uri http://127.0.0.1:5141/health/live
Invoke-RestMethod -Uri http://127.0.0.1:5141/health/ready
Invoke-RestMethod -Uri http://127.0.0.1:5143/health/live
Invoke-RestMethod -Uri http://127.0.0.1:5143/health/ready
```

### 5. Parada segura do ambiente

Para interromper os containers sem perder estado, use `docker compose stop` ou `docker compose down` sem opções de exclusão de volumes:

```powershell
docker compose stop
# ou, para encerrar e remover apenas os containers:
docker compose down
```

Preserve sempre os volumes `postgres-data`, `rabbitmq-data`, `diagnostics-data` e `collector-queue`, bem como o diretório externo de chaves e a pasta `backups/`. Nenhum comando documentado ou script do repositório remove volumes do Docker.

## Validação de atualização independente, rollback e recuperação dos serviços

A entrega [#89](https://github.com/adnerscarpelini/ouroboros-server/issues/89) valida a implantação independente, o rollback de versão compatível e a recuperação de falhas com acesso direto às APIs e serviços reais, sem exigir gateways ([#83](https://github.com/adnerscarpelini/ouroboros-server/issues/83)) e sem excluir volumes.

### Verificação automatizada reproduzível

Na raiz do repositório, com Docker e `pwsh` disponíveis, execute a suíte dedicada ([`ServiceUpdateAndRecoveryTests`](../tests/Ouroboros.IntegrationTests/ServiceUpdateAndRecoveryTests.cs)), que sobe containers isolados de PostgreSQL (`ProvisionedInstance`) e RabbitMQ (`RabbitMqFixture`) sem depender nem alterar o `.env` ou os volumes do Compose local:

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class "*ServiceUpdateAndRecoveryTests"
```

### Cenários validados e reprodução com Docker Compose

Com o ambiente do [Docker Compose](#executar-os-serviços-com-docker-compose) em execução, todos os cenários abaixo podem ser reproduzidos preservando integralmente os volumes (`postgres-data`, `rabbitmq-data`, `diagnostics-data` e `collector-queue`):

#### 1. Atualização e reversão (rollback) de Notifications sem implantar Identity

- **Garantia validada:** `Ouroboros.Notifications.Worker` é atualizado entre versões compatíveis do contrato v1 (por exemplo, `1.0.0-rev100` → `1.1.0-rev110`) e revertido (`1.1.0-rev110` → `1.0.0-rev100`) com `identity-api` em execução contínua, sem reimplantar nem reiniciar `Identity`. Envelopes v1 padrão e envelopes contendo campos JSON opcionais aditivos são aceitos nas duas versões (`EnvelopeCodec` preserva os campos obrigatórios e ignora extensões opcionais desconhecidas), `serviceVersion` reflete a versão em execução nos logs JSON estruturados e mensagens já entregues antes do rollback permanecem deduplicadas em `notifications.inbox` (`State = Delivered`, `Attempts = 1`).
- **Comandos equivalentes no Compose:**

  ```powershell
  # Construir duas imagens compatíveis do worker (ex.: 1.0.0-rev100 e 1.1.0-rev110)
  docker build -f src/Services/Notifications/Ouroboros.Notifications.Worker/Dockerfile --build-arg APP_VERSION=1.0.0 --build-arg SOURCE_REVISION=rev100 -t ouroboros-notifications-worker:1.0.0-rev100 .
  docker build -f src/Services/Notifications/Ouroboros.Notifications.Worker/Dockerfile --build-arg APP_VERSION=1.1.0 --build-arg SOURCE_REVISION=rev110 -t ouroboros-notifications-worker:1.1.0-rev110 .

  # Atualizar somente notifications-worker para 1.1.0-rev110 sem reiniciar identity-api
  $env:IMAGE_TAG = '1.1.0-rev110'
  docker compose up -d --no-deps notifications-worker
  Invoke-RestMethod -Uri http://127.0.0.1:5143/health/ready

  # Reverter (rollback) somente notifications-worker para 1.0.0-rev100 sem reiniciar identity-api
  $env:IMAGE_TAG = '1.0.0-rev100'
  docker compose up -d --no-deps notifications-worker
  Invoke-RestMethod -Uri http://127.0.0.1:5143/health/ready
  Remove-Item Env:IMAGE_TAG
  ```

#### 2. Parada e reinício de Notifications

- **Garantia validada:** Quando `notifications-worker` para, `identity-api` permanece pronto (`GET /health/ready` retorna `200 OK` com `{"status":"Healthy"}`), o login funciona normalmente e novas solicitações de cadastro/redefinição gravam intenções de e-mail que aguardam recuperação. Quando `notifications-worker` volta a iniciar e reporta prontidão (`200 OK` em `http://127.0.0.1:5143/health/ready`), as mensagens pendentes são consumidas e entregues (`State = Delivered`).
- **Comandos equivalentes no Compose:**

  ```powershell
  docker compose stop notifications-worker
  Invoke-RestMethod -Uri http://127.0.0.1:5141/health/ready
  Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5141/api/v1/identity/registrations -ContentType 'application/json' -Body '{"email":"recuperacao-worker@example.com"}'

  docker compose start notifications-worker
  Invoke-RestMethod -Uri http://127.0.0.1:5143/health/ready
  ```

#### 3. Parada e reinício do RabbitMQ

- **Garantia validada:** Com o container `rabbitmq` parado:
  - `identity-api` permanece pronto (`GET /health/ready` `200 OK`) e o login funciona normalmente.
  - Novas solicitações de e-mail permanecem retidas na outbox (`identity.email_intents`) com `State = Pending`, `ProtectedEnvelope` preservado e `NextAttemptAt` agendado para retentativa.
  - Mensagens persistentes (`Persistent = true`) publicadas na fila durável `notifications.email.v1` antes da parada sobrevivem no volume `rabbitmq-data` do broker.
  - `notifications-worker` permanece vivo (`GET /health/live` `200 OK`), mas reporta não pronto (`GET /health/ready` `503 Service Unavailable` com `{"status":"Unhealthy"}`).
  - Após `docker compose start rabbitmq`, `notifications-worker` volta a `200 OK` em `/health/ready`, a mensagem que já estava enfileirada no broker e a intenção pendente na outbox são publicadas (`State = Published`, `ProtectedEnvelope = null`) e entregues (`State = Delivered`).
- **Comandos equivalentes no Compose:**

  ```powershell
  docker compose stop rabbitmq
  Invoke-RestMethod -Uri http://127.0.0.1:5141/health/ready
  # /health/live do worker segue 200 OK; /health/ready responde 503 Unhealthy enquanto o broker está parado
  Invoke-RestMethod -Uri http://127.0.0.1:5143/health/live

  docker compose start rabbitmq
  Invoke-RestMethod -Uri http://127.0.0.1:5143/health/ready
  ```

#### 4. Parada e reinício da instância PostgreSQL compartilhada

- **Impacto comum e recuperação:** Como `ouroboros_identity` e `ouroboros_notifications` compartilham a mesma instância PostgreSQL no ambiente local ([decisão educacional](architecture/microservices.md#um-banco-por-móduloserviço-uma-instância-local)), parar o container `postgres` afeta ambos os serviços simultaneamente:
  - Tanto `identity-api` (`:5141`) quanto `notifications-worker` (`:5143`) continuam respondendo `200 OK` em `GET /health/live`, mas passam a responder `503 Service Unavailable` com corpo fixo `{"status":"Unhealthy"}` em `GET /health/ready` e `GET /health`.
  - Tentativas de login durante a indisponibilidade falham sem expor senhas, nomes de roles, hosts ou connection strings no corpo da resposta.
  - Os health checks de prontidão ([`IdentityReadinessHealthCheck`](../src/Services/Identity/Ouroboros.Identity/Infrastructure/Persistence/IdentityReadinessHealthCheck.cs) e [`NotificationsReadinessHealthCheck`](../src/Services/Notifications/Ouroboros.Notifications/Infrastructure/NotificationsReadinessHealthCheck.cs)) abrem conexões com `Pooling = false` e timeouts curtos (3 s) e, desde a [#116](https://github.com/adnerscarpelini/ouroboros-server/issues/116), retornam estado de saúde sem executar `NpgsqlConnection.ClearAllPools()` ao capturar falha. A probe observa a dependência; não invalida pools globais, não redefine conexões da aplicação nem realiza failover. Limpezas explícitas necessárias em fixtures e procedimentos de manutenção têm outro escopo e permanecem separadas.
  - Assim que `postgres` retorna (`docker compose start postgres`), ambos os serviços recuperam automaticamente a prontidão (`200 OK` em `/health/ready`) e o login volta a funcionar sem necessidade de reiniciar os containers de aplicação.
- **Comandos equivalentes no Compose:**

  ```powershell
  docker compose stop postgres
  Invoke-RestMethod -Uri http://127.0.0.1:5141/health/live
  Invoke-RestMethod -Uri http://127.0.0.1:5143/health/live

  docker compose start postgres
  Invoke-RestMethod -Uri http://127.0.0.1:5141/health/ready
  Invoke-RestMethod -Uri http://127.0.0.1:5143/health/ready
  ```

#### 5. Reentrega após falha do publicador ou do consumidor

- **Falha do publicador após publicar no broker e antes de `ConfirmAsync`:** Se o publicador de Identity publicar a mensagem com confirmação do broker (`BasicPublishAsync` em modo confirm/mandatory) e parar antes de gravar `ConfirmAsync` no banco local, a intenção permanece `Pending` até a expiração da lease (`lease_until`) e é republicada no ciclo seguinte com o **mesmo `MessageId` (`ExternalId`)**. `Notifications` recebe as duas entregas na fila, mas `InboxAcceptance` deduplica a segunda entrega pela chave primária/tombstone em `notifications.inbox`, executando o transporte de e-mail uma única vez (`State = Delivered`, `Attempts = 1`).
- **Falha do consumidor após enviar ao provedor e antes de `DeliverAsync`:** Se o executor de Notifications reclamar a mensagem (`ClaimAsync`), registrar a tentativa (`BeginAttemptAsync`, gravando `State = Sending` e `Attempts = 1`) e concluir a chamada ao transporte externo (`IEmailTransport.SendAsync`), mas parar antes do commit local em `DeliverAsync`, a linha permanece em `State = Sending` com o `ProtectedEnvelope` retido. Quando a lease (`lease_until`) e `next_attempt_at` vencem, o `InboxExecutor` retoma a mesma mensagem preservando o **`MessageId`** original e o cabeçalho RFC 5322 **`Message-ID: <{messageId:N}@ouroboros.local>`**, concluindo a confirmação local na segunda tentativa (`State = Delivered`, `Attempts = 2`, `ProtectedEnvelope = null`).
- **Tratamento e janela explícita de duplicação no transporte:** No adaptador local `PickupEmailTransport`, a escrita em disco é idempotente por nome de arquivo (`{messageId:N}.eml` gravado via arquivo temporário `.tmp` e `File.Move(..., overwrite: true)`), resultando em um único arquivo final com o mesmo `Message-ID`. Conforme a [política de entrega confiável](architecture/reliable-delivery.md), como o transporte externo não participa da transação do banco, um provedor SMTP/HTTP real que não deduplique por `Message-ID` pode enviar o e-mail duas vezes nessa janela entre o retorno de `SendAsync` e o commit de `DeliverAsync` (entrega *at-least-once*, sem promessa de *exactly-once* no provedor externo).

#### 6. Limites de rollback com migrations e novas escritas

- **Migrations aditivas e preservação de dados:** Quando uma versão nova aplica uma migration aditiva (por exemplo, nova coluna opcional ou tabela nova adicionada pela role `*_migrator`) e grava novos dados, reverter o container do serviço para o binário da versão anterior preserva integralmente os dados gravados pela versão nova, desde que **não** seja executado downgrade destrutivo de schema (`DROP COLUMN` / `DROP TABLE`) nem remoção de volumes. O binário anterior continua lendo e gravando as colunas conhecidas pelo seu modelo EF Core e mantendo `/health/ready` em `200 OK` (`{"status":"Healthy"}`), sem expor segredos.
- **Limites explícitos de rollback:**
  - Não execute downgrade destrutivo de banco como parte de rollback de container/binário: alterações incompatíveis exigem padrão *expand-and-contract* em entregas separadas.
  - Para a transferência inicial do banco de Identity ([#82](https://github.com/adnerscarpelini/ouroboros-server/issues/82)), permanecem os limites documentados em [Voltar ao banco original](#voltar-ao-banco-original) (`rollback` e `reconcile` preservam novas escritas em CSV e recusam históricos de migrations divergentes ou linhas copiadas excluídas).

## RabbitMQ local

O broker é infraestrutura separada da API e do PostgreSQL. O Compose usa `rabbitmq:4.4.0-management`, hostname estável, vhost `ouroboros` e volume `rabbitmq-data`. Identity tem publicador da outbox habilitável explicitamente. O worker da #87 tem aceitação/deduplicação persistentes; seu provedor local continua restrito a Development. [Topologia, contratos e limites](architecture/messaging.md).

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

3. Provisione explicitamente o vhost, usuários, exchanges, filas e bindings (pelo profile `provision` do Compose ou pelo script no host):

   ```powershell
   docker compose --profile provision run --rm provision-rabbitmq
   # ou diretamente no host:
   pwsh -NoProfile -File scripts/messaging/provision-rabbitmq.ps1 -EnvFile .env -LocalDevelopment
   ```

   Com porta de gestão alterada, acrescente `-ManagementUrl http://127.0.0.1:<porta>`. Sem `-EnvFile`, as cinco variáveis de usuário/senhas devem vir do ambiente, carregadas pelo gerenciador de segredos. Variáveis já definidas no processo têm precedência sobre o arquivo; confira sua origem ao trocar credenciais. O leitor de arquivo usa somente a allowlist da tabela, ignora comentários/outros nomes, remove um par de aspas externas e não expande variáveis nem executa o conteúdo. Não use dot-source, `Invoke-Expression`, `-Verbose`, `-Debug` ou transcript para carregar/exibir os segredos.

   A saída de sucesso é `Topologia RabbitMQ e permissões convergidas; nenhum dado removido.` Uma falha interrompe o script com indicação sanitizada da operação; não repita a senha em logs para investigar. Reexecute o mesmo comando após corrigir a causa: ele não apaga mensagens. Uma incompatibilidade de argumentos de uma fila existente exige análise do operador, sem apagá-la para fazer a declaração passar.

4. Para testar reinício local sem descartar persistência, use `docker compose restart rabbitmq` e reexecute o provisionador. Para parar somente o broker, use `docker compose stop rabbitmq`. `docker compose down` mantém os volumes, mas para todos os containers do projeto. Preserve `rabbitmq-data`, `postgres-data` e backups; os comandos documentados não removem volumes.

A fila principal tem 64 MiB e rejeita novas publicações quando cheia, sem TTL/DLX do corpo original. A fila de falhas tem 8 MiB e TTL máximo de 30 dias, somente para referências sanitizadas. O consumidor da #87 persiste aceitação/expiração/classificação antes do ACK; o executor chama o provedor separadamente com tentativas limitadas. O pickup disponível continua restrito a Development. A recuperação da publicação de origem é a #86; [recuperação do consumidor](#recuperar-processamento-e-referências-de-falha) usa scripts próprios.

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

   No **banco próprio**, as migrations só rodam por comando explícito com a role de migration (`identity_migrator`); com a role de runtime o comando é recusado. Você pode aplicá-las pelo job `migrate-identity` do Compose, pelo comando CLI `migrate` da API ou por `dotnet ef database update` com `--connection`:

   ```powershell
   docker compose --profile migrate run --rm migrate-identity
   # ou via CLI da API:
   $env:ConnectionStrings__Identity = "Host=localhost;Port=5432;Database=ouroboros_identity;Username=identity_migrator;Password=<IDENTITY_MIGRATION_PASSWORD>"
   dotnet run --project src/Services/Identity/Ouroboros.Identity.Api -- migrate
   Remove-Item Env:ConnectionStrings__Identity
   # ou via dotnet-ef:
   dotnet ef database update --connection "Host=localhost;Port=5432;Database=ouroboros_identity;Username=identity_migrator;Password=<IDENTITY_MIGRATION_PASSWORD>" --project src/Services/Identity/Ouroboros.Identity --startup-project src/Services/Identity/Ouroboros.Identity.Api --context IdentityDbContext
   ```

   Uma senha com `;` ou aspas exige aspas no valor da connection string (formato do Npgsql), por exemplo `Password="a;b"`. Depois de migrar o banco próprio, reexecute o [provisionamento](#provisionar-bancos-e-roles-por-serviço) para que o histórico de migrations seja negado ao runtime.

   `OwnIdentityParameters` cria sementes com `uuidv7()` em instalação limpa, exigindo PostgreSQL 18 (a versão do `compose.yaml`). No upgrade, copia e valida os parâmetros e o histórico do schema `settings` na mesma transação. A migration não faz backup automaticamente nem exclui a origem; não ative a versão nova se ela falhar.

   No primeiro `database update` em um banco vazio, o log mostra `Failed executing DbCommand` sobre `__EFMigrationsHistory` antes de criar a tabela. É um comportamento conhecido do EF Core: o comando termina com `Done.`. Repetir o comando não altera nada.

5. Opcionalmente, confira os schemas: `identity` contém `users`, `sessions`, `refresh_tokens`, `email_tokens`, `parameters` e `parameter_history`. Em banco migrado, `settings` continua retido, sem novas escritas e sem projeto que o acesse ([inventário, backup final e desativação controlada](#desativar-o-banco-original-e-rotacionar-a-credencial-antiga)); não é necessário criá-lo em instalação limpa. Ajuste usuário e banco se mudou o `.env`:

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

Move todo o banco de Identity (schema `identity`: `users`, `sessions`, `refresh_tokens`, `email_tokens`, `email_intents` quando já migrada, `parameters`, `parameter_history` e `__EFMigrationsHistory`) do banco original da instância (`POSTGRES_DB`) para `ouroboros_identity`, em um único procedimento, sem estado intermediário com dois bancos. Está em `scripts/database/transfer-identity.sh` (com `identity-digest.sql`) e roda só por comando explícito: nem `docker compose up` nem as APIs o executam. A decisão, a validação e os limites do rollback estão em [Persistência](architecture/persistence.md#transferência-do-banco-de-identity).

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

**Validação.** O resumo comparado entre origem e destino tem 20 facetas: contagens e linhas inteiras de `users`, `sessions`, `refresh_tokens`, `email_tokens`, `parameters` e `parameter_history`; identificadores públicos de usuários e sessões; hashes de senha; `token_hash` dos refresh tokens e dos tokens de e-mail; revisões dos parâmetros e do histórico; histórico de migrations; e a estrutura (colunas com tipos e defaults, índices, constraints, gatilhos, comentários e sequências). Quando `email_intents` existe na origem, seu conteúdo integral também participa do resumo e da linha de base. Qualquer diferença reprova a validação.

**Falha e repetição.** A falha de validação impede a troca e a origem fica intacta. Nada é excluído: nem a origem, nem volumes, nem a credencial antiga; o único banco removido é o de verificação criado pelo próprio `backup`. O `copy` recusa destino que já tem o schema `identity`, origem alterada depois do backup e senhas de runtime e de migration iguais; se falhar no meio, remove o schema parcial que ele mesmo criou no destino vazio. Para repetir depois de uma validação que falhou, avalie o destino e, conscientemente, esvazie-o antes de rodar o `copy` de novo (o comando aparece na recusa; ajuste usuário e banco se mudou o `.env`):

```powershell
docker compose exec postgres psql -U ouroboros -d ouroboros_identity -c "DROP SCHEMA identity CASCADE"
```

### Trocar a connection string de Identity

A troca é ação do operador e só é liberada pelo `cutover`. Com as APIs de Identity paradas, defina a connection string da role de runtime, que tem precedência sobre `ConnectionStrings:Ouroboros`, e inicie-as:

```powershell
dotnet user-secrets set "ConnectionStrings:Identity" "Host=localhost;Port=5432;Database=ouroboros_identity;Username=identity_runtime;Password=<IDENTITY_RUNTIME_PASSWORD>" --project src/Services/Identity/Ouroboros.Identity.Api
```

Fora de desenvolvimento, use a variável `ConnectionStrings__Identity`. Senha com `;` ou aspas exige aspas no valor da connection string. Depois da troca, sessões, refresh tokens, senhas, tokens de e-mail pendentes e parâmetros com histórico continuam válidos. Migrations do banco próprio: comando explícito com a role de migration, descrito no passo 4 de [Banco de dados local](#banco-de-dados-local). O desligamento do banco original e a revogação/rotação da credencial antiga seguem procedimento separado ([#97](https://github.com/adnerscarpelini/ouroboros-server/issues/97)): [Desativar o banco original e rotacionar a credencial antiga](#desativar-o-banco-original-e-rotacionar-a-credencial-antiga).

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

Limites: linhas excluídas no destino não são reconciliadas (o `rollback` aborta se linhas copiadas deixaram de existir); há um único ciclo de `rollback` e `reconcile`; se os históricos de migrations divergem, `rollback` e `reconcile` recusam e a reconciliação é manual. Aplicar #85 só no destino impede voltar diretamente à origem pré-#85; preserve a outbox e reconcilie schema/escritas antes de mudar a connection string.

O `rollback` exige que não haja sessões ativas no destino, e o `reconcile`, na origem (`FORCE_TERMINATE=1` as encerra, como no `freeze`). Os testes do procedimento estão descritos em [Testes](#testes).

## Desativar o banco original e rotacionar a credencial antiga

Após a validação do `cutover` de Identity ([#82](https://github.com/adnerscarpelini/ouroboros-server/issues/82)), a retirada dos hosts agregadores e do projeto `Ouroboros.Settings` ([#96](https://github.com/adnerscarpelini/ouroboros-server/issues/96)) e o encerramento da janela operacional de rollback, o operador inventaria o banco original (`POSTGRES_DB`, padrão `ouroboros`), verifica sessões e privilégios na origem, com confirmação manual das configurações de runtime, gera um backup final completo e verificável, rotaciona/revoga a credencial ampla antiga e desativa conexões ao banco original sem excluir dados, schemas, bancos ou volumes ([#97](https://github.com/adnerscarpelini/ouroboros-server/issues/97)). O procedimento versionado é [`scripts/database/decommission-original-db.sh`](../scripts/database/decommission-original-db.sh), exposto no [`compose.yaml`](../compose.yaml) pelo serviço `decommission-original-db` sob o profile explícito `decommission`. Decisão, inventário e critérios de retenção em [Destino e desativação do banco original (#97)](architecture/persistence.md#destino-e-desativação-do-banco-original-97).

**Comando geral** (executado na raiz do repositório; `<id>` é o identificador impresso por `final-backup`):

```powershell
docker compose --profile decommission run --rm decommission-original-db <comando> [id]
```

| Comando | O que faz |
| --- | --- |
| `inventory` | Lista os schemas remanescentes no banco original (`identity`, `settings`, `public`), a contagem de linhas de cada tabela e os históricos `__EFMigrationsHistory`, classificando o que foi transferido para `ouroboros_identity` (`transferred_to_target`, `migration_history_transferred`) e o que permanece retido apenas na origem (`retained_only_in_source`, `migration_history_retired_in_source`). |
| `verify-unused` | Verifica que há zero sessões ativas no banco original (`pg_stat_activity`), que `PUBLIC` não possui `CONNECT` no banco original, que nenhuma role de serviço (`identity_runtime`, `identity_migrator`, `notifications_runtime`, `notifications_migrator`) possui `CONNECT` nele e que `ouroboros_identity` já contém o schema `identity`. |
| `final-backup` | Exige `verify-unused`, congela o banco original em modo somente leitura (`default_transaction_read_only = on`), gera `./backups/original-<id>/final.dump` (formato custom com todos os schemas remanescentes), `final.dump.sha256` e `inventory.txt`, restaura em banco temporário `original_decommission_check_<id>`, compara os digests de schemas, colunas (nome/tipo/nulabilidade), sequências e linhas de tabelas comuns, remove apenas o banco temporário e grava `final-backup-verified`. Imprime `Pacote: <id>`. |
| `rotate-admin-credential` | Rotaciona a senha da credencial administrativa ampla (`ADMIN_ROLE`, padrão `POSTGRES_USER`) para `NEW_ADMIN_PASSWORD` com `session-safety.sql` (sem registrar nos logs) e, se `LEGACY_ROLE` for informada, encerra suas sessões, revoga `CONNECT`/`TEMPORARY` em todos os bancos e aplica `NOLOGIN PASSWORD NULL`, validando em seguida a conectividade de `identity_runtime` e `notifications_runtime`. |
| `deactivate <id>` | Desativa novas conexões ao banco original (`ALLOW_CONNECTIONS false CONNECTION LIMIT 0` e `default_transaction_read_only = on`) sem apagar o banco, schemas ou volumes; exige `CONFIRM_ORIGINAL_DB_DEACTIVATION=DEACTIVATE:<banco>`, `RETENTION_DAYS` (`>= 1`), backup final verificado (`<id>`), `verify-unused` e digest inalterado desde o backup final, gravando `deactivated.manifest`. |
| `reactivate <id>` | Reabilita conexões em modo somente leitura (`ALLOW_CONNECTIONS true CONNECTION LIMIT -1` e `default_transaction_read_only = on`) e grava `reactivated` no pacote, permitindo inspeção ou recuperação autorizada durante a janela de retenção. |
| `help` | Mostra a ajuda do script. |

### Passo a passo operacional

**Retenção aprovada:** conservar o banco original e o backup final por 30 dias a partir do cutover validado. O operador deve registrar o instante do cutover e conferir o prazo antes de qualquer decisão posterior. RETENTION_DAYS=30 registra a política no manifesto; o script não acompanha o relógio nem exclui dados ao fim do prazo. As operações continuam manuais e exigem aprovação explícita; não foram executadas no ambiente existente nesta entrega.

1. **Inventariar o banco original e confirmar ausência de uso por runtime:**

   A inspeção de configurações é manual: verify-unused não lê variáveis nem user-secrets dos processos. Certifique-se de que `ConnectionStrings:Identity` (ou `ConnectionStrings__Identity`) está ativa em todos os processos de `Identity` e que `ConnectionStrings:Ouroboros` foi removida dos user-secrets e variáveis de ambiente das aplicações:

   ```powershell
   dotnet user-secrets remove "ConnectionStrings:Ouroboros" --project src/Services/Identity/Ouroboros.Identity.Api
   docker compose --profile decommission run --rm decommission-original-db inventory
   docker compose --profile decommission run --rm decommission-original-db verify-unused
   ```

2. **Gerar e validar o backup final completo (`final-backup`):**

   ```powershell
   docker compose --profile decommission run --rm decommission-original-db final-backup
   ```

   O pacote inclui source-database, que o vincula à origem, e um ID com timestamp e sufixo aleatório. Anote o <id> impresso em `Pacote: <id>`. Os artefatos (`final.dump`, `final.dump.sha256`, `inventory.txt`, `source-inventory.digest`, `restore-check.digest` e `final-backup-verified`) ficam em `./backups/original-<id>/` com permissões restritas (`0700`/`0600`). Proteja esse diretório fora do Git pela janela de retenção aprovada.

3. **Revogar/rotacionar a credencial antiga ampla (`rotate-admin-credential`):**

   Ao encerrar a janela de rollback, passe a nova senha administrativa via variável de ambiente da sessão (sem gravá-la em argumento ou histórico) e, se existir uma role legada ampla dedicada (`LEGACY_ROLE`), informe-a para revogação:

   ```powershell
   $env:NEW_ADMIN_PASSWORD = [Net.NetworkCredential]::new('', (Read-Host 'Nova senha para POSTGRES_USER' -AsSecureString)).Password
   # Opcional: se havia uma role ampla legada distinta de POSTGRES_USER:
   # $env:LEGACY_ROLE = 'nome_da_role_legada'
   docker compose --profile decommission run --rm -e NEW_ADMIN_PASSWORD -e LEGACY_ROLE decommission-original-db rotate-admin-credential
   Remove-Item Env:NEW_ADMIN_PASSWORD
   Remove-Item Env:LEGACY_ROLE -ErrorAction SilentlyContinue
   ```

   Atualize `POSTGRES_PASSWORD` no `.env` (ou no cofre de segredos do ambiente) com a nova senha administrativa para os próximos comandos de manutenção. `identity-api` e `notifications-worker` continuam operando normalmente com `identity_runtime` e `notifications_runtime`, sem reinício.

4. **Desativar o banco original sem exclusão (`deactivate <id>`):**

   Com a decisão explícita do dono do projeto sobre a janela de retenção (`RETENTION_DAYS`, 30 dias a partir do cutover validado, conforme decisão aprovada) e a confirmação nominal `CONFIRM_ORIGINAL_DB_DEACTIVATION=DEACTIVATE:<banco>` (por exemplo, `DEACTIVATE:ouroboros`):

   ```powershell
   docker compose --profile decommission run --rm `
     -e RETENTION_DAYS=30 `
     -e CONFIRM_ORIGINAL_DB_DEACTIVATION=DEACTIVATE:ouroboros `
     decommission-original-db deactivate <id>
   ```

   O banco original permanece preservado no volume `postgres-data`, mas recusa novas conexões (`ALLOW_CONNECTIONS false`, SQLSTATE `55000`). O healthcheck do container `postgres` consulta `-d postgres` e permanece `healthy`.

5. **Reativar em modo somente leitura para inspeção ou recuperação (`reactivate <id>`):**

   Se for necessário inspecionar os dados retidos (`settings` ou `public`) ou executar um rollback tardio autorizado dentro da janela de retenção:

   ```powershell
   docker compose --profile decommission run --rm decommission-original-db reactivate <id>
   ```

   Se o retorno ao banco original ocorrer após novas escritas em `ouroboros_identity`, reative primeiro o banco original com `reactivate <id>` e, em seguida, execute `transfer-identity rollback <transfer_id>`, `transfer-identity reconcile <transfer_id>` e `transfer-identity unfreeze` conforme [Voltar ao banco original](#voltar-ao-banco-original) antes de voltar a connection string.

### Verificar os testes da desativação do banco original

Na raiz do repositório:

```powershell
# Testes de arquitetura (sem Docker): script LF, aprovação explícita, final-backup-verified, bloqueio sem DROP DATABASE da origem e sem remoção de volumes
dotnet test --project tests/Ouroboros.ArchitectureTests -- --filter-class "*OriginalDatabaseArchitectureTests"

# Testes de integração (exige Docker): inventário, final-backup restaurado, recusas de deactivate, bloqueio, reactivate e rotação/revogação de credencial sem vazar senha nos logs
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class "*OriginalDatabaseDecommissioningTests"
```

## Criar o primeiro administrador

Os comandos operacionais da API de Identity escrevem em UTF-8, inclusive com saída redirecionada no Windows.

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
- Código de saída `0`: criado; `1`: recusa de regra, erro técnico ou cancelamento solicitado; `2`: falta `--email` ou a variável da senha. Recusa informa a regra e `Nada foi criado.`; erro técnico tem saída genérica e diagnóstico sanitizado `command-failed` com trace W3C; cancelamento informa `Comando cancelado.`. O processo drena as filas ao descartar o host antes de sair.

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

Com banco, assinatura JWT e [proteção da outbox](#configurar-a-outbox-de-identity) configurados, na raiz do repositório:

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

Todo endpoint exige um access token válido da audiência do listener (`Authorization: Bearer <token>`), salvo os marcados com `[AllowAnonymous]`; os endpoints administrativos exigem um token de `Admin`. Os endpoints técnicos de liveness (`GET /health/live`) e readiness (`GET /health/ready` e `GET /health`) são anônimos, sem rate limiting, nos dois listeners, respondem apenas `{"status":"Healthy"}` (`200`) ou `{"status":"Unhealthy"}` (`503`) sem expor segredos ou mensagens de exceção, e a prontidão de Identity depende exclusivamente do próprio banco, nunca do RabbitMQ ([Executar os serviços com Docker Compose](#4-subir-os-serviços-e-verificar-health-e-readiness)). O JWKS `GET /api/v1/identity/keys` é anônimo no listener público, com 60 requisições/minuto por IP e cache de cinco minutos. Os demais endpoints anônimos são os de login, `POST /api/v1/identity/sessions` no listener público e `POST /api/v1/admin/identity/sessions` no administrativo ([Fazer login com e-mail e senha](identity/login.md)), que devolvem o access token, e os de renovação, `POST /api/v1/identity/sessions/refresh` e `POST /api/v1/admin/identity/sessions/refresh` ([Renovar o access token com rotação do refresh token](identity/refresh-session.md)), que trocam o refresh token por um par novo, e os de logout, `POST /api/v1/identity/sessions/logout` e `POST /api/v1/admin/identity/sessions/logout` ([Encerrar a sessão atual](identity/logout.md)), que encerram a sessão do refresh token. Os de logout-all, `POST /api/v1/identity/sessions/logout-all` e `POST /api/v1/admin/identity/sessions/logout-all` ([Encerrar todas as sessões](identity/logout-all.md)), exigem access token e encerram todas as sessões do usuário do token. Os de consulta do próprio usuário, `GET /api/v1/identity/me` e `GET /api/v1/admin/identity/me` ([Consultar o próprio usuário](identity/me.md)), também exigem access token. Só no listener público, a solicitação e a conclusão do cadastro de cliente, `POST /api/v1/identity/registrations` e `POST /api/v1/identity/registrations/complete` ([Cadastrar cliente por e-mail](identity/registration.md)), e a solicitação e a conclusão da redefinição de senha, `POST /api/v1/identity/password-resets` e `POST /api/v1/identity/password-resets/complete` ([Redefinir a senha por e-mail](identity/password-reset.md)), também são anônimas. Em cada público, o login aceita 10 requisições por minuto de cada IP, a renovação e o logout, 30 cada, a solicitação de cadastro, 5, a conclusão do cadastro, 10, a solicitação de redefinição de senha, 5, e a conclusão da redefinição, 10; acima disso, respondem `429` com `Retry-After`. Os limites ficam em `Identity:RateLimiting:Policies` no `appsettings.json`, com contadores separados por público, e valores ausentes ou não positivos impedem a subida ([Limitar requisições de autenticação por IP](identity/auth-rate-limiting.md)). Fora deles, uma requisição sem token responde 401 em qualquer rota, inclusive inexistente: a resposta não revela quais rotas existem. Com um token válido, a rota inexistente responde 404. As duas respostas saem em ProblemDetails ([Respostas de erro com ProblemDetails](architecture/error-responses.md)), e o 401 não explica por que o token foi recusado. As rotas administrativas (`/api/v1/admin/...`) só existem no listener administrativo, e as demais, só no público; no deploy, o listener administrativo deve ficar só em loopback ou na rede interna ([APIs e workers](architecture/hosts.md)).

## Executar os gateways externo e administrativo

A entrega [#83](https://github.com/adnerscarpelini/ouroboros-server/issues/83) adiciona dois gateways HTTP finos em `src/Gateways/`, separados por público e sem `DbContext`, regras de domínio ou orquestração síncrona ([arquitetura e garantias](architecture/hosts.md#gateways)):

| Executável | Público | Escuta padrão (`Gateway:Urls`) | `Gateway:AllowNonLoopback` | Prefixo encaminhado | Destino padrão (`Gateway:Downstream:IdentityBaseUrl`) |
| --- | --- | --- | --- | --- | --- |
| `Ouroboros.ExternalGateway.Api` | Externo (`External`) | `http://localhost:5151` (`http://0.0.0.0:5151` no Compose) | `true` | `/api/v1/identity/*` | `http://localhost:5141` (`http://identity-api:5141` no Compose) |
| `Ouroboros.AdminGateway.Api` | Administrativo (`Admin`) | `http://127.0.0.1:5152` | `false` | `/api/v1/admin/identity/*` | `http://127.0.0.1:5142` |

### Executar localmente com `dotnet run`

1. Inicie a API de Identity ([Executar a API de Identity](#executar-a-api-de-identity)). Para que o `Identity` aceite o `X-Forwarded-For` e o `X-Forwarded-Proto` repassados pelos gateways locais em loopback e contabilize o rate limit pelo IP real do cliente, defina `Identity:ForwardedHeaders:KnownProxies:0=127.0.0.1` (e `::1` quando usar IPv6 loopback) ao subir o `Identity`:

   ```powershell
   $env:Identity__ForwardedHeaders__KnownProxies__0 = '127.0.0.1'
   $env:Identity__ForwardedHeaders__KnownProxies__1 = '::1'
   dotnet run --project src/Services/Identity/Ouroboros.Identity.Api
   ```

2. Em outros terminais, na raiz do repositório, inicie os gateways finos:

   ```powershell
   # Gateway externo (escuta em http://localhost:5151 e encaminha /api/v1/identity/* para http://localhost:5141)
   dotnet run --project src/Gateways/Ouroboros.ExternalGateway.Api

   # Gateway administrativo (escuta apenas em loopback http://127.0.0.1:5152 e encaminha /api/v1/admin/identity/* para http://127.0.0.1:5142)
   dotnet run --project src/Gateways/Ouroboros.AdminGateway.Api
   ```

3. Consulte a prontidão e exercite as rotas através de cada gateway:

   ```powershell
   # Saúde e rotas públicas pelo ExternalGateway (:5151)
   Invoke-RestMethod -Uri http://localhost:5151/health/live
   Invoke-RestMethod -Uri http://localhost:5151/health/ready
   Invoke-RestMethod -Uri http://localhost:5151/api/v1/identity/keys

   # Saúde e login administrativo pelo AdminGateway (:5152, restrito a loopback)
   Invoke-RestMethod -Uri http://127.0.0.1:5152/health/live
   Invoke-RestMethod -Uri http://127.0.0.1:5152/health/ready
   ```

- Rotas do outro público (`/api/v1/admin/identity/*` em `:5151` ou `/api/v1/identity/*` em `:5152`) são recusadas na borda com `404 Not Found` sem chamar o `Identity`.
- Se o `Identity` estiver parado ou exceder `Gateway:Downstream:Timeout` (padrão `00:00:05`, máximo `00:00:30`), o gateway responde com ProblemDetails sanitizado `502 Bad Gateway` ou `504 Gateway Timeout`, sem expor URLs internas nem mensagens de socket.
- Tentar configurar um endereço fora de loopback em `Ouroboros.AdminGateway.Api` com `Gateway:AllowNonLoopback=false` impede a subida com `OptionsValidationException`.

### Executar com Docker Compose (`--profile gateways`)

No [`compose.yaml`](../compose.yaml), os gateways ficam no profile opcional `gateways` para não bloquear o acesso direto a `identity-api`:

```powershell
docker compose --profile gateways up -d --build external-gateway admin-gateway
Invoke-RestMethod -Uri http://127.0.0.1:5151/health/ready
Invoke-RestMethod -Uri http://127.0.0.1:5151/api/v1/identity/keys
Invoke-RestMethod -Uri http://127.0.0.1:5152/health/live
```

No Compose, Identity aceita cabeçalhos encaminhados somente de loopback e do IP estático EXTERNAL_GATEWAY_IP. A antiga variável IDENTITY_KNOWN_PROXY_NETWORK não é usada. O gateway administrativo usa network_mode: service:identity-api, acessa 127.0.0.1:5142 e tem sua porta 5152 publicada em loopback pelo serviço Identity. Compartilhar rede não compartilha processo, volumes ou segredos. Ao recriar Identity, recrie também o gateway administrativo.

Para aplicar a subnet a uma rede Compose já existente, pare e remova os containers e a rede com docker compose --profile gateways down, preservando os volumes; depois execute novamente a sequência de subida. Não use opções de remoção de volumes. Se houver sobreposição de rede, ajuste GATEWAY_NETWORK_SUBNET e EXTERNAL_GATEWAY_IP no .env antes de subir.

URLs HTTPS em Gateway:Urls ativam TLS e exigem certificado configurado no Kestrel. [GatewayTlsTests](../tests/Ouroboros.IntegrationTests/GatewayTlsTests.cs) verifica o handshake dos dois públicos.

### Verificar os testes dos gateways

Na raiz do repositório:

```powershell
# Testes de arquitetura dos gateways (sem Docker): isolamento, opções, sem DbContext/serviços e respostas 502/504 sanitizadas
dotnet test --project tests/Ouroboros.ArchitectureTests -- --filter-class "*GatewayArchitectureTests"

# Testes de integração dos gateways (exige Docker): rotas públicas e administrativas, autorização no Identity e rate limit com X-Forwarded-For entre réplicas
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class "*GatewayIntegrationTests"
```

## Logs dos executáveis

A API de Identity e o worker de Notifications registram pelo OpenTelemetry, único provedor de log, também no Windows: não há Windows Event Log nem saída de Debug. Cada entrada é sanitizada e escrita no console (stdout), uma linha JSON por entrada, com `timestamp` (UTC), `level`, `category`, `eventId`, `service` (`identity` ou `notifications`), `serviceVersion`, `traceId` e `spanId` (quando há uma atividade W3C corrente), `occurrenceId`, `message` e `attributes`; uma exceção aparece em `attributes` como `exception.type`, `exception.message` e `exception.stacktrace`, já sanitizados. Campos, exemplos e limites em [Correlação por W3C Trace Context](architecture/trace-correlation.md#formato-do-console). As linhas saem em lotes, em até cerca de 1 s depois do log. Os níveis seguem `Logging:LogLevel` do `appsettings.json` de cada executável; as categorias `Microsoft.EntityFrameworkCore.Database.Command` e `Microsoft.EntityFrameworkCore.Update` são suprimidas pela configuração comum para evitar SQL, parâmetros e duplicação da exceção. A fronteira ou catch registra a falha sanitizada, sem habilitar logging sensível do EF. Para guardar os diagnósticos fora do console, use o [coletor local](#diagnósticos-no-coletor-local).

Para achar os logs de uma resposta de erro, pegue o segundo segmento do `traceId` do ProblemDetails (`00-<trace-id>-<span-id>-<flags>`) e filtre as linhas por ele. Por exemplo, guardando a saída da API em `.local/` (ignorada pelo Git):

```powershell
dotnet run --project src/Services/Identity/Ouroboros.Identity.Api | Tee-Object -FilePath .local/identity.log
# Em outro terminal, com o <trace-id> da resposta:
Select-String -Path .local/identity.log -SimpleMatch '"traceId":"<trace-id>"'
```

O mesmo filtro vale para a saída do worker: a publicação da outbox e o processamento no worker de uma solicitação continuam o trace da requisição. Para que uma chamada continue um trace do cliente, envie um `traceparent` W3C válido (por exemplo, `-Headers @{ traceparent = '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01' }` no `Invoke-RestMethod`); valor inválido é descartado e o host gera um trace novo. Os logs são sanitizados, mas a sanitização reconhece só chaves e padrões ([limites](architecture/diagnostics-pipeline.md#limites)) e as linhas trazem identificadores e pilhas de exceção: não os copie para issues ou para o repositório e apague os arquivos locais depois da investigação.

Falhas do próprio pipeline, como fila cheia ou falha e timeout ao exportar para o coletor, não aparecem no stdout: saem em stderr, em linhas que começam com `ouroboros-observability` ([como interpretar](#interpretar-o-sinal-em-stderr)).

Nos dois públicos HTTP, cada `4xx`/`5xx` gera um resumo `Warning` com `Status`, `Route` normalizada, `DurationMs`, `Classification` e `Code`. Validação usa `validation-failed`; falha de negócio usa o código permitido do catálogo; erro sem código usa `http-<status>`; exceção não tratada usa `unhandled-exception`. Só a ocorrência técnica `Error` recebe a exceção, e o resumo compartilha o trace sem repeti-la. Cancelamento esperado fica em `Information`, com `expected-cancellation`. Catches de e-mail, publicador, consumidor, executor, relay e manutenção registram as falhas relevantes com contexto seguro. Critérios e limites em [Política de captura de falhas](architecture/error-responses.md#política-de-captura-de-falhas).

Testes da correlação e da sanitização, sem Docker:

```powershell
dotnet test --project tests/Ouroboros.ArchitectureTests -- --filter-class '*TraceCorrelationTests'
dotnet test --project tests/Ouroboros.ArchitectureTests -- --filter-class '*DiagnosticSanitizationTests'
```

Os cenários com publicação real e processo do worker ficam em `EmailOutboxPublisherTests` e `NotificationsWorkerTests` (exigem Docker), com os comandos de [outbox](#publicar-e-retomar-a-outbox-de-identity) e de [imagem do worker](#imagem-linux-de-notifications).

## Diagnósticos no coletor local

O coletor (`otel-collector`) e o destino dedicado (`diagnostics-store`) guardam os diagnósticos da API e do worker em arquivos rotacionados no volume `diagnostics-data`, com retenção limitada, sem acessar os bancos. O uso é opcional: sem endpoint configurado, os executáveis só escrevem no console. Decisão, recursos e limites em [Pipeline de diagnósticos sanitizados](architecture/diagnostics-pipeline.md).

### Subir o coletor

O Compose valida as variáveis do arquivo inteiro, então o `.env` precisa existir, como para o [RabbitMQ](#rabbitmq-local); o coletor e o destino não usam credenciais. Na raiz do repositório:

```powershell
docker compose up -d otel-collector
docker compose ps otel-collector diagnostics-store
```

O comando sobe também `diagnostics-volume-init`, que prepara `diagnostics-data` e `collector-queue` com dono 10001 e modo `0750`, e `diagnostics-store`. O coletor recebe OTLP/HTTP em `127.0.0.1:4318` (`OTEL_COLLECTOR_PORT` altera a porta da máquina); o destino e a telemetria interna não têm portas publicadas. OTLP sem TLS nem autenticação se restringe ao desenvolvimento local. Os erros dos dois ficam em `docker compose logs otel-collector diagnostics-store`, com rotação Docker de aproximadamente 10 MiB por container.

### Enviar os diagnósticos

Defina `Observability:Otlp:Endpoint` no executável. Por exemplo, para a API:

```powershell
$env:Observability__Otlp__Endpoint = 'http://127.0.0.1:4318/'
dotnet run --project src/Services/Identity/Ouroboros.Identity.Api
```

O mesmo vale para o worker de Notifications. De um container na rede do Compose, use `http://otel-collector:4318/`. O valor é uma URL `http` ou `https` absoluta só com host e porta: credenciais, caminho, query ou fragmento impedem a subida. O executável acrescenta `v1/logs`, `v1/traces` e `v1/metrics` para enviar os três sinais. Os appsettings não definem endpoint; `Remove-Item Env:Observability__Otlp__Endpoint` desativa o envio OTLP e mantém logs no console. Com o coletor parado, o executável continua funcionando e o descarte aparece em [stderr](#interpretar-o-sinal-em-stderr).

### Ler o destino

A imagem do coletor não tem shell; leia o volume por um container temporário, só leitura:

```powershell
docker run --rm -v ouroboros_diagnostics-data:/data:ro busybox:1.37.0 ls -l /data
docker run --rm -v ouroboros_diagnostics-data:/data:ro busybox:1.37.0 cat /data/logs.jsonl | Select-String -SimpleMatch '"traceId":"<trace-id>"'
docker run --rm -v ouroboros_diagnostics-data:/data:ro busybox:1.37.0 cat /data/traces.jsonl | Select-String -SimpleMatch '"traceId":"<trace-id>"'
docker run --rm -v ouroboros_diagnostics-data:/data:ro busybox:1.37.0 cat /data/metrics.jsonl | Select-String -SimpleMatch 'outbox.backlog'
```

Cada linha de `logs.jsonl` é um lote em OTLP/JSON (`resourceLogs`): o resource traz `service.name`, `service.version`, `service.instance.id` e `deployment.environment.name`, e cada registro traz `traceId`, `spanId`, a mensagem em `body` e os atributos, entre eles `log.record.uid`, igual ao `occurrenceId` do console. Para achar uma ocorrência vista no console, filtre pelo valor do `occurrenceId`. `traces.jsonl` contém `resourceSpans`, com spans HTTP, `outbox.publish`, `inbox.accept`, `inbox.deliver` e o ciclo raiz `outbox.publish.cycle`; filtre pelo mesmo trace. `metrics.jsonl` contém `resourceMetrics`, com as agregações dos [instrumentos e dimensões permitidas](architecture/diagnostics-pipeline.md#métricas-do-fluxo).

Os sinais chegam alguns segundos depois do evento, por causa dos lotes e do leitor periódico de métricas. Os gauges só aparecem depois do primeiro snapshot de cada serviço; valores terminais refletem linhas ainda retidas, sem contagem histórica durável. Cada sinal tem um arquivo ativo e até 8 rotacionados de 32 MiB, removidos por idade acima de 7 dias quando há rotação: aproximadamente 864 MiB para os três sinais ([retenção e limites](architecture/diagnostics-pipeline.md#retenção-e-limpeza)). Não copie o conteúdo para issues ou para o repositório.

### Interpretar o sinal em stderr

Os eventos `Warning` ou mais graves do OpenTelemetry no processo saem em stderr, nunca no stdout nem no destino, no formato:

```text
ouroboros-observability <instante UTC> <nível> <fonte>#<evento>: <mensagem> (suprimidos desde o último aviso: N)
```

| Mensagem | Significado |
| --- | --- |
| `'BatchLogRecordExportProcessor' exporting to 'OtlpLogExporter' dropped '<N>' item(s) due to buffer full` | A fila do processo para o coletor encheu (coletor lento ou indisponível, ou volume alto) e `N` registros não foram enviados a ele; o console tem fila própria |
| O mesmo, com `exporting to 'JsonConsoleLogExporter'` | O stdout não acompanhou o volume e `N` linhas do console foram descartadas |
| `BatchActivityExportProcessor` com `OtlpTraceExporter` e `dropped` | A fila de spans para o coletor encheu; os spans novos excedentes foram descartados |
| Falha ou timeout do exportador OTLP | O coletor não respondeu em 5 s ou recusou o lote: confira se está no ar, a porta e `docker compose logs otel-collector` |

Cada evento sai no máximo uma vez a cada 30 s; `N` no fim conta as repetições suprimidas. O descarte é informado de forma agregada, quando o processador encerra ou a exportação falha. O aviso do SDK de meter não assinado é excluído por representar a seleção intencional de métricas; outros motivos, instrumentos duplicados e falhas de exportação continuam visíveis. Para separar o sinal da saída normal, redirecione o stderr, por exemplo com `2> .local/identity-diagnostics.err` no `dotnet run`. A linha passa pela mesma sanitização de padrões, mas não a publique em issues.

### Recuperar o buffer do coletor

A fila persistente usa `collector-queue` em `/var/lib/ouroboros-collector`, separado de `diagnostics-data`: até 8 MiB serializados por sinal, arquivos ativos de até 32 MiB cada (96 MiB no total) e temporários de compactação no mesmo filesystem, que podem elevar o uso a aproximadamente 192 MiB. Reserve esse espaço além dos arquivos do destino. O executável continua com filas em memória; `200` OTLP pode anteceder a escrita da fila, pois há um batch em memória no coletor. Os 300 s de retry são por lote em processamento, sem TTL global da fila ([garantias e limites](architecture/diagnostics-pipeline.md#fila-persistente-e-isolamento-das-instâncias)).

Com o volume válido e preservado, reiniciar ou substituir o container retoma entradas já gravadas, inclusive depois de SIGKILL. `fsync` não protege perda do volume/host e o destino pode receber duplicatas. Não execute duas instâncias com o mesmo diretório ou escale o serviço compartilhando esse volume: cada instância precisa de volume exclusivo, por exemplo em projetos Compose distintos. O bbolt recusa o segundo escritor por timeout de lock.

Em erro de permissão, arquivo inválido ou disco cheio, confira os logs do coletor e a capacidade do filesystem. Preserve os arquivos; pare a instância antes de inspecionar ou fazer backup do volume. Corrija a permissão para o usuário 10001 ou a causa de falta de espaço, e reinicie usando o mesmo volume. Arquivo inválido pode impedir a subida e exige avaliação/restauração pelo operador: não é recriado automaticamente. Não apague o volume ou arquivos da fila como procedimento de recuperação.

Para observar admissão, envio e ocupação, leia as métricas Prometheus pela rede interna. Com o nome padrão do projeto, por exemplo (substitua `ouroboros_default` pela rede do projeto usado):

```powershell
docker run --rm --network ouroboros_default busybox:1.37.0 wget -qO- http://otel-collector:8888/metrics | Select-String 'otelcol_exporter_(enqueue_failed|send_failed|queue_size|queue_capacity)'
```

`enqueue_failed_*` indica falha ao admitir dados; `send_failed_*` inclui tentativas que ainda podem ser recuperadas. Crescimento desses contadores, junto da ocupação e dos logs, indica saturação ou falha; não os some como uma contagem de perdas únicas. A porta 8888 permanece interna, sem publicação no host.

### Parar o coletor

```powershell
docker compose stop otel-collector diagnostics-store
```

Entradas já gravadas em `collector-queue` permanecem para a próxima subida, desde que o volume seja preservado e continue válido. O batch em memória do coletor e as filas dos executáveis ainda têm janela de perda. `docker compose down` mantém `diagnostics-data` e `collector-queue`; os comandos documentados não removem esses volumes.

### Testes do pipeline

Com coletor e destino reais, exigem Docker e não usam os containers do Compose:

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class '*DiagnosticsPipelineTests'
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class '*CollectorRecoveryTests'
```

## Executar o worker de Notifications

`Ouroboros.Notifications.Worker` é um processo separado, sem API de negócio, com banco/migrations próprios e aceitação durável da #87. O consumidor vem desligado; habilitá-lo exige as dependências abaixo e provedor explícito. O único provedor disponível, `PickupDirectory`, é restrito a Development. Identity grava/publica intenções pela outbox das #85/#86. [Comportamento do worker](notifications/worker.md) e [deduplicação/recuperação](notifications/durable-processing.md).

### Banco e migrations de Notifications

Provisione `ouroboros_notifications`, `notifications_runtime` e `notifications_migrator` pelo [procedimento da #92](#provisionar-bancos-e-roles-por-serviço). Não use connection string de Identity nem fallback legado. Aplique as migrations pelo job explícito `migrate-notifications` do Compose, pelo comando CLI `migrate` do worker ou por `dotnet ef database update` com a variável `ConnectionStrings__Notifications` usando a role **`notifications_migrator`**, sem expô-la em argumento/histórico:

```powershell
docker compose --profile migrate run --rm migrate-notifications
# ou via CLI do worker no host:
$env:ConnectionStrings__Notifications = [Net.NetworkCredential]::new('', (Read-Host 'Conexão própria de migration de Notifications' -AsSecureString)).Password
dotnet run --project src/Services/Notifications/Ouroboros.Notifications.Worker -- migrate
# ou via dotnet-ef:
dotnet tool restore
dotnet ef database update --project src/Services/Notifications/Ouroboros.Notifications --startup-project src/Services/Notifications/Ouroboros.Notifications.Worker --context NotificationsDbContext
Remove-Item Env:ConnectionStrings__Notifications
```

`20261026081751_InitialNotificationsInbox` cria `notifications.inbox`, `supersessions`, `failure_outbox` e `processing_recoveries`, com histórico no mesmo schema; revoga seu acesso de runtime. Reaplique o provisionamento de privilégios após migrations conforme a operação dos bancos. Para executar, use **`notifications_runtime`** em `ConnectionStrings:Notifications` — também por variável `ConnectionStrings__Notifications` ou user-secrets próprios em Development. O validador recusa outra identidade de banco; runtime não aplica migrations, executa DDL ou lê o histórico. Não deixe a conexão de migration no ambiente do worker.

### Chaves de proteção de Notifications

Gere um par próprio, diferente da assinatura JWT, num diretório **novo**, externo ao checkout e ao contexto de build. O script não sobrescreve chaves nem aceita diretório existente/dentro de checkout e não imprime material de chave:

```powershell
$protectionDirectory = [IO.Path]::GetFullPath((Read-Host 'Diretório externo novo para proteção de Notifications'))
pwsh -NoProfile -File scripts/messaging/new-notification-protection-key.ps1 -Directory $protectionDirectory
```

O resultado é RSA3072, um PEM privado, `public.json` (`activeKeyId`, `keys` com `keyId`, `n`, `e`) e `private.json` (`keys` com `keyId`, `privateKeyPath`). No Windows, o diretório perde herança ampla e recebe ACL da conta que executou o gerador; no Linux, diretório `0700` e arquivos `0600`. Confira dono e permissões. Só Notifications recebe o manifesto/PEM privados; a operação entregará **somente `public.json`** ao produtor na #85. Para runtime sob outra conta, conceda leitura e travessia específicas, sem escrita ou acesso amplo. Nenhuma chave entra na imagem, Git ou logs.

Rotação é operacional: gere um próximo par em outro diretório novo, com `keyId` novo, distribua a pública antes de ativá-la na origem e conserve no manifesto privado as entradas ainda necessárias para mensagens válidas. Não retire uma privada enquanto houver mensagem recuperável que precise dela, considerando prazos e a retenção das #85/#87. Não reutilize chaves de assinatura JWT. O leitor aceita até 16 entradas únicas e relê o manifesto; não há rotação, distribuição ou remoção automática de chaves. Os manifests atualizados devem ser substituídos atomicamente, com as mesmas permissões; o gerador não mescla arquivos existentes.

### Chaves de fingerprint de Notifications

A deduplicação usa outro segredo HMAC de 32 bytes, exclusivo de Notifications. Gere-o em diretório **novo**, externo ao Git e ao contexto de build:

```powershell
$fingerprintDirectory = [IO.Path]::GetFullPath((Read-Host 'Diretório externo novo para fingerprint de Notifications'))
pwsh -NoProfile -File scripts/messaging/new-notification-fingerprint-key.ps1 -Directory $fingerprintDirectory
```

O script cria uma chave e `fingerprint.json` com `activeKeyId` e entradas `keyId`/`keyPath`, usando ACL da conta no Windows e diretório `0700`/arquivos `0600` no Linux. Configure `Notifications:FingerprintKeyRingPath` com caminho absoluto do manifesto. Conserve as entradas antigas enquanto houver tombstones que dependam delas, até 30 dias após o prazo absoluto; o leitor escolhe o `keyId` original ao comparar duplicatas. Admite até 16 entradas e não faz rotação automática. Não compartilhe esse segredo com Identity nem reutilize RSA, assinatura JWT, HMAC de alias ou chave AES de backup. Para outra conta/UID, conceda leitura/travessia específicas; ao montar no container, adapte os `keyPath` absolutos para o local interno, como no manifesto privado RSA.

### Configurar e iniciar

O worker tem `UserSecretsId` próprio, `ouroboros-notifications-development`. Não recebe a configuração de Identity. Opções do [`appsettings.json`](../src/Services/Notifications/Ouroboros.Notifications.Worker/appsettings.json):

| Opção em `Notifications` | Padrão e efeito |
| --- | --- |
| `Health:Urls` | `["http://127.0.0.1:5143"]`; listener técnico dos endpoints `/health/live`, `/health/ready` e `/health` (`http://0.0.0.0:5143` no container do Compose, publicado apenas em `127.0.0.1:5143` no host) |
| `Consumer:Enabled` | `false`; habilitar exige provedor, banco, proteção/fingerprint e brokers válidos |
| `Email:Provider` | `None`; `PickupDirectory` é aceito somente em Development; valor desconhecido é recusado |
| `Email:From` | `nao-responda@ouroboros.local`; endereço único, sem CR/LF |
| `Email:PickupDirectory` | `.local/notifications-emails`, relativa ao diretório compilado/publicado do worker; prefira pasta absoluta externa e restrita |
| `Broker:Host` / `Broker:Port` | `localhost` / `5672` para processo local |
| `Broker:User` / `Broker:Password` | `notifications_consumer` / externa; não usar operador nem credencial de falhas |
| `PrivateKeyRingPath` | Caminho absoluto de `private.json`, externo e legível somente pelo receptor autorizado |
| `FingerprintKeyRingPath` | Caminho absoluto de `fingerprint.json`, com chaves HMAC retidas para deduplicação |
| `FailuresBroker:Host` / `Port` / `User` / `Password` | `localhost` / `5672` / `notifications_failures` / externa; senha distinta da credencial de consumo |
| `ProcessingPollSeconds` | `1`, permitido de 1 a 30; intervalo do executor/relay |
| `BackupDirectory` | Opcional; diretório gerenciado de backups **próprio de Notifications** para purga automática |

`Identity:Email` no worker é erro de configuração. `None` não envia nem simula sucesso. A configuração é validada na subida; senha de broker ausente/curta, usuário incorreto ou caminho privado não absoluto impedem habilitar consumo. Privada indisponível/incorreta suspende o processamento sem gastar chamada ao provedor; conteúdo ou tag AES inválidos terminam como `invalid-contract`, sem expor o conteúdo.

Com banco migrado, RabbitMQ [iniciado/provisionado](#rabbitmq-local), chaves geradas e pasta de pickup externa restrita à conta do worker:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$env:Notifications__Consumer__Enabled = 'true'
$env:Notifications__Email__Provider = 'PickupDirectory'
$env:Notifications__Email__PickupDirectory = [IO.Path]::GetFullPath((Read-Host 'Pasta externa restrita para os e-mails'))
$env:Notifications__PrivateKeyRingPath = Join-Path $protectionDirectory 'private.json'
$env:Notifications__FingerprintKeyRingPath = Join-Path $fingerprintDirectory 'fingerprint.json'
$env:ConnectionStrings__Notifications = [Net.NetworkCredential]::new('', (Read-Host 'Conexão própria de notifications_runtime' -AsSecureString)).Password
# O gerenciador de segredos pode definir esta variável; a leitura abaixo não grava a senha no histórico.
$env:Notifications__Broker__Password = [Net.NetworkCredential]::new('', (Read-Host 'Senha de notifications_consumer' -AsSecureString)).Password
$env:Notifications__FailuresBroker__Password = [Net.NetworkCredential]::new('', (Read-Host 'Senha distinta de notifications_failures' -AsSecureString)).Password
dotnet run --project src/Services/Notifications/Ouroboros.Notifications.Worker
```

O worker recebe o contrato protegido na fila `notifications.email.v1`, persiste antes do ACK e envia pelo executor; o relay publica somente referências sanitizadas de falha. Ajuste `Notifications__Broker__Host`/`Port` **e** `Notifications__FailuresBroker__Host`/`Port` ao ambiente; defaults `localhost:5672` servem para processo local. Fora de Development, os brokers exigem TLS/certificados válidos, sem declarar topologia pelo runtime. `.eml` contém link secreto em claro como artefato de entrega: restrinja a pasta e não copie conteúdo a logs/issues. A idempotência do arquivo complementa a inbox, sem prometer exactly-once. Depois do ensaio, retire da sessão as duas senhas e `ConnectionStrings__Notifications` se foram definidas para ele.

### Recuperar processamento e referências de falha

Primeiro corrija a chave/configuração e reinicie o processo se alterou opções. Trabalho `Accepted` suspenso por proteção indisponível pode ser retomado explicitamente, sem ler conteúdo. Com conexão libpq autorizada do **banco de Notifications**, execute por id público:

```powershell
$messageId = [Guid]::Parse((Read-Host 'Id da mensagem suspensa')).ToString('D')
psql --no-psqlrc --set=ON_ERROR_STOP=1 --set=message_id=$messageId --file scripts/messaging/resume-notification-processing.sql
```

Só retoma suspensa ainda válida, com conteúdo, sem lease e com menos de cinco chamadas. Preserva identidade, contagem e prazo e audita `current_user` em `processing_recoveries`. Zero linhas não autoriza forçar terminal/contador. `Delivered`, `Failed`, `Expired` e `Cancelled` não são reabertos; recusa permanente ou esgotamento descartam conteúdo e exigem nova intenção de negócio. [Estados e tentativas](notifications/durable-processing.md#executor-tentativas-e-ambiguidade).

Uma referência sanitizada suspensa por rota/autenticação/topologia do broker de falhas tem retomada separada, depois do reparo:

```powershell
$failureId = [Guid]::Parse((Read-Host 'Id da referência de falha suspensa')).ToString('D')
psql --no-psqlrc --set=ON_ERROR_STOP=1 --set=failure_id=$failureId --file scripts/messaging/resume-notification-failure-publication.sql
```

A operação também é auditada; só retoma referência não publicada, suspensa, sem lease e dentro da retenção. Não altera o e-mail nem reenvia seu conteúdo. Não copie o corpo/ciphertext para a fila de falhas nem republique uma referência na fila principal para fazer replay. A fila recebe somente ids/códigos/prazos; o TTL usa o tempo restante, não um novo prazo de 30 dias.

### Backup e restauração de Notifications

Use o mesmo [wrapper de backup protegido](#backup-protegido-das-intenções-de-e-mail) com dump do banco de Notifications, chave AES externa própria e diretório gerenciado **separado** do de Identity. Configure `Notifications:BackupDirectory` (ou `Notifications__BackupDirectory`) para purgar esses arquivos; a limpeza é tentada antes do SQL, independentemente do executor/banco. O formato continua `email-intents-*.json`, com marcador do wrapper: não renomeie arquivos para induzir adoção de backups legados.

Restaure com `Notifications:Consumer:Enabled=false`, credencial própria e destinos controlados; preserve os terminais e não zere tentativas nem estenda expiração. Depois do restore, mantenha consumidor/executor/relay desabilitados e execute a manutenção com a conexão de runtime válida para reaplicar expiração/limpeza antes de habilitar. A manutenção roda mesmo com consumo desligado quando a conexão está configurada. Não há promessa de RPO zero. Auditoria do wrapper cobre suas operações; acesso direto a arquivos requer controles do operador.

### Imagem Linux de Notifications

Na raiz do repositório, com Docker disponível, construa as imagens versionadas de Notifications e de Identity passando `APP_VERSION` e `SOURCE_REVISION` (`<version>-<commit>`):

```powershell
docker build -f src/Services/Identity/Ouroboros.Identity.Api/Dockerfile --build-arg APP_VERSION=0.1.0 --build-arg SOURCE_REVISION=local -t ouroboros-identity-api:0.1.0-local .
docker build -f src/Services/Notifications/Ouroboros.Notifications.Worker/Dockerfile --build-arg APP_VERSION=0.1.0 --build-arg SOURCE_REVISION=local -t ouroboros-notifications-worker:0.1.0-local .
docker run --rm --entrypoint sh ouroboros-identity-api:0.1.0-local -c 'id -u'
docker run --rm --entrypoint sh ouroboros-notifications-worker:0.1.0-local -c 'id -u; test -w /app/.local/notifications-emails'
```

O build usa SDK `10.0.402` e runtime `aspnet:10.0.13-alpine3.24`; ambos os processos executam como UID `1654` (`USER $APP_UID`), com pickup padrão gravável no worker e rótulos OCI `org.opencontainers.image.version` iguais a `<APP_VERSION>-<SOURCE_REVISION>`. Cada Dockerfile copia somente o próprio serviço e bibliotecas técnicas, sem assemblies do outro serviço, e tem `.dockerignore` próprio para excluir `.env`, `*.pem`, `*.key`, `.local`, `backups`, `bin` e `obj`. Mantenha os segredos fora do contexto, mesmo com essas exclusões.

Para executar os dois serviços em containers com chaves externas, migrations explícitas e probes de readiness, use o fluxo completo em [Executar os serviços com Docker Compose](#executar-os-serviços-com-docker-compose), que utiliza `scripts/messaging/new-compose-keys.ps1` para gerar os manifestos com os caminhos internos `/run/ouroboros-keys/identity/...` e `/run/ouroboros-keys/notifications/...`. Se montar manualmente sem esse script, ajuste `privateKeyPath` e `keyPath` nos manifestos para o caminho absoluto interno do container e conceda leitura dos arquivos e travessia do diretório ao UID `1654`, preservando escrita exclusiva da operação. A CI constrói ambas as imagens a cada PR/push; publicação em registro e deploy ficam fora do escopo.

Teste reproduzível do worker com broker real e processo filho, sem alterar o `.env` ou broker local:

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class '*NotificationsWorkerTests'
```

## Configurar a outbox de Identity

Identity exige configuração de proteção de intenção, além do banco e da assinatura JWT, antes de iniciar. `Identity:Email` foi removida e é recusada; remova a seção antiga de user-secrets/variáveis externas, sem migrar sua privada ou provedor para a origem. [Atomicidade, estados e limites](identity/email-outbox.md).

| Opção em `Identity:EmailIntents` | Uso |
| --- | --- |
| `PublicKeyRingPath` | Caminho absoluto externo de uma cópia de `public.json` de Notifications |
| `SupersessionKeyPath` | Caminho absoluto externo do segredo HMAC de substituição, exatamente 32 bytes |
| `MaintenanceEnabled` | `true` por padrão; limpeza na subida e a cada 30 s, em lotes limitados |
| `BackupDirectory` | Opcional; diretório dedicado reconhecido pelo wrapper de backup, necessário para purga automática desses arquivos |

1. Gere o par de proteção de Notifications conforme [chaves do receptor](#chaves-de-proteção-de-notifications). Entregue à conta de Identity somente uma cópia de `public.json`, protegida contra escrita não autorizada. Não monte `private.json` ou PEM de Notifications em Identity. A origem recusa material privado no manifesto e reutilização de uma pública JWT conhecida. O receptor precisa conservar as privadas necessárias às intenções/backups ainda retidos; a troca de `activeKeyId` é operacional e não muda o segredo de substituição.
2. Gere o segredo HMAC em outro diretório **novo**, externo ao código e ao contexto de build:

   ```powershell
   $aliasDirectory = [IO.Path]::GetFullPath((Read-Host 'Diretório externo novo para o alias de e-mail'))
   pwsh -NoProfile -File scripts/messaging/new-email-intent-alias-key.ps1 -Directory $aliasDirectory
   ```

   O script produz `supersession.key`, 32 bytes aleatórios, sem imprimir a chave. Aplica ACL da conta geradora no Windows, ou diretório `0700` e arquivo `0600` no Linux. Conceda somente leitura à conta de Identity quando distinta da operação; não entregue esse segredo a Notifications. Mantenha-o estável durante o ciclo das mensagens: mudar RSA não deve mudar o alias. Não há migração automática de aliases em rotação de HMAC.
3. Configure os caminhos; os comandos abaixo armazenam apenas caminhos, sem conteúdo secreto:

   ```powershell
   $publicPath = [IO.Path]::GetFullPath((Read-Host 'Cópia pública de Notifications destinada a Identity'))
   dotnet user-secrets set 'Identity:EmailIntents:PublicKeyRingPath' $publicPath --project src/Services/Identity/Ouroboros.Identity.Api
   dotnet user-secrets set 'Identity:EmailIntents:SupersessionKeyPath' (Join-Path $aliasDirectory 'supersession.key') --project src/Services/Identity/Ouroboros.Identity.Api
   ```

   Fora de Development, use `Identity__EmailIntents__PublicKeyRingPath` e `Identity__EmailIntents__SupersessionKeyPath` com arquivos externos montados somente para a origem. A subida executa uma prova de proteção local, sem banco, mensagem real ou rede; configuração ilegível/inválida não é um modo de envio desprotegido.
4. Aplique a migration `20261026015350_AddEmailIntents` por comando explícito com a role de migration, conforme [banco local](#banco-de-dados-local). Reexecute o provisionamento de privilégios depois da migration. A API não aplica migrations na subida. Em ambiente existente, preserve backups e confira o [limite de retorno entre schemas](#voltar-ao-banco-original).

Após a migração, solicitação de cadastro/reset grava token e envelope cifrado na mesma transação; não cria `.eml` em Identity. A manutenção marca vencidas e elimina o conteúdo sem depender do broker. Desabilitá-la com `Identity__EmailIntents__MaintenanceEnabled=false` é opção de ensaio, sem satisfazer a limpeza operacional exigida. Para arquivos gerenciados, configure também `BackupDirectory` e dê à conta da manutenção somente as permissões necessárias à remoção; ela não precisa receber a chave que decifra os backups.

### Publicar e retomar a outbox de Identity

A #86 executa o publicador como serviço hospedado isolado dentro de Identity. A migration `20261026025858_AddEmailPublicationLeases` precisa estar aplicada pela role de migration, conforme o [banco local](#banco-de-dados-local). O caso de uso HTTP continua gravando intenção local, sem esperar o broker. [Confirm, leases, retries e limites](identity/email-outbox.md#publicação-e-recuperação).

| Opção em `Identity:EmailPublisher` | Padrão e limite |
| --- | --- |
| `Enabled` | `false`; habilitação explícita |
| `Host` / `Port` | `localhost` / `5672`; fora de Development exige listener TLS e certificado válido |
| `User` | `identity_publisher`, sem consumo ou configuração da topologia |
| `Password` | Externa, no mínimo 32 caracteres; não usar credencial do operador |
| `BatchSize` | `4`, permitido de 1 a 8; publicações sequenciais por ciclo |
| `PollSeconds` | `2`, permitido de 1 a 30; espera entre ciclos |

Para ensaio local, [inicie/provisione RabbitMQ](#rabbitmq-local), configure a [proteção da outbox](#configurar-a-outbox-de-identity) e aplique as migrations. As variáveis abaixo habilitam a publicação no processo de Identity sem gravar a senha em argumento ou histórico:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Identity__EmailPublisher__Enabled = 'true'
# Pode vir do gerenciador de segredos; use a senha da credencial identity_publisher.
$env:Identity__EmailPublisher__Password = [Net.NetworkCredential]::new('', (Read-Host 'Senha de identity_publisher' -AsSecureString)).Password
dotnet run --project src/Services/Identity/Ouroboros.Identity.Api
```

`dotnet run` usa o perfil Development padrão. Fora do ambiente local, configure `Identity__EmailPublisher__Host`/`Port` para o listener TLS; não desabilite a validação do certificado. O processo não declara topologia: faça a correção pelo provisionador/operador. Para ver `.eml` em Development, inicie separadamente o [worker](#executar-o-worker-de-notifications) com as privadas de proteção necessárias. O consumidor da #87 persiste aceitação/deduplicação antes do ACK; configure seu banco, fingerprint e broker de falhas conforme a seção do worker. Após o ensaio, remova a senha da sessão se foi definida localmente: `Remove-Item Env:Identity__EmailPublisher__Password`.

Broker parado/timeout gera retry com recursos limitados e preserva a intenção até o prazo. `basic.nack` por capacidade é transitório. Autenticação, certificado, permissão/topologia conhecida ou retorno por ausência do binding fixo suspendem a intenção com `last_failure_code=configuration-suspended`: **corrigir e reiniciar não retoma a linha automaticamente**. Corrija a causa, reinicie Identity se alterou opções/credenciais e execute a retomada explícita pela conexão autorizada do banco de Identity. Com autenticação libpq já configurada pelo operador, sem credenciais em argumentos:

```powershell
$messageId = [Guid]::Parse((Read-Host 'Id público da intenção suspensa')).ToString('D')
psql --no-psqlrc --set=ON_ERROR_STOP=1 --set=message_id=$messageId --file scripts/messaging/resume-email-publication.sql
```

O script retorna somente id, estado, tentativas e prazo. Só retoma `Pending` suspensa, ainda válida, com conteúdo protegido e sem lease; mantém id, tentativas e expiração. Se nenhuma linha for atualizada, não force estado nem recrie prazo: ela pode estar em processamento ou terminal. Expiradas/canceladas não são ressuscitadas. Envelope inválido/divergente termina em `Failed`, com descarte do conteúdo, e exige nova intenção de negócio após corrigir a causa. Nunca selecione o payload para investigar a retomada.

O `Meter` `Ouroboros.Identity.Outbox` disponibiliza backlog, idade pendente em segundos, falhas, retries e estados terminais. Falhas/retries têm somente o código de conjunto fechado, e estados terminais têm `state`; gauges refletem o último snapshot do ciclo e só emitem após o primeiro. Com `Observability:Otlp:Endpoint`, as métricas seguem ao [coletor local](#diagnósticos-no-coletor-local), junto dos sinais de Notifications ([instrumentos](architecture/diagnostics-pipeline.md#métricas-do-fluxo)). Com o broker parado, `GET /health/ready` de Identity permanece `200 OK` (`{"status":"Healthy"}`) e o login continua funcionando, enquanto `GET /health/ready` de Notifications responde `503 Service Unavailable` (`{"status":"Unhealthy"}`).

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class '*EmailOutboxPublisherTests'
```

### Backup protegido das intenções de e-mail

O wrapper `scripts/messaging/protected-email-backup.ps1` protege um dump custom **já produzido** pelo operador, até 64 MiB. Não acessa o banco, não recebe sua connection string e não substitui a janela/validação do procedimento de transferência. Use diretório de backup novo/dedicado, fora do Git, com chave AES256 de **32 bytes externos** em outro diretório protegido. Essa chave e seu `KeyId` são próprios de backup, distintos de JWT, proteção RSA e HMAC de alias; gere/guarde pelo gerenciador de segredos, sem imprimir bytes ou usar valores em argumentos.

Exemplo com clientes PostgreSQL locais e autenticação segura previamente configurada por libpq (`PGHOST`, `PGPORT`, `PGDATABASE`, `PGUSER` e `PGPASSFILE`, conforme a credencial de backup autorizada). Restrinja também a pasta de dump temporário; o arquivo ainda é sensível:

```powershell
$sourceDump = [IO.Path]::GetFullPath((Read-Host 'Novo arquivo temporário restrito para pg_dump'))
pg_dump --format=custom --no-owner --no-acl --file $sourceDump
if ($LASTEXITCODE -ne 0) { throw 'Backup PostgreSQL não concluído' }
$backupDirectory = [IO.Path]::GetFullPath((Read-Host 'Diretório externo novo/dedicado de backup protegido'))
$backupKeyPath = [IO.Path]::GetFullPath((Read-Host 'Arquivo externo da chave de backup de 32 bytes'))
pwsh -NoProfile -File scripts/messaging/protected-email-backup.ps1 -Mode Protect -SourceFile $sourceDump -Directory $backupDirectory -KeyPath $backupKeyPath -KeyId email-backup-v1
```

O diretório recebe marcador `.email-intent-backups`, ACL restrita ou `0700`; os arquivos protegidos usam `0600` no Linux e nome `email-intents-<UTC yyyyMMddHHmmss>-<UUID>.json`. AES-GCM autentica o dump e metadados de versão/chave/criação/expiração. O conteúdo protegido expira **30 dias após sua criação**. O wrapper não sobrescreve backups nem adota diretório existente sem marcador reconhecido. `-RemoveSource` é uma opção **explícita** para retirar o dump de entrada após Protect; sem ela, o dump temporário permanece e o operador precisa tratá-lo pela mesma política, sem considerá-lo protegido só porque uma cópia foi cifrada.

Configure o mesmo diretório para a manutenção da API:

```powershell
dotnet user-secrets set 'Identity:EmailIntents:BackupDirectory' $backupDirectory --project src/Services/Identity/Ouroboros.Identity.Api
```

Fora de Development, use `Identity__EmailIntents__BackupDirectory`. A limpeza de arquivos gerenciados é tentada antes da limpeza de banco, em lotes limitados, inclusive se PostgreSQL estiver indisponível. Só remove nomes/prazos reconhecidos sob o marcador e não segue links. Sem essa configuração, a purga automática de arquivos não está ativa. A retenção de banco também não elimina cópias fora desse diretório.

Para restaurar, selecione um arquivo protegido ainda válido, um `RestoreFile` **novo**, com pai restrito, e a chave correspondente. Não use saída do comando para transportar conteúdo:

```powershell
$protectedArchive = [IO.Path]::GetFullPath((Read-Host 'Arquivo de backup cifrado a restaurar'))
$restoreDump = [IO.Path]::GetFullPath((Read-Host 'Novo arquivo temporário restrito para restaurar'))
pwsh -NoProfile -File scripts/messaging/protected-email-backup.ps1 -Mode Restore -SourceFile $protectedArchive -Directory $backupDirectory -KeyPath $backupKeyPath -KeyId email-backup-v1 -RestoreFile $restoreDump
```

Restore autentica antes de escrever e recusa o prazo vencido, formato/chave errados ou destino existente. O resultado é novamente um dump sensível, não uma restauração automática no PostgreSQL. Mantenha processamento parado; restaure com o operador autorizado e reaplique expiração, estados terminais e limpeza antes de habilitar publicação/consumo. Não reabra mensagens canceladas ou vencidas, nem recrie seus prazos.

O wrapper grava auditoria em `access-YYYYMMDD.jsonl`, com id da operação, Protect/Restore, início/sucesso/falha, instante UTC e ator, sem conteúdo ou caminhos. Cobre operações pelo wrapper; acesso direto ao filesystem exige controle/auditoria do operador e não fica automaticamente registrado nele. Os arquivos diários também entram na limpeza limitada: são elegíveis após 31 dias do início UTC do dia. Backups cifrados continuam com teto de 30 dias.

Backups antigos de `transfer-identity.sh` e seus CSVs de reconciliação não são adotados, cifrados ou apagados por essa manutenção. O operador deve protegê-los, restringir acesso e retirar cópias temporárias/legadas conforme a política, sem apagar a origem ou artefatos de transferência incidentalmente. Não prometa que `BackupDirectory` cobre arquivos de outros formatos ou locais.

## E-mails em desenvolvimento

Identity não possui mais provedor ou pickup local: suas solicitações gravam a [outbox protegida](identity/email-outbox.md). `Identity:Email` é recusada. O worker possui `Notifications:Email` e gera `.eml` somente ao receber um envelope protegido válido, conforme [execução do worker](#executar-o-worker-de-notifications). A publicação automática exige [habilitar o publicador](#publicar-e-retomar-a-outbox-de-identity); Notifications persiste aceitação/deduplicação pela #87, sem prometer exactly-once no provedor. Não leia ou registre o token da outbox para contornar essa etapa.

## Cadastrar um cliente e entrar

O contrato HTTP mantém as duas etapas: solicitar o cadastro e concluir com o token recebido pelo canal de comunicação. O primeiro comando confirma a resposta genérica e, para e-mail elegível, persiste token/intenção. O caso de uso HTTP não gera `.eml`: em Development, habilite [publicador](#publicar-e-retomar-a-outbox-de-identity) e [worker](#executar-o-worker-de-notifications) para receber o arquivo no pickup de Notifications. Notifications reconhece a mensagem somente depois de persistir sua aceitação/classificação, conforme a #87.

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:5141/api/v1/identity/registrations -ContentType 'application/json' -Body '{"email":"cliente@example.com"}'
```

Resposta `202` tem sempre o mesmo corpo, com ou sem conta, e não garante entrega. Os exemplos da [conclusão de cadastro](identity/registration.md#concluir-o-cadastro) usam `<token do link>` como marcador de contrato; em Development, obtenha-o somente no artefato entregue pelo pickup configurado de Notifications, sem expô-lo em logs ou consultar a outbox protegida. Depois de concluir com token válido, o cliente pode entrar pelo [login](identity/login.md#como-usar).

## Redefinir a senha em desenvolvimento

Com uma conta de cliente criada, solicite a redefinição pelo listener público:

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:5141/api/v1/identity/password-resets -ContentType 'application/json' -Body '{"email":"cliente@example.com"}'
```

A resposta continua genérica `202`; para conta elegível, a intenção é atômica com o token, e a senha atual continua valendo. Com publicador/worker desabilitados, não há `.eml`; para o ensaio local, habilite os dois conforme [publicação](#publicar-e-retomar-a-outbox-de-identity) e [Notifications](#executar-o-worker-de-notifications). A [conclusão de reset](identity/password-reset.md#concluir-a-redefinição) mantém seu contrato com `<token do link>`; testes de integração obtêm o conteúdo usando somente chaves efêmeras de teste. Não há instrução de decifrar a outbox real para copiar o token. A publicação é confirmada na #86, e a aceitação/deduplicação durável está na #87, com possível ambiguidade na entrega ao provedor.

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
