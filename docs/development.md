# Executar o checkout atual

Este guia contém comandos dos projetos disponíveis. A organização Services e os executáveis independentes são acompanhados no [estado de implementação](implementation-status.md). Não execute caminhos futuros como se já existissem.

## Requisitos

- .NET SDK 10.0.402, fixado em `global.json`.
- Visual Studio compatível com .NET 10 e SLNX, se for usar o Visual Studio.
- Docker com Docker Compose, para o PostgreSQL de desenvolvimento; os testes de integração também exigem o Docker em execução.

## Visual Studio e compilação

Abra `Ouroboros.slnx` por **Arquivo → Abrir → Projeto/Solução**. Abrir somente a pasta do repositório usa a visualização de arquivos. `src`, `src/Modules`, `src/Services` e `src/Services/Identity` são pastas organizadoras da solution. Identity é o projeto `Ouroboros.Identity`, em `src/Services/Identity/Ouroboros.Identity`; Accounts, Customers, Settings e Transactions são projetos em `src/Modules`. Cada projeto de biblioteca é independente e compilável pelo menu de contexto **Compilar**.

Defina `Ouroboros.Api` e/ou `Ouroboros.Backoffice.Api` como projetos de inicialização para executar os hosts (para os dois ao mesmo tempo, use **Configurar Projetos de Inicialização → Vários projetos de inicialização**). As bibliotecas de Identity e dos módulos não são executáveis independentes.

Na raiz do repositório:

