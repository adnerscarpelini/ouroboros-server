<p align="center">
  <img src="logo.png" alt="Logo do Ouroboros Server" width="240" />
</p>

# Ouroboros Server

Projeto pessoal de estudos e portfólio em C#/.NET para uma fintech fictícia e educacional, sem movimentação de dinheiro real. A proposta inicial de e-commerce foi substituída por esse contexto. O projeto evolui continuamente, com foco em aprendizado, simplicidade e decisões explicáveis.

## Estado atual

Estrutura inicial de um monólito modular em .NET 10, com as primeiras entidades de domínio (o usuário e as sessões do Identity e os parâmetros do sistema do Settings) e os primeiros casos de uso: a criação do primeiro administrador por comando de linha, o login com e-mail e senha (o primeiro endpoint HTTP), a renovação e o encerramento das sessões, a consulta do próprio usuário e o cadastro de cliente por e-mail, com a solicitação e a conclusão que cria a conta, e a solicitação de redefinição de senha; assim, um cliente já consegue se cadastrar e entrar. Os demais casos de uso e modelos de domínio ainda serão definidos.

A solution contém onze projetos reais:

- `Ouroboros.Api`: API externa (app do cliente, endpoints anônimos e integrações); host ASP.NET Core que compõe os módulos e declara os controllers da API externa em `Controllers/<Module>/`; hoje, só os endpoints de sessão dos clientes (login, renovação do access token, logout e logout-all), a consulta do próprio usuário, a solicitação e a conclusão do cadastro de cliente e a solicitação de redefinição de senha.
- `Ouroboros.Backoffice.Api`: API interna de backoffice; host que compõe os módulos, declara os controllers do backoffice em `Controllers/<Module>/` (hoje, só os endpoints de sessão dos administradores, login, renovação do access token, logout e logout-all, e a consulta do próprio usuário) e roda comandos de operação em `CommandLine/`, como o `create-admin`.
- `Ouroboros.Hosting`: configuração HTTP comum aos dois hosts (controllers, versionamento, ProblemDetails, política padrão de autorização por público e o middleware de limite de requisições).
- `Ouroboros.BuildingBlocks`: código técnico compartilhado pelos módulos (a base `Entity` e os tipos de persistência comuns); não referencia módulos nem hosts.
- `Ouroboros.Modules.Identity`: identidade, com a entidade `User`, o hash e a política de senha, a criação do primeiro administrador, o login com sessões e refresh token, a renovação do access token com rotação do refresh token, o logout da sessão atual e de todas as sessões, a consulta do próprio usuário, o cadastro de cliente por e-mail (solicitação e conclusão), a solicitação de redefinição de senha, o bloqueio da conta após falhas seguidas de senha, o limite de requisições por IP da autenticação, a emissão e validação dos tokens JWT e a porta de envio de e-mail; lê parâmetros do Settings pelos `Contracts`.
- `Ouroboros.Modules.Settings`: parâmetros do sistema, lidos pelos outros módulos pelo contrato `ISystemParameters`, com validação e histórico de alterações.
- `Ouroboros.Modules.Customers`: estrutura para clientes.
- `Ouroboros.Modules.Accounts`: estrutura para contas.
- `Ouroboros.Modules.Transactions`: estrutura para transações.
- `Ouroboros.ArchitectureTests`: testes de arquitetura da visibilidade dos tipos dos módulos, da não exposição do `Id` interno das entidades, das dependências do `Ouroboros.BuildingBlocks`, da separação dos controllers entre os hosts, da descoberta, das rotas e do versionamento dos controllers e da autenticação e autorização.
- `Ouroboros.IntegrationTests`: testes de integração da API contra um PostgreSQL real em container.

