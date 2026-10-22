# Persistência com PostgreSQL e EF Core

## Contexto

Cada módulo é dono dos próprios dados (veja [Estrutura dos módulos](module-structure.md#fronteiras-adotadas-para-a-evolução)). Antes da primeira entidade, era preciso definir como os módulos compartilham o PostgreSQL sem compartilhar tabelas, de onde vem a connection string e quando as migrations são aplicadas.

## Decisão

- **Um banco, um schema por módulo.** Todos os módulos usam o mesmo banco PostgreSQL; cada um grava apenas no próprio schema (`identity`, `customers`, `accounts`, `transactions`). Foreign keys, JOINs e SQL direto entre schemas de módulos diferentes continuam proibidos.
- **Um DbContext por módulo**, `internal sealed`, em `Infrastructure/Persistence/`, com `HasDefaultSchema("<schema>")`. O provider é o `Npgsql.EntityFrameworkCore.PostgreSQL`, referenciado pelo próprio módulo.
- **Histórico de migrations por módulo**, na tabela `<schema>.__EFMigrationsHistory` (configurada com `MigrationsHistoryTable`). As migrations de um módulo não aparecem no histórico dos outros, e o schema é criado junto com a tabela de histórico no primeiro `database update`.
- **Uma connection string, `ConnectionStrings:Ouroboros`.** Cada módulo a lê por meio de options `internal` validadas com `Validate` + `ValidateOnStart`: se ela estiver ausente, vazia ou em branco, a API não sobe e lança `OptionsValidationException` com uma mensagem que cita a chave e o user-secrets. O `dotnet ef` falha com a mesma mensagem.
  - Em desenvolvimento, a connection string fica nos user-secrets do projeto `Ouroboros.Api` (`UserSecretsId` no `.csproj`), lidos apenas no ambiente `Development`.
  - Fora de desenvolvimento, vem da variável de ambiente `ConnectionStrings__Ouroboros`.
- **Migrations aplicadas por comando, nunca na subida da API.** A ferramenta `dotnet-ef` é local, fixada em [`dotnet-tools.json`](../../dotnet-tools.json), e usa `Ouroboros.Api` como startup project (por isso a Api referencia `Microsoft.EntityFrameworkCore.Design` com `PrivateAssets="all"`).
- **Convenções das entidades.** Chave interna `Id` (`bigint` identity), identificador público `ExternalId` (UUIDv7), datas `CreatedAt`/`UpdatedAt` preenchidas por um interceptor e nomes em snake_case, conforme [Identificadores, datas e nomes das entidades](entity-conventions.md).
- **Banco de desenvolvimento em Docker Compose.** O [`compose.yaml`](../../compose.yaml) da raiz sobe apenas o PostgreSQL (`postgres:18-alpine`), com volume nomeado `postgres-data` e healthcheck. As credenciais (`POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD`; `POSTGRES_PORT` opcional, padrão 5432) são obrigatórias e vêm do arquivo `.env`, fora do Git; o [`.env.example`](../../.env.example) versionado serve de modelo.

As versões dos pacotes ficam em [Directory.Packages.props](../../Directory.Packages.props).

## Estado atual

Só o Identity tem persistência: o [`IdentityDbContext`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Persistence/IdentityDbContext.cs), registrado em [`AddIdentityModule`](../../src/Modules/Ouroboros.Modules.Identity/DependencyInjection.cs) com as options [`IdentityDatabaseOptions`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Persistence/IdentityDatabaseOptions.cs). O DbContext usa o [`AuditTimestampsInterceptor`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Persistence/AuditTimestampsInterceptor.cs) e aplica o [`SnakeCaseNaming`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Persistence/SnakeCaseNaming.cs) no `OnModelCreating`, e o módulo já tem a base `Entity` e a `EntityConfiguration<TEntity>` (veja [convenções das entidades](entity-conventions.md)). Ainda não há entidades; a migration inicial `InitialCreate` é vazia, e aplicá-la cria apenas o schema `identity` com sua tabela de histórico. Customers, Accounts e Transactions ainda não têm DbContext.

Como o Identity valida a connection string na subida, a API inteira passa a exigi-la. O passo a passo para subir o banco local e aplicar as migrations está no [README](../../README.md#banco-de-dados-local).

## Padrão para um módulo novo

```text
Ouroboros.Modules.<Module>/
└── Infrastructure/
    └── Persistence/
        ├── <Module>DbContext.cs             # internal sealed, HasDefaultSchema("<schema>"), SnakeCaseNaming por último
        ├── <Module>DatabaseOptions.cs       # internal, lê ConnectionStrings:Ouroboros
        ├── AuditTimestampsInterceptor.cs    # datas das entidades
        ├── EntityConfiguration.cs           # mapeamento comum das entidades
        ├── SnakeCaseNaming.cs               # nomes em snake_case
        └── Migrations/                      # geradas pelo dotnet ef
```

1. Referencie `Npgsql.EntityFrameworkCore.PostgreSQL` no `.csproj` do módulo.
2. Em `Add<Module>Module`, registre as options com `Validate` + `ValidateOnStart`, o `TimeProvider.System` com `TryAddSingleton`, o `AuditTimestampsInterceptor` como singleton e o DbContext com `UseNpgsql(..., npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", <schema>))` e `AddInterceptors(<interceptor resolvido da DI>)`, como no Identity.
3. No `OnModelCreating`, chame `HasDefaultSchema("<schema>")`, aplique as configurações das entidades e termine com `SnakeCaseNaming.Apply(modelBuilder)`; chamado antes, ele não converte o que for configurado depois. A base `Entity` e esses tipos de persistência hoje existem só no Identity: o primeiro módulo a precisar deles decide se os copia ou extrai (veja [convenções das entidades](entity-conventions.md#onde-fica)).
4. Gere a migration inicial e aplique-a, na raiz do repositório:

```powershell
dotnet ef migrations add InitialCreate --project src/Modules/Ouroboros.Modules.<Module> --startup-project src/Ouroboros.Api --context <Module>DbContext --output-dir Infrastructure/Persistence/Migrations
dotnet ef database update --project src/Modules/Ouroboros.Modules.<Module> --startup-project src/Ouroboros.Api --context <Module>DbContext
```

Cada módulo tem migrations e histórico próprios, então `--project` e `--context` são sempre obrigatórios.

### Migrations geradas

O `dotnet ef` gera as classes de migration como `public`. Elas são usadas pelo EF por reflexão e não expõem nada consumível por outro módulo, então são a única exceção à regra de visibilidade: o [teste de visibilidade](../../tests/Ouroboros.ArchitectureTests/ModuleVisibilityTests.cs) aceita tipos `public` que herdam `Migration`. Os arquivos em `**/Infrastructure/Persistence/Migrations/*.cs` são marcados `generated_code = true` no [`.editorconfig`](../../.editorconfig), então o `dotnet format` os ignora (eles são gerados com CRLF e BOM). Não edite migrations à mão sem necessidade.

## Alternativas descartadas

- **Aplicar migrations na subida da API** (`Database.Migrate()`): simples em desenvolvimento, mas faz várias instâncias competirem pela mesma migração, exige que o usuário da aplicação tenha permissão de DDL e esconde falhas de schema na inicialização. A Microsoft [recomenda](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying) aplicar migrations em produção por comando, script SQL ou bundle.
- **Um banco por módulo**: isolamento maior, mas mais infraestrutura para um projeto de estudos; schemas separados já tornam explícita a posse dos dados e permitem extrair um módulo depois.
- **Um único DbContext para todos os módulos**: exigiria que a Api ou um projeto comum enxergasse as entidades de todos os módulos, contrariando a visibilidade `internal`, e misturaria o histórico de migrations.
- **Uma connection string por módulo**: possível no futuro, se um módulo mudar de banco; hoje só adicionaria configuração repetida.

## Testes

Em [`tests/Ouroboros.ArchitectureTests`](../../tests/Ouroboros.ArchitectureTests/Ouroboros.ArchitectureTests.csproj), sem banco:

- [`OuroborosApiFactory`](../../tests/Ouroboros.ArchitectureTests/OuroborosApiFactory.cs): sobe o host no ambiente `Testing` com uma connection string fictícia como última fonte de configuração, acima de variáveis de ambiente e user-secrets, para que os testes de host não dependam da máquina. Esses testes não acessam o banco.
- [`DatabaseConfigurationTests`](../../tests/Ouroboros.ArchitectureTests/DatabaseConfigurationTests.cs): com connection string vazia ou em branco, a API não sobe e a mensagem cita `ConnectionStrings:Ouroboros`; com ela, sobe.

Em [`tests/Ouroboros.IntegrationTests`](../../tests/Ouroboros.IntegrationTests/Ouroboros.IntegrationTests.csproj), a API sobe contra um PostgreSQL real em container (Testcontainers), com as migrations de todos os DbContexts de módulo aplicadas uma vez num banco modelo; cada teste recebe um banco próprio clonado dele. Esses testes exigem Docker, mas não o banco do Docker Compose nem user-secrets. Funcionamento, comandos e como escrever um teste estão em [Testes de integração com PostgreSQL](integration-tests.md).

## Fora do escopo

Containerizar a API, Seq e um compose de produção.

## Origem

- [#5](https://github.com/adnerscarpelini/ouroboros-server/issues/5): decisão e implementação inicial no Identity.
- [#7](https://github.com/adnerscarpelini/ouroboros-server/issues/7): testes de integração contra PostgreSQL real.
- [#6](https://github.com/adnerscarpelini/ouroboros-server/issues/6): convenções de identificadores, datas e nomes das entidades.

[Voltar à arquitetura](README.md)