```powershell
dotnet restore Ouroboros.slnx
dotnet build Ouroboros.slnx --no-restore
dotnet build src/Services/Identity/Ouroboros.Identity/Ouroboros.Identity.csproj
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

- Os testes de arquitetura não acessam o banco: sobem o host com uma connection string e uma chave de assinatura fictícias. Os testes não usam a chave dos user-secrets.
- Os testes de dependência analisam os assemblies de todos os projetos de `src`, listados em `architecture-assemblies.txt`, gerado no build do projeto de testes. Compile antes (`dotnet build Ouroboros.slnx`); sem o manifesto, o teste falha pedindo a compilação. Um serviço ou executável novo em `src` é analisado automaticamente. Regras e lista de transição em [Dependências entre camadas e serviços](architecture/layer-dependencies.md#verificação).
- Os testes de integração exigem o Docker em execução. Eles sobem o próprio PostgreSQL em container com Testcontainers e não usam o banco do Docker Compose, o `.env` nem os user-secrets. A primeira execução baixa a imagem `postgres:18-alpine`; as seguintes levam poucos segundos.
- Os testes de provisionamento (`DatabaseProvisioningTests`) também exigem o Docker. [ProvisionedInstance](../tests/Ouroboros.IntegrationTests/ProvisionedInstance.cs) sobe uma instância PostgreSQL própria, separada da `PostgresFixture` porque o provisionamento cria roles e revoga `CONNECT`, efeitos de instância inteira. A instância nasce com um banco `ouroboros` com dados legados e com log verboso (`log_statement=all`, `log_min_duration_statement=0`), para provar que as senhas não chegam ao log do servidor. O teste copia `scripts/database` para o container e executa o mesmo `psql` do comando do Compose, com senhas aleatórias geradas a cada execução; não usa o `.env`.
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

O checkout usa [PostgresFixture](../tests/Ouroboros.IntegrationTests/PostgresFixture.cs): um container por suíte e um banco modelo com migrations aplicadas. Cada teste recebe banco clonado, isolado dos demais. [IntegrationTest](../tests/Ouroboros.IntegrationTests/IntegrationTest.cs) cria a API e remove somente seu banco ao terminar.

[IntegrationApiFactory](../tests/Ouroboros.IntegrationTests/IntegrationApiFactory.cs) e [BackofficeIntegrationApiFactory](../tests/Ouroboros.IntegrationTests/BackofficeIntegrationApiFactory.cs) iniciam os hosts em Testing, com configuração em memória que sobrepõe connection string e chave fictícia. Limites elevados evitam interferência nos testes de negócio; RateLimitingTests usa limites próprios. Casos que dependem de tempo ou corridas substituem TimeProvider/hasher por adaptadores controlados. [CapturingEmailSender](../tests/Ouroboros.IntegrationTests/CapturingEmailSender.cs) captura links e simula falha sem envio real; EmailTests verifica o adaptador .eml. Isso não comprova entrega distribuída.

## Banco de dados local

Os dois hosts exigem a connection string `ConnectionStrings:Ouroboros`: sem ela, falham ao subir com `OptionsValidationException` e uma mensagem que cita a chave. As migrations nunca são aplicadas na subida; rode-as por comando. Todos os comandos abaixo são executados na raiz do repositório.

1. Crie o `.env` a partir do modelo e troque a senha de `POSTGRES_PASSWORD`, além das quatro senhas por serviço quando for [provisionar os bancos](#provisionar-bancos-e-roles-por-serviço) (o `.env` fica fora do Git):

   ```powershell
   cp .env.example .env
   ```

2. Suba o PostgreSQL e aguarde o healthcheck:

   ```powershell
   docker compose up -d --wait
   ```

3. Grave a connection string nos user-secrets do projeto `Ouroboros.Api`, com os valores do `.env`. O `Ouroboros.Backoffice.Api` usa o mesmo `UserSecretsId`, então lê a mesma connection string:

   ```powershell
   dotnet user-secrets set "ConnectionStrings:Ouroboros" "Host=localhost;Port=5432;Database=ouroboros;Username=ouroboros;Password=<a senha do .env>" --project src/Ouroboros.Api
   ```

4. Para **instalação nova**, sem tabelas legadas de parâmetros, restaure a ferramenta local `dotnet-ef` e aplique as migrations de Identity. Para **banco existente**, antes de qualquer atualização siga o [procedimento de backup restaurado, janela sem escrita e transferência](identity/parameters.md#upgrade-de-banco-existente). Se migrations antigas de Settings precisarem ser aplicadas, faça isso antes de Identity; nunca depois do corte.

   ```powershell
   dotnet tool restore
   dotnet ef database update --project src/Services/Identity/Ouroboros.Identity --startup-project src/Ouroboros.Api --context IdentityDbContext
   ```

   `OwnIdentityParameters` cria sementes com `uuidv7()` em instalação limpa, exigindo PostgreSQL 18 (a versão do `compose.yaml`). No upgrade, copia e valida os parâmetros e histórico de Settings na mesma transação. A migration não faz backup automaticamente nem exclui a origem; não ative a versão nova se ela falhar.

   No primeiro `database update` em um banco vazio, o log mostra `Failed executing DbCommand` sobre `__EFMigrationsHistory` antes de criar a tabela. É um comportamento conhecido do EF Core: o comando termina com `Done.`. Repetir o comando não altera nada.

5. Opcionalmente, confira os schemas: `identity` contém `users`, `sessions`, `refresh_tokens`, `email_tokens`, `parameters` e `parameter_history`. Em banco migrado, `settings` continua preservado, sem novas escritas; não é necessário criá-lo em instalação limpa. Ajuste usuário e banco se mudou o `.env`:

   ```powershell
   docker compose exec postgres psql -U ouroboros -d ouroboros -c "\dn"
   ```

Para gerar uma nova migration do Identity:

```powershell
dotnet ef migrations add <Nome> --project src/Services/Identity/Ouroboros.Identity --startup-project src/Ouroboros.Api --context IdentityDbContext --output-dir Infrastructure/Persistence/Migrations
```

`docker compose down` para o banco e mantém os dados no volume `postgres-data`. Preserve esse volume e os backups durante a migração. Fora de desenvolvimento, a connection string vem da variável de ambiente `ConnectionStrings__Ouroboros`. Detalhes e o padrão para outros módulos estão em [Persistência com PostgreSQL e EF Core](architecture/persistence.md).

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

O provisionamento não altera nenhum host nem aplica migrations: os hosts seguem lendo `ConnectionStrings:Ouroboros`, configurada em [Banco de dados local](#banco-de-dados-local). A disponibilidade de cada etapa está no [estado de implementação](implementation-status.md).

## Criar o primeiro administrador

Administradores não se cadastram sozinhos. Com o banco configurado e as migrations de Identity aplicadas, incluindo seus parâmetros locais, crie o primeiro `Admin` pelo comando `create-admin` do backoffice, que roda e encerra sem subir o servidor HTTP. A senha vai na variável de ambiente `OUROBOROS_ADMIN_PASSWORD`, nunca em argumento (argumentos ficam no histórico do shell e na lista de processos):

```powershell
$env:OUROBOROS_ADMIN_PASSWORD = '<senha>'
dotnet run --project src/Ouroboros.Backoffice.Api -- create-admin --email <e-mail>
Remove-Item Env:OUROBOROS_ADMIN_PASSWORD
```

No bash: `OUROBOROS_ADMIN_PASSWORD='<senha>' dotnet run --project src/Ouroboros.Backoffice.Api -- create-admin --email <e-mail>`.

- O e-mail precisa ser do domínio do parâmetro `Identity:AdminInvitations:AllowedEmailDomain` (valor inicial `adnerscarpelini.com`), sem subdomínios.
- A senha tem de 15 a 128 caracteres, sem regras de composição (frases com espaços e acentos valem), e não pode ser comum nem conter `ouroboros` ou a parte local do e-mail ([Política de senha](architecture/password-policy.md)).
- Só funciona enquanto não houver nenhum `Admin`; os próximos entram por convite.
- Código de saída `0`: criado; `1`: recusado, com a mensagem da regra e `Nada foi criado.`; `2`: falta `--email` ou a variável da senha.

Detalhes em [Criar o primeiro administrador](identity/first-admin.md). Com o `Admin` criado, entre pelo login do backoffice, `POST /api/v1/admin/identity/sessions` ([Fazer login com e-mail e senha](identity/login.md#como-usar)).

## Chave de assinatura dos tokens

Os dois hosts exigem a chave `Authentication:Jwt:SigningKey`, que assina e valida os access tokens JWT (HS256). Sem ela, ou com uma chave vazia ou menor que 32 bytes (256 bits) em UTF-8, falham ao subir com `OptionsValidationException` e uma mensagem que cita a chave. A chave nunca vai para o Git nem para o `appsettings.json`.

1. Gere uma chave aleatória. Por exemplo, com OpenSSL:

   ```powershell
   openssl rand -base64 48
   ```

   Ou, só com PowerShell:

   ```powershell
   $bytes = New-Object byte[] 48; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes); [Convert]::ToBase64String($bytes)
   ```

   Os dois geram 64 caracteres, acima do mínimo de 32 bytes.

2. Grave a chave nos user-secrets do projeto `Ouroboros.Api`. Como na connection string, o `Ouroboros.Backoffice.Api` usa o mesmo `UserSecretsId` e lê a mesma chave:

   ```powershell
   dotnet user-secrets set "Authentication:Jwt:SigningKey" "<a chave gerada>" --project src/Ouroboros.Api
   ```

Fora de desenvolvimento, a chave vem da variável de ambiente `Authentication__Jwt__SigningKey`, com o mesmo valor nos dois hosts. Trocar a chave invalida os tokens já emitidos. Emissor, audiência de cada host e tempos de vida ficam no `appsettings.json` de cada host; detalhes em [Autenticação JWT e autorização por políticas](architecture/authentication.md).

Os valores de `Authentication:Jwt` disponíveis são:

| Chave | Valor no checkout |
| --- | --- |
| Issuer | `ouroboros` nos dois hosts |
| Audience | `ouroboros-api` na API externa, `ouroboros-backoffice` no backoffice |
| AccessTokenLifetime | `00:15:00` |
| RefreshTokenLifetime | `7.00:00:00`, contados novamente a cada renovação |
| SigningKey | Externa ao Git, pelo menos 32 bytes UTF-8; Base64 é usado como texto, sem decodificação |

Emissor/audiência ausentes e durações não positivas também impedem a subida. O checkout valida HS256 com tolerância de relógio de 30 segundos e sem consulta à sessão a cada chamada; rotação assimétrica é uma entrega da #84, conforme o [estado de implementação](implementation-status.md).

## Executar os hosts

Com o banco e a chave de assinatura configurados, em dois terminais na raiz do repositório:

```powershell
dotnet run --project src/Ouroboros.Api              # API externa
dotnet run --project src/Ouroboros.Backoffice.Api   # backoffice
```

| Host | Público | Perfil `http` (padrão) | Perfil `https` |
| --- | --- | --- | --- |
| `Ouroboros.Api` | App do cliente, anônimos e integrações | `http://localhost:5141` | `https://localhost:7135` e `http://localhost:5141` |
| `Ouroboros.Backoffice.Api` | Backoffice (rede interna) | `http://localhost:5142` | `https://localhost:7136` e `http://localhost:5142` |