Cada módulo é uma class library com as pastas `Application`, `Contracts`, `Domain` e `Infrastructure`, além de `DependencyInjection.cs`. Os módulos não declaram controllers: expõem os casos de uso em `Contracts`. Só o Identity e o Settings registram algo. O Identity registra a autenticação JWT, as políticas por capacidade (com os nomes em `Contracts`), o hash de senha, o envio de e-mail, o limite de requisições por IP, os casos de uso `ICreateFirstAdmin`, `ILogin`, `IRefreshSession`, `ILogout`, `ILogoutAll`, `IGetCurrentUser`, `IRequestRegistration`, `ICompleteRegistration` e `IRequestPasswordReset` e o `IdentityDbContext`, que usa o schema `identity` do PostgreSQL e tem as tabelas `users`, `sessions`, `refresh_tokens` e `email_tokens`. O Settings registra o `ISystemParameters` e o `SettingsDbContext`, que usa o schema `settings` e tem as tabelas `parameters` e `parameter_history` ([Parâmetros do sistema com histórico de alterações](docs/architecture/system-parameters.md)). A persistência segue a [nota de persistência](docs/architecture/persistence.md): um banco, um schema, um DbContext e um histórico de migrations por módulo. As entidades seguem as [convenções de identificadores, datas e nomes](docs/architecture/entity-conventions.md), com a base comum em `Ouroboros.BuildingBlocks` ([Código compartilhado entre módulos](docs/architecture/building-blocks.md)). O Identity tem domínio próprio e usa do ASP.NET Core Identity só o `PasswordHasher<T>`, com 210.000 iterações e rehash na verificação, conforme o [Domínio de identidade próprio e hash de senha](docs/architecture/identity.md).

Os dois hosts validam access tokens JWT (HS256), cada um só com a sua audiência, e negam por padrão: todo endpoint exige token, salvo os marcados com `[AllowAnonymous]`, e o backoffice exige também o papel `Admin` ([Autenticação JWT e autorização por políticas](docs/architecture/authentication.md)). Os tokens são emitidos pelo login de cada host ([Fazer login com e-mail e senha](docs/identity/login.md)), que devolve um access token de 15 minutos e um refresh token opaco de 7 dias; cinco falhas seguidas de senha bloqueiam a conta por 15 minutos ([Bloquear a conta após falhas consecutivas de senha](docs/identity/account-lockout.md)), e cada IP pode fazer 10 tentativas de login por minuto em cada host, acima disso recebendo `429` ([Limitar requisições de autenticação por IP](docs/identity/auth-rate-limiting.md)). Antes que o access token expire, o cliente troca o refresh token por um par novo na mesma sessão; o token usado é revogado, e cada IP pode fazer 30 renovações por minuto em cada host ([Renovar o access token com rotação do refresh token](docs/identity/refresh-session.md)). Reapresentar um refresh token já revogado encerra a sessão dele, sem afetar as outras ([Revogar a sessão ao detectar reuso de refresh token](docs/identity/refresh-token-reuse.md)). Para sair, o cliente encerra a sessão atual com o refresh token; o access token já emitido continua válido até expirar, e cada IP pode fazer 30 logouts por minuto em cada host ([Encerrar a sessão atual](docs/identity/logout.md)). Para encerrar todas as sessões do usuário, inclusive a atual, o cliente chama o logout-all com o access token; os access tokens já emitidos também continuam válidos até expirar, no máximo 15 minutos ([Encerrar todas as sessões](docs/identity/logout-all.md)). Para saber quem está autenticado, o cliente consulta a rota `me` com o access token e recebe só `id`, `email` e `role`, lidos do banco; usuário inativo ou sem o papel do host recebe `401` mesmo com token válido ([Consultar o próprio usuário](docs/identity/me.md)). Para se cadastrar, o cliente informa só o e-mail na API externa e recebe sempre a mesma resposta `202`; se o e-mail não tem conta, chega a ele um link de uso único, válido por 24 horas, para concluir o cadastro; cada IP pode fazer 5 solicitações por minuto. Para concluir, o cliente envia o token do link e a senha escolhida, que segue a mesma política do administrador; o sistema consome o token e cria a conta ativa com o papel `Customer`, responde `204` sem tokens e o cliente entra pelo login; cada IP pode fazer 10 conclusões por minuto ([Cadastrar cliente por e-mail](docs/identity/registration.md)). Quem esqueceu a senha informa o e-mail na API externa e recebe sempre a mesma resposta `202`; se o e-mail é de uma conta de cliente ativa, chega ao e-mail da conta um link de uso único, válido por 60 minutos, e a senha atual continua valendo; cada IP pode fazer 5 solicitações por minuto. A conclusão da redefinição ainda não existe ([Redefinir a senha por e-mail](docs/identity/password-reset.md)).

