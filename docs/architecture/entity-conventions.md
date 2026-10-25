# Identificadores, datas e nomes das entidades

## Contexto

Entidades seguem uma convenção única para chave interna, identificador público, datas e nomes no PostgreSQL. A chave sequencial fica dentro do serviço; rotas e respostas usam o identificador público.

## Decisão

- **Dois identificadores por entidade.** `Id` (`long`, `bigint generated always as identity`) é a chave interna, usada em chaves primárias, foreign keys e índices dentro do serviço, e nunca sai dele. `ExternalId` (`Guid`, `uuid`) é o identificador público: rotas, respostas HTTP (no campo `id`), claims e tipos de `Contracts` usam somente ele.
- **`ExternalId` é um UUIDv7 gerado no domínio**, com `Guid.CreateVersion7()`, na criação do objeto, antes de qualquer gravação. A coluna `external_id` é `uuid not null` com índice único. Por ser ordenado no tempo, o UUIDv7 fragmenta menos o índice que um UUIDv4.
- **Datas em `DateTimeOffset`, UTC, colunas `timestamptz`.** `CreatedAt` e `UpdatedAt` são preenchidos na gravação por um interceptor do EF Core, nunca pelo código de domínio:
  - entidade nova: as duas datas recebem o mesmo instante;
  - entidade alterada: só `UpdatedAt` muda, e `CreatedAt` nunca é regravado, mesmo que a propriedade seja marcada como alterada;
  - alterar só um owned type (value object), inclusive aninhado ou item de `OwnsMany`, também atualiza o `UpdatedAt` da entidade dona.
  - `UpdatedAt` não é token de concorrência.
- **O relógio vem do `TimeProvider`.** O serviço registra `TimeProvider.System` com `TryAddSingleton`, então um `TimeProvider` registrado antes (por exemplo, um `FakeTimeProvider` em testes) prevalece.
- **Nomes em snake_case.** Tabelas, colunas, chaves, foreign keys e índices do modelo do serviço ficam em snake_case (`ExternalId` → `external_id`). Owned types na tabela do dono herdam o nome e a chave do dono, e suas colunas levam o prefixo da propriedade (`HomeAddress.StreetName` → `home_address_street_name`). Nomes de schema não são convertidos: já são explícitos, como `identity`.
- **Value objects e owned types não seguem esta convenção**: não têm `Id`, `ExternalId` nem datas próprias.

### Onde fica

Os tipos técnicos reutilizados ficam em Ouroboros.BuildingBlocks. Cada serviço mantém suas entidades e mapeamentos próprios:

| Tipo | Papel |
| --- | --- |
| [`Domain/Entity`](../../src/Ouroboros.BuildingBlocks/Domain/Entity.cs) | Base `public abstract` com `Id`, `ExternalId`, `CreatedAt` e `UpdatedAt`, todos com `private set`. |
| [`Persistence/EntityConfiguration<TEntity>`](../../src/Ouroboros.BuildingBlocks/Persistence/EntityConfiguration.cs) | Mapeamento comum: chave `Id` identity, `ExternalId` `uuid` com índice único e datas `timestamptz`; as subclasses implementam `ConfigureEntity`. |
| [`Persistence/AuditTimestampsInterceptor`](../../src/Ouroboros.BuildingBlocks/Persistence/AuditTimestampsInterceptor.cs) | `SaveChangesInterceptor` que preenche as datas a partir do `TimeProvider`. |
| [`Persistence/SnakeCaseNaming`](../../src/Ouroboros.BuildingBlocks/Persistence/SnakeCaseNaming.cs) | Converte os nomes do modelo do serviço; é a última chamada do `OnModelCreating`. |

Os namespaces são `Ouroboros.BuildingBlocks.Domain` (só `Entity`, sem dependência do EF Core) e `Ouroboros.BuildingBlocks.Persistence`. As entidades concretas continuam `internal` em cada serviço.


## Como criar uma entidade nova