Os dois perfis usam o ambiente `Development`; para o `https`, acrescente `--launch-profile https`. Os user-secrets só são lidos no ambiente `Development`; com `--no-launch-profile`, o ambiente passa a ser `Production` e o host não encontra a connection string nem a chave de assinatura, a menos que você defina `ASPNETCORE_ENVIRONMENT=Development` ou as variáveis `ConnectionStrings__Ouroboros` e `Authentication__Jwt__SigningKey`.

Todo endpoint exige um access token válido da audiência do host (`Authorization: Bearer <token>`), salvo os marcados com `[AllowAnonymous]`; no backoffice, o token precisa ser de um `Admin`. Os únicos endpoints anônimos são os de login, `POST /api/v1/identity/sessions` na API externa e `POST /api/v1/admin/identity/sessions` no backoffice ([Fazer login com e-mail e senha](identity/login.md)), que devolvem o access token, e os de renovação, `POST /api/v1/identity/sessions/refresh` e `POST /api/v1/admin/identity/sessions/refresh` ([Renovar o access token com rotação do refresh token](identity/refresh-session.md)), que trocam o refresh token por um par novo, e os de logout, `POST /api/v1/identity/sessions/logout` e `POST /api/v1/admin/identity/sessions/logout` ([Encerrar a sessão atual](identity/logout.md)), que encerram a sessão do refresh token. Os de logout-all, `POST /api/v1/identity/sessions/logout-all` e `POST /api/v1/admin/identity/sessions/logout-all` ([Encerrar todas as sessões](identity/logout-all.md)), exigem access token e encerram todas as sessões do usuário do token. Os de consulta do próprio usuário, `GET /api/v1/identity/me` e `GET /api/v1/admin/identity/me` ([Consultar o próprio usuário](identity/me.md)), também exigem access token. Só na API externa, a solicitação e a conclusão do cadastro de cliente, `POST /api/v1/identity/registrations` e `POST /api/v1/identity/registrations/complete` ([Cadastrar cliente por e-mail](identity/registration.md)), e a solicitação e a conclusão da redefinição de senha, `POST /api/v1/identity/password-resets` e `POST /api/v1/identity/password-resets/complete` ([Redefinir a senha por e-mail](identity/password-reset.md)), também são anônimas. O login aceita 10 requisições por minuto de cada IP, a renovação e o logout, 30 cada, a solicitação de cadastro, 5, a conclusão do cadastro, 10, a solicitação de redefinição de senha, 5, e a conclusão da redefinição, 10; acima disso, respondem `429` com `Retry-After`. Os limites ficam em `Identity:RateLimiting:Policies` no `appsettings.json` de cada host, e valores ausentes ou não positivos impedem a subida ([Limitar requisições de autenticação por IP](identity/auth-rate-limiting.md)). Fora deles, uma requisição sem token responde 401 em qualquer rota, inclusive inexistente: a resposta não revela quais rotas existem. Com um token válido, a rota inexistente responde 404. As duas respostas saem em ProblemDetails ([Respostas de erro com ProblemDetails](architecture/error-responses.md)), e o 401 não explica por que o token foi recusado. Rotas de backoffice (`/api/v1/admin/...`) só existem no backoffice, e as demais, só na API externa; no deploy, o backoffice deve ficar só na rede interna ([Hosts da API externa e do backoffice](architecture/hosts.md)).