Fora o `User`, as sessões com seus refresh tokens, os tokens enviados por e-mail e os parâmetros do sistema, não há entidades; não há repositórios nem operações financeiras implementadas. Os casos de uso são o `ICreateFirstAdmin`, usado pelo comando `create-admin` ([Criar o primeiro administrador](#criar-o-primeiro-administrador)), o `ILogin`, o `IRefreshSession`, o `ILogout` e o `ILogoutAll`, usados pelos controllers de sessão dos dois hosts, o `IGetCurrentUser`, usado pelos controllers `me`, o `IRequestRegistration` e o `ICompleteRegistration`, usados pelo controller de cadastro da API externa, e o `IRequestPasswordReset`, usado pelo controller de redefinição de senha da API externa. Os endpoints HTTP são declarados em controllers `public` nos próprios hosts, organizados por módulo, chamando os casos de uso que os módulos expõem em `Contracts`, conforme [Controllers HTTP nos hosts](docs/architecture/http-controllers.md) (com o passo a passo para criar um endpoint), na API externa ou no backoffice conforme o público ([Hosts da API externa e do backoffice](docs/architecture/hosts.md)), com a versão na URL (`/api/v1/...`) segundo o [Versionamento da API HTTP](docs/architecture/api-versioning.md). Há testes de arquitetura e testes de integração contra um PostgreSQL real em container, incluindo os do caso de uso do primeiro administrador, os do login, da renovação do access token, do logout, do logout-all e da consulta do próprio usuário nos dois hosts, os da solicitação e da conclusão do cadastro, os da solicitação de redefinição de senha, os do bloqueio de conta e os do limite de requisições; não há workflow de CI nesta etapa.

## Requisitos

- .NET SDK 10.0.402, fixado em `global.json`.
- Visual Studio compatível com .NET 10 e SLNX, se for usar o Visual Studio.
- Docker com Docker Compose, para o PostgreSQL de desenvolvimento; os testes de integração também exigem o Docker em execução.

## Visual Studio e compilação

Abra `Ouroboros.slnx` por **Arquivo → Abrir → Projeto/Solução**. Abrir somente a pasta do repositório usa a visualização de arquivos. `src` e `Modules` são pastas organizadoras da solution; cada módulo é um projeto C# independente, compilável pelo menu de contexto **Compilar**.

Defina `Ouroboros.Api` e/ou `Ouroboros.Backoffice.Api` como projetos de inicialização para executar os hosts (para os dois ao mesmo tempo, use **Configurar Projetos de Inicialização → Vários projetos de inicialização**). As bibliotecas dos módulos não são executáveis independentes.

Na raiz do repositório:

```powershell
dotnet restore Ouroboros.slnx
dotnet build Ouroboros.slnx --no-restore
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
- Os testes de integração exigem o Docker em execução. Eles sobem o próprio PostgreSQL em container com Testcontainers e não usam o banco do Docker Compose, o `.env` nem os user-secrets. A primeira execução baixa a imagem `postgres:18-alpine`; as seguintes levam poucos segundos.

Como funcionam e como escrever um teste: [Testes de integração com PostgreSQL](docs/architecture/integration-tests.md).

## Banco de dados local

Os dois hosts exigem a connection string `ConnectionStrings:Ouroboros`: sem ela, falham ao subir com `OptionsValidationException` e uma mensagem que cita a chave. As migrations nunca são aplicadas na subida; rode-as por comando. Todos os comandos abaixo são executados na raiz do repositório.

1. Crie o `.env` a partir do modelo e troque a senha de `POSTGRES_PASSWORD` (o `.env` fica fora do Git):

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

4. Restaure a ferramenta local `dotnet-ef` (fixada em `dotnet-tools.json`) e aplique as migrations de cada módulo com persistência, o Identity e o Settings:

   ```powershell
   dotnet tool restore
   dotnet ef database update --project src/Modules/Ouroboros.Modules.Identity --startup-project src/Ouroboros.Api --context IdentityDbContext
   dotnet ef database update --project src/Modules/Ouroboros.Modules.Settings --startup-project src/Ouroboros.Api --context SettingsDbContext
   ```

   A migration do Settings inclui os valores iniciais dos parâmetros com a função `uuidv7()`, que exige PostgreSQL 18 (a versão do `compose.yaml`).

   No primeiro `database update` em um banco vazio, o log mostra `Failed executing DbCommand` sobre `__EFMigrationsHistory` antes de criar a tabela. É um comportamento conhecido do EF Core: o comando termina com `Done.`. Repetir o comando não altera nada.

5. Opcionalmente, confira os schemas criados (devem aparecer `identity`, com as tabelas `users`, `sessions`, `refresh_tokens` e `email_tokens`, e `settings`, com as tabelas `parameters` e `parameter_history`; ajuste usuário e banco se mudou o `.env`):

   ```powershell
   docker compose exec postgres psql -U ouroboros -d ouroboros -c "\dn"
   ```

Para gerar uma nova migration do Identity ou do Settings:

```powershell
dotnet ef migrations add <Nome> --project src/Modules/Ouroboros.Modules.Identity --startup-project src/Ouroboros.Api --context IdentityDbContext --output-dir Infrastructure/Persistence/Migrations
dotnet ef migrations add <Nome> --project src/Modules/Ouroboros.Modules.Settings --startup-project src/Ouroboros.Api --context SettingsDbContext --output-dir Infrastructure/Persistence/Migrations
```

`docker compose down` para o banco e mantém os dados no volume `postgres-data`; `docker compose down -v` também apaga o volume. Fora de desenvolvimento, a connection string vem da variável de ambiente `ConnectionStrings__Ouroboros`. Detalhes e o padrão para outros módulos estão em [Persistência com PostgreSQL e EF Core](docs/architecture/persistence.md).

## Criar o primeiro administrador

Administradores não se cadastram sozinhos. Com o banco configurado e as migrations do Identity e do Settings aplicadas, crie o primeiro `Admin` pelo comando `create-admin` do backoffice, que roda e encerra sem subir o servidor HTTP. A senha vai na variável de ambiente `OUROBOROS_ADMIN_PASSWORD`, nunca em argumento (argumentos ficam no histórico do shell e na lista de processos):

```powershell
$env:OUROBOROS_ADMIN_PASSWORD = '<senha>'
dotnet run --project src/Ouroboros.Backoffice.Api -- create-admin --email <e-mail>
Remove-Item Env:OUROBOROS_ADMIN_PASSWORD
```

No bash: `OUROBOROS_ADMIN_PASSWORD='<senha>' dotnet run --project src/Ouroboros.Backoffice.Api -- create-admin --email <e-mail>`.

- O e-mail precisa ser do domínio do parâmetro `Identity:AdminInvitations:AllowedEmailDomain` (valor inicial `adnerscarpelini.com`), sem subdomínios.
- A senha tem de 15 a 128 caracteres, sem regras de composição (frases com espaços e acentos valem), e não pode ser comum nem conter `ouroboros` ou a parte local do e-mail ([Política de senha](docs/architecture/password-policy.md)).
- Só funciona enquanto não houver nenhum `Admin`; os próximos entram por convite.
- Código de saída `0`: criado; `1`: recusado, com a mensagem da regra e `Nada foi criado.`; `2`: falta `--email` ou a variável da senha.

Detalhes em [Criar o primeiro administrador](docs/identity/first-admin.md). Com o `Admin` criado, entre pelo login do backoffice, `POST /api/v1/admin/identity/sessions` ([Fazer login com e-mail e senha](docs/identity/login.md#como-usar)).

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

Fora de desenvolvimento, a chave vem da variável de ambiente `Authentication__Jwt__SigningKey`, com o mesmo valor nos dois hosts. Trocar a chave invalida os tokens já emitidos. Emissor, audiência de cada host e tempos de vida ficam no `appsettings.json` de cada host; detalhes em [Autenticação JWT e autorização por políticas](docs/architecture/authentication.md).

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

Todo endpoint exige um access token válido da audiência do host (`Authorization: Bearer <token>`), salvo os marcados com `[AllowAnonymous]`; no backoffice, o token precisa ser de um `Admin`. Os únicos endpoints anônimos são os de login, `POST /api/v1/identity/sessions` na API externa e `POST /api/v1/admin/identity/sessions` no backoffice ([Fazer login com e-mail e senha](docs/identity/login.md)), que devolvem o access token, e os de renovação, `POST /api/v1/identity/sessions/refresh` e `POST /api/v1/admin/identity/sessions/refresh` ([Renovar o access token com rotação do refresh token](docs/identity/refresh-session.md)), que trocam o refresh token por um par novo, e os de logout, `POST /api/v1/identity/sessions/logout` e `POST /api/v1/admin/identity/sessions/logout` ([Encerrar a sessão atual](docs/identity/logout.md)), que encerram a sessão do refresh token. Os de logout-all, `POST /api/v1/identity/sessions/logout-all` e `POST /api/v1/admin/identity/sessions/logout-all` ([Encerrar todas as sessões](docs/identity/logout-all.md)), exigem access token e encerram todas as sessões do usuário do token. Os de consulta do próprio usuário, `GET /api/v1/identity/me` e `GET /api/v1/admin/identity/me` ([Consultar o próprio usuário](docs/identity/me.md)), também exigem access token. Só na API externa, a solicitação e a conclusão do cadastro de cliente, `POST /api/v1/identity/registrations` e `POST /api/v1/identity/registrations/complete` ([Cadastrar cliente por e-mail](docs/identity/registration.md)), e a solicitação de redefinição de senha, `POST /api/v1/identity/password-resets` ([Redefinir a senha por e-mail](docs/identity/password-reset.md)), também são anônimas. O login aceita 10 requisições por minuto de cada IP, a renovação e o logout, 30 cada, a solicitação de cadastro, 5, a conclusão do cadastro, 10, e a solicitação de redefinição de senha, 5; acima disso, respondem `429` com `Retry-After`. Os limites ficam em `Identity:RateLimiting:Policies` no `appsettings.json` de cada host, e valores ausentes ou não positivos impedem a subida ([Limitar requisições de autenticação por IP](docs/identity/auth-rate-limiting.md)). Fora deles, uma requisição sem token responde 401 em qualquer rota, inclusive inexistente: a resposta não revela quais rotas existem. Com um token válido, a rota inexistente responde 404. As duas respostas saem em ProblemDetails ([Respostas de erro com ProblemDetails](docs/architecture/error-responses.md)), e o 401 não explica por que o token foi recusado. Rotas de backoffice (`/api/v1/admin/...`) só existem no backoffice, e as demais, só na API externa; no deploy, o backoffice deve ficar só na rede interna ([Hosts da API externa e do backoffice](docs/architecture/hosts.md)).

## E-mails em desenvolvimento

Não há servidor de e-mail. No ambiente `Development`, os dois hosts gravam cada e-mail enviado como um arquivo `.eml` em `.local/emails/`, na raiz do repositório (pasta ignorada pelo Git); abra o arquivo em qualquer cliente de e-mail. O log registra só o nome do arquivo, nunca o corpo nem tokens. Hoje, enviam e-mail a solicitação de cadastro de cliente, com o link para concluir o cadastro ([Cadastrar um cliente e entrar](#cadastrar-um-cliente-e-entrar)), e a solicitação de redefinição de senha, com o link para redefinir a senha ([Redefinir a senha por e-mail](docs/identity/password-reset.md)); o convite de administradores vai usar o mesmo envio.

A configuração fica na seção `Identity:Email` dos `appsettings.Development.json`. O adaptador local só é aceito em `Development`: em outro ambiente, o host não sobe. Sem provedor configurado (o padrão fora de `Development`), o host sobe, mas qualquer envio falha com erro explícito. Detalhes em [Envio de e-mail por porta com adaptador local](docs/architecture/email.md).

## Cadastrar um cliente e entrar

Clientes se cadastram sozinhos pela API externa, em duas etapas: informam o e-mail, recebem um link de uso único e, com ele, definem a senha. Ainda não há front-end; em desenvolvimento, o link chega como `.eml` em `.local/emails/` e o token é copiado dele. Com o banco configurado, as migrations do Identity e do Settings aplicadas e a API externa rodando no ambiente `Development` (`dotnet run --project src/Ouroboros.Api`), na raiz do repositório:

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

3. Conclua o cadastro com o token e a senha. A senha segue a [política de senha](docs/architecture/password-policy.md) (15 a 128 caracteres, sem regras de composição, sem ser comum nem conter `ouroboros` ou a parte local do e-mail):

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

Detalhes, regras e erros em [Cadastrar cliente por e-mail](docs/identity/registration.md) e [Fazer login com e-mail e senha](docs/identity/login.md).

## Documentação

- [Índice](docs/README.md)
- [Arquitetura](docs/architecture/README.md)
- [Hosts da API externa e do backoffice](docs/architecture/hosts.md)
- [Persistência com PostgreSQL e EF Core](docs/architecture/persistence.md)
- [Identificadores, datas e nomes das entidades](docs/architecture/entity-conventions.md)
- [Código compartilhado entre módulos (BuildingBlocks)](docs/architecture/building-blocks.md)
- [Testes de integração com PostgreSQL](docs/architecture/integration-tests.md)
- [Domínio de identidade próprio e hash de senha](docs/architecture/identity.md)
- [Autenticação JWT e autorização por políticas](docs/architecture/authentication.md)
- [Envio de e-mail por porta com adaptador local](docs/architecture/email.md)
- [Parâmetros do sistema com histórico de alterações](docs/architecture/system-parameters.md)
- [Política de senha](docs/architecture/password-policy.md)
- [Criar o primeiro administrador](docs/identity/first-admin.md)
- [Fazer login com e-mail e senha](docs/identity/login.md)
- [Bloquear a conta após falhas consecutivas de senha](docs/identity/account-lockout.md)
- [Renovar o access token com rotação do refresh token](docs/identity/refresh-session.md)
- [Revogar a sessão ao detectar reuso de refresh token](docs/identity/refresh-token-reuse.md)
- [Encerrar a sessão atual (logout)](docs/identity/logout.md)
- [Encerrar todas as sessões (logout-all)](docs/identity/logout-all.md)
- [Consultar o próprio usuário (me)](docs/identity/me.md)
- [Cadastrar cliente por e-mail](docs/identity/registration.md)
- [Redefinir a senha por e-mail](docs/identity/password-reset.md)
- [Limitar requisições de autenticação por IP](docs/identity/auth-rate-limiting.md)
