<p align="center">
  <img src="logo.png" alt="Logo do Ouroboros Server" width="240" />
</p>

# Ouroboros Server

Projeto pessoal de estudos e portfólio em C#/.NET para uma fintech fictícia e educacional, sem movimentação de dinheiro real. A proposta inicial de e-commerce foi substituída por esse contexto. O projeto evolui continuamente, com foco em aprendizado, simplicidade e decisões explicáveis.

## Estado atual

Somente a estrutura inicial de um monólito modular em .NET 10. Os casos de uso e os modelos de domínio ainda serão definidos.

A solution contém seis projetos reais:

- `Ouroboros.Api`: host ASP.NET Core e composição dos módulos; mapeia os controllers dos módulos, ainda sem endpoints.
- `Ouroboros.Modules.Identity`: estrutura para identidade.
- `Ouroboros.Modules.Customers`: estrutura para clientes.
- `Ouroboros.Modules.Accounts`: estrutura para contas.
- `Ouroboros.Modules.Transactions`: estrutura para transações.
- `Ouroboros.ArchitectureTests`: testes de arquitetura da visibilidade dos tipos dos módulos e da descoberta, das rotas e do versionamento dos controllers.

Cada módulo é uma class library com as pastas `Application`, `Contracts`, `Domain` e `Infrastructure`, além de `DependencyInjection.cs`. Só o Identity registra algo: seus controllers (com a base dos controllers de backoffice) e o `IdentityDbContext`, que usa o schema `identity` do PostgreSQL e tem uma migration inicial vazia. A persistência segue a [nota de persistência](docs/architecture/persistence.md): um banco, um schema, um DbContext e um histórico de migrations por módulo.

Não há entidades, DTOs, handlers, repositórios, autenticação ou operações financeiras implementadas. Os endpoints HTTP serão declarados em controllers `internal` dos próprios módulos, conforme [Controllers HTTP nos módulos](docs/architecture/http-controllers.md), com a versão na URL (`/api/v1/...`) segundo o [Versionamento da API HTTP](docs/architecture/api-versioning.md). Há apenas testes de arquitetura; não há testes funcionais, testes contra o banco real nem workflow de CI nesta etapa.

## Requisitos

- .NET SDK 10.0.402, fixado em `global.json`.
- Visual Studio compatível com .NET 10 e SLNX, se for usar o Visual Studio.
- Docker com Docker Compose, para o PostgreSQL de desenvolvimento.

## Visual Studio e compilação

Abra `Ouroboros.slnx` por **Arquivo → Abrir → Projeto/Solução**. Abrir somente a pasta do repositório usa a visualização de arquivos. `src` e `Modules` são pastas organizadoras da solution; cada módulo é um projeto C# independente, compilável pelo menu de contexto **Compilar**.

Defina `Ouroboros.Api` como projeto de inicialização para executar o host. As bibliotecas dos módulos não são executáveis independentes.

Na raiz do repositório:

```powershell
dotnet restore Ouroboros.slnx
dotnet build Ouroboros.slnx --no-restore
dotnet build src/Modules/Ouroboros.Modules.Customers/Ouroboros.Modules.Customers.csproj
dotnet test --solution Ouroboros.slnx
```

Os testes não precisam do banco nem de connection string: sobem o host com uma connection string fictícia. Os arquivos `.gitkeep` preservam as pastas vazias no Git; os itens `Folder` nos projetos explicitam sua organização para o Visual Studio.

## Banco de dados local

A API exige a connection string `ConnectionStrings:Ouroboros`: sem ela, falha ao subir com `OptionsValidationException` e uma mensagem que cita a chave. As migrations nunca são aplicadas na subida; rode-as por comando. Todos os comandos abaixo são executados na raiz do repositório.

1. Crie o `.env` a partir do modelo e troque a senha de `POSTGRES_PASSWORD` (o `.env` fica fora do Git):

   ```powershell
   cp .env.example .env
   ```

2. Suba o PostgreSQL e aguarde o healthcheck:

   ```powershell
   docker compose up -d --wait
   ```

3. Grave a connection string nos user-secrets do projeto `Ouroboros.Api`, com os valores do `.env`:

   ```powershell
   dotnet user-secrets set "ConnectionStrings:Ouroboros" "Host=localhost;Port=5432;Database=ouroboros;Username=ouroboros;Password=<a senha do .env>" --project src/Ouroboros.Api
   ```

4. Restaure a ferramenta local `dotnet-ef` (fixada em `dotnet-tools.json`) e aplique as migrations do Identity:

   ```powershell
   dotnet tool restore
   dotnet ef database update --project src/Modules/Ouroboros.Modules.Identity --startup-project src/Ouroboros.Api --context IdentityDbContext
   ```

   No primeiro `database update` em um banco vazio, o log mostra `Failed executing DbCommand` sobre `__EFMigrationsHistory` antes de criar a tabela. É um comportamento conhecido do EF Core: o comando termina com `Done.`. Repetir o comando não altera nada.

5. Opcionalmente, confira os schemas criados (deve aparecer `identity`; ajuste usuário e banco se mudou o `.env`):

   ```powershell
   docker compose exec postgres psql -U ouroboros -d ouroboros -c "\dn"
   ```

Para gerar uma nova migration do Identity:

```powershell
dotnet ef migrations add <Nome> --project src/Modules/Ouroboros.Modules.Identity --startup-project src/Ouroboros.Api --context IdentityDbContext --output-dir Infrastructure/Persistence/Migrations
```

`docker compose down` para o banco e mantém os dados no volume `postgres-data`; `docker compose down -v` também apaga o volume. Fora de desenvolvimento, a connection string vem da variável de ambiente `ConnectionStrings__Ouroboros`. Detalhes e o padrão para outros módulos estão em [Persistência com PostgreSQL e EF Core](docs/architecture/persistence.md).

## Executar o host

Com o banco configurado:

```powershell
dotnet run --project src/Ouroboros.Api
```

O comando usa o launch profile `http`: ambiente `Development`, em `http://localhost:5141`. Os user-secrets só são lidos no ambiente `Development`; com `--no-launch-profile`, o ambiente passa a ser `Production` e a API não encontra a connection string, a menos que você defina `ASPNETCORE_ENVIRONMENT=Development` ou a variável `ConnectionStrings__Ouroboros`. Como ainda não há endpoints, requisições retornam 404.

## Documentação

- [Índice](docs/README.md)
- [Arquitetura](docs/architecture/README.md)
- [Persistência com PostgreSQL e EF Core](docs/architecture/persistence.md)