## E-mails em desenvolvimento

Não há servidor de e-mail. No ambiente `Development`, os dois hosts gravam cada e-mail enviado como um arquivo `.eml` em `.local/emails/`, na raiz do repositório (pasta ignorada pelo Git); abra o arquivo em qualquer cliente de e-mail. O log registra só o nome do arquivo, nunca o corpo nem tokens. Hoje, enviam e-mail a solicitação de cadastro de cliente, com o link para concluir o cadastro ([Cadastrar um cliente e entrar](#cadastrar-um-cliente-e-entrar)), e a solicitação de redefinição de senha, com o link para redefinir a senha ([Redefinir a senha por e-mail](identity/password-reset.md)); o convite de administradores vai usar o mesmo envio.

A configuração fica na seção `Identity:Email` dos `appsettings.Development.json`. O adaptador local só é aceito em `Development`: em outro ambiente, o host não sobe. Sem provedor configurado (o padrão fora de `Development`), o host sobe, mas qualquer envio falha com erro explícito. Detalhes em [Envio de e-mail por porta com adaptador local](architecture/email.md).

| Chave em Identity:Email | Configuração disponível |
| --- | --- |
| Provider | `PickupDirectory` em Development; `None` por padrão |
| PickupDirectory | `../../.local/emails`, resolvido a partir do content root do host; obrigatório para PickupDirectory |
| From | `nao-responda@ouroboros.local` por padrão |

Cada arquivo usa nome com instante/UUID, texto UTF-8 sem BOM, quebras CRLF, cabeçalhos MIME e assunto não ASCII codificado. O link é segredo: não copie seu conteúdo para logs ou issues. A solicitação responde 202 mesmo se o envio após commit falhar, preservando proteção contra enumeração; o checkout não recupera automaticamente a intenção perdida entre commit e envio.

## Cadastrar um cliente e entrar

Clientes se cadastram sozinhos pela API externa, em duas etapas: informam o e-mail, recebem um link de uso único e, com ele, definem a senha. Ainda não há front-end; em desenvolvimento, o link chega como `.eml` em `.local/emails/` e o token é copiado dele. Com o banco configurado, as migrations de Identity aplicadas, incluindo seus parâmetros locais, e a API externa rodando no ambiente `Development` (`dotnet run --project src/Ouroboros.Api`), na raiz do repositório:

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

4. Entre pelo login da API externa, que devolve o access token e o refresh token (a conclusão não faz login automático):

   ```powershell
   $login = @{ email = 'cliente@example.com'; password = '<senha>' } | ConvertTo-Json
   Invoke-RestMethod -Method Post -Uri http://localhost:5141/api/v1/identity/sessions `
     -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($login))
   ```

Detalhes, regras e erros em [Cadastrar cliente por e-mail](identity/registration.md) e [Fazer login com e-mail e senha](identity/login.md). Para trocar a senha esquecida pelo mesmo caminho (solicitar o link, copiar o token do `.eml` e concluir com a senha nova), veja o [fluxo completo da redefinição de senha](identity/password-reset.md#fluxo-completo-em-desenvolvimento).

## Redefinir a senha em desenvolvimento

Com a API externa no ar em `Development` (`http://localhost:5141` no perfil padrão) e uma conta de cliente criada. Ainda não há front-end; o token é copiado do link do e-mail.

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

Execute os hosts conforme este guia. Nos exemplos autenticados, substitua `<accessToken>` pelo token do login e `<refreshToken>` pelo refresh token vigente do mesmo público. Senhas e tokens dos exemplos são marcadores; não publique valores reais.

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