1. Crie a entidade em `Domain/`, `internal`, herdando `Entity` (`using Ouroboros.BuildingBlocks.Domain;`; o serviço referencia `Ouroboros.BuildingBlocks`). Não atribua `Id`, `ExternalId`, `CreatedAt` nem `UpdatedAt`: o `ExternalId` nasce com o objeto e as datas são preenchidas ao salvar.
2. Crie a configuração em `Infrastructure/Persistence/Configurations/`, `internal sealed`, herdando `EntityConfiguration<TEntity>` e mapeando só o que é próprio da entidade em `ConfigureEntity`. Não renomeie colunas à mão: o `SnakeCaseNaming` cuida disso.
3. Garanta que a configuração seja aplicada no `OnModelCreating` do DbContext antes de `SnakeCaseNaming.Apply(modelBuilder)`. O DbContext do proprietário chama `modelBuilder.ApplyConfigurationsFromAssembly(...)`, que encontra toda `IEntityTypeConfiguration<T>` do assembly do serviço; basta criar a classe. Um DbContext que aplique configurações uma a uma (`ApplyConfiguration(new ...())`) precisa listá-la.
4. Gere a migration do proprietário com os comandos do [guia de desenvolvimento](../development.md#banco-de-dados-local).
5. Na resposta HTTP, use um DTO cujo nome termine em `Response` e exponha o `ExternalId` como `id`, sem o `Id` interno. Rotas e buscas recebem o `ExternalId`.

O exemplo real é o `User` do Identity ([#8](https://github.com/adnerscarpelini/ouroboros-server/issues/8)): a entidade [`Domain/Users/User`](../../src/Services/Identity/Ouroboros.Identity/Domain/Users/User.cs), a configuração [`UserConfiguration`](../../src/Services/Identity/Ouroboros.Identity/Infrastructure/Persistence/Configurations/UserConfiguration.cs) e a migration `AddUsers`, descritas em [Domínio de identidade próprio e hash de senha](identity.md). Resumidamente:

```csharp
internal sealed class UserConfiguration : EntityConfiguration<User>
{
    protected override void ConfigureEntity(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.Property(user => user.Email).HasMaxLength(User.EmailMaxLength);
        builder.HasIndex(user => user.Email).IsUnique();
        builder.Property(user => user.Role).HasConversion<string>().HasMaxLength(20);
        // ...
    }
}
```

A [consulta do próprio usuário](../identity/me.md) aplica o passo 5: `MeResponse(Guid Id, string Email, string Role)` expõe o `ExternalId` no campo `id`, sem a chave interna.

## Alternativas descartadas

- **UUIDv7 como única chave:** dispensaria o segundo identificador, mas deixaria chaves primárias, foreign keys e índices internos com 16 bytes em vez de 8.
- **Pacote `EFCore.NamingConventions` para o snake_case:** a convenção global renomeia também as colunas da tabela `__EFMigrationsHistory` (`MigrationId` → `migration_id`), o que quebra `dotnet ef database update` em bancos já criados. O `SnakeCaseNaming` age só sobre as entidades do modelo do serviço e deixa o histórico com `MigrationId` e `ProductVersion`.
- **Preencher as datas no domínio ou nos casos de uso:** cada caso de uso precisaria lembrar de atualizar `UpdatedAt`, e a regra para owned types se espalharia pelo código.

## Consequências

- Todo DbContext do proprietário com entidades precisa registrar o `AuditTimestampsInterceptor` e chamar `SnakeCaseNaming.Apply` por último, conforme [Persistência](persistence.md).
- Mapear uma entidade exige herdar `EntityConfiguration<TEntity>`; uma configuração que não herde dela fica sem o índice único e os tipos de coluna da convenção.
- O teste que protege a exposição do `Id` depende de nomes. Ele falha se uma propriedade `long` ou `long?` com nome terminado em `Id` aparecer num tipo de `Contracts` ou num tipo cujo nome termine em `Response`. Não detecta campos, propriedades terminadas em `ID` ou `Key`, nem DTOs com outro sufixo; por isso, DTOs de resposta devem terminar em `Response`.
- Os testes usam a base e a configuração, que são `public` em BuildingBlocks, com uma entidade de teste. Para usar as entidades e os DbContexts reais, que são internos, as bibliotecas dos serviços concedem `InternalsVisibleTo` a `Ouroboros.IntegrationTests`, nunca aos hosts (veja [Visibilidade dos tipos](module-structure.md#visibilidade-dos-tipos)).

## Testes

Em [`tests/Ouroboros.ArchitectureTests`](../../tests/Ouroboros.ArchitectureTests/Ouroboros.ArchitectureTests.csproj):

- [`IdentifierExposureTests`](../../tests/Ouroboros.ArchitectureTests/IdentifierExposureTests.cs): para cada serviço, nenhuma propriedade `long`/`long?` terminada em `Id` em `Contracts` ou em tipos terminados em `Response`.

Em [`tests/Ouroboros.IntegrationTests`](../../tests/Ouroboros.IntegrationTests/Ouroboros.IntegrationTests.csproj) (exigem Docker, veja [Testes de integração](integration-tests.md)):

- [`EntityConventionsTests`](../../tests/Ouroboros.IntegrationTests/EntityConventionsTests.cs): contra o PostgreSQL real, com uma entidade de teste `Widget` que tem `OwnsOne` e `OwnsMany` e um `FakeTimeProvider`: datas iguais na criação, só `UpdatedAt` na alteração, `CreatedAt` protegido, owned atualizando o dono, `ExternalId` UUID versão 7 com índice único e tipos das colunas.
- [`IdentityPersistenceWiringTests`](../../tests/Ouroboros.IntegrationTests/IdentityPersistenceWiringTests.cs): o `IdentityDbContext` real usa o interceptor, o histórico de migrations mantém as colunas `MigrationId` e `ProductVersion`, e o modelo real está em snake_case.
- [`SnakeCaseNamingTests`](../../tests/Ouroboros.IntegrationTests/SnakeCaseNamingTests.cs): conversão de nomes (inclusive siglas) e um modelo com owned types e relacionamentos.

## Issues relacionadas

- [#6](https://github.com/adnerscarpelini/ouroboros-server/issues/6): identificadores e datas das entidades.
- [#8](https://github.com/adnerscarpelini/ouroboros-server/issues/8): entidade `User` e aplicação das configurações por `ApplyConfigurationsFromAssembly`.
- [#33](https://github.com/adnerscarpelini/ouroboros-server/issues/33): extração dos tipos para `Ouroboros.BuildingBlocks`.

[Voltar à arquitetura](README.md)
