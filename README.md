<p align="center">
  <img src="logo.png" alt="Logo do Ouroboros Server" width="240" />
</p>

# Ouroboros Server

Projeto pessoal de estudos e portfólio em C#/.NET para uma fintech fictícia e educacional, sem movimentação de dinheiro real. A proposta inicial de e-commerce foi substituída por esse contexto. O projeto evolui continuamente, com foco em aprendizado, simplicidade e decisões explicáveis.

## Estado atual

Estrutura inicial de um monólito modular em .NET 10, com a primeira entidade de domínio (o usuário do Identity). Os casos de uso e os demais modelos de domínio ainda serão definidos.

A solution contém nove projetos reais:

- `Ouroboros.Api`: API externa (app do cliente, endpoints anônimos e integrações); host ASP.NET Core que compõe os módulos e mapeia os controllers que não são de backoffice, ainda sem endpoints.
- `Ouroboros.Backoffice.Api`: API interna de backoffice; host que compõe os módulos e mapeia só os controllers de backoffice, ainda sem endpoints.
- `Ouroboros.Hosting`: configuração HTTP comum aos dois hosts (controllers por público, versionamento, ProblemDetails e política padrão de autorização).
- `Ouroboros.Modules.Identity`: identidade, com a entidade `User`, o hash de senha e a emissão e validação dos tokens JWT.
- `Ouroboros.Modules.Customers`: estrutura para clientes.
- `Ouroboros.Modules.Accounts`: estrutura para contas.
- `Ouroboros.Modules.Transactions`: estrutura para transações.
- `Ouroboros.ArchitectureTests`: testes de arquitetura da visibilidade dos tipos dos módulos, da não exposição do `Id` interno das entidades, da separação dos controllers entre os hosts, da descoberta, das rotas e do versionamento dos controllers e da autenticação e autorização.
- `Ouroboros.IntegrationTests`: testes de integração da API contra um PostgreSQL real em container.

Cada módulo é uma class library com as pastas `Application`, `Contracts`, `Domain` e `Infrastructure`, além de `DependencyInjection.cs`. Só o Identity registra algo: seus controllers (com a base dos controllers de backoffice, que exige o papel `Admin`), a autenticação JWT, o hash de senha e o `IdentityDbContext`, que usa o schema `identity` do PostgreSQL e tem a tabela `users`. A persistência segue a [nota de persistência](docs/architecture/persistence.md): um banco, um schema, um DbContext e um histórico de migrations por módulo. As entidades seguem as [convenções de identificadores, datas e nomes](docs/architecture/entity-conventions.md). O Identity tem domínio próprio e usa do ASP.NET Core Identity só o `PasswordHasher<T>`, com 210.000 iterações e rehash na verificação, conforme o [Domínio de identidade próprio e hash de senha](docs/architecture/identity.md).

Os dois hosts validam access tokens JWT (HS256), cada um só com a sua audiência, e negam por padrão: todo endpoint exige token, salvo os marcados com `[AllowAnonymous]`, e o backoffice exige também o papel `Admin` ([Autenticação JWT e autorização por políticas](docs/architecture/authentication.md)). Ainda não há login: o refresh token e os endpoints que emitem tokens chegam com a [#12](https://github.com/adnerscarpelini/ouroboros-server/issues/12).

Fora o `User`, não há entidades; não há DTOs, handlers, repositórios, login ou operações financeiras implementadas. Os endpoints HTTP serão declarados em controllers `internal` dos próprios módulos, conforme [Controllers HTTP nos módulos](docs/architecture/http-controllers.md), e servidos pela API externa ou pelo backoffice conforme o público ([Hosts da API externa e do backoffice](docs/architecture/hosts.md)), com a versão na URL (`/api/v1/...`) segundo o [Versionamento da API HTTP](docs/architecture/api-versioning.md). Há testes de arquitetura e testes de integração contra um PostgreSQL real em container; não há testes funcionais de casos de uso nem workflow de CI nesta etapa.

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

4. Restaure a ferramenta local `dotnet-ef` (fixada em `dotnet-tools.json`) e aplique as migrations do Identity:

   ```powershell
   dotnet tool restore
   dotnet ef database update --project src/Modules/Ouroboros.Modules.Identity --startup-project src/Ouroboros.Api --context IdentityDbContext
   ```

   No primeiro `database update` em um banco vazio, o log mostra `Failed executing DbCommand` sobre `__EFMigrationsHistory` antes de criar a tabela. É um comportamento conhecido do EF Core: o comando termina com `Done.`. Repetir o comando não altera nada.

5. Opcionalmente, confira os schemas criados (deve aparecer `identity`, que contém a tabela `users`; ajuste usuário e banco se mudou o `.env`):

   ```powershell
   docker compose exec postgres psql -U ouroboros -d ouroboros -c "\dn"
   ```

Para gerar uma nova migration do Identity:

```powershell
dotnet ef migrations add <Nome> --project src/Modules/Ouroboros.Modules.Identity --startup-project src/Ouroboros.Api --context IdentityDbContext --output-dir Infrastructure/Persistence/Migrations
```

`docker compose down` para o banco e mantém os dados no volume `postgres-data`; `docker compose down -v` também apaga o volume. Fora de desenvolvimento, a connection string vem da variável de ambiente `ConnectionStrings__Ouroboros`. Detalhes e o padrão para outros módulos estão em [Persistência com PostgreSQL e EF Core](docs/architecture/persistence.md).

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

Todo endpoint exige um access token válido da audiência do host (`Authorization: Bearer <token>`), salvo os marcados com `[AllowAnonymous]`; no backoffice, o token precisa ser de um `Admin`. Como ainda não há endpoints nem login, uma requisição sem token responde 401 em qualquer rota, inclusive inexistente: a resposta não revela quais rotas existem. Com um token válido, a rota inexistente responde 404. As duas respostas saem em ProblemDetails ([Respostas de erro com ProblemDetails](docs/architecture/error-responses.md)), e o 401 não explica por que o token foi recusado. Rotas de backoffice (`/api/v1/admin/...`) só existem no backoffice, e as demais, só na API externa; no deploy, o backoffice deve ficar só na rede interna ([Hosts da API externa e do backoffice](docs/architecture/hosts.md)).

## Documentação

- [Índice](docs/README.md)
- [Arquitetura](docs/architecture/README.md)
- [Hosts da API externa e do backoffice](docs/architecture/hosts.md)
- [Persistência com PostgreSQL e EF Core](docs/architecture/persistence.md)
- [Identificadores, datas e nomes das entidades](docs/architecture/entity-conventions.md)
- [Testes de integração com PostgreSQL](docs/architecture/integration-tests.md)
- [Domínio de identidade próprio e hash de senha](docs/architecture/identity.md)
- [Autenticação JWT e autorização por políticas](docs/architecture/authentication.md)
