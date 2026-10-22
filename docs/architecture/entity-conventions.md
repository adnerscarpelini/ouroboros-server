# Identificadores, datas e nomes das entidades

## Contexto

Antes da primeira entidade (o `User`, na [#8](https://github.com/adnerscarpelini/ouroboros-server/issues/8)), era preciso definir o tipo da chave, o identificador exposto fora do módulo, como as datas de criação e alteração são preenchidas e como tabelas e colunas são nomeadas no PostgreSQL. Sem uma convenção, cada entidade resolveria isso de um jeito, e a chave sequencial do banco acabaria em rotas e respostas, revelando volume de dados e permitindo enumerar registros.

## Decisão

- **Dois identificadores por entidade.** `Id` (`long`, `bigint generated always as identity`) é a chave interna, usada em chaves primárias, foreign keys e índices dentro do módulo, e nunca sai dele. `ExternalId` (`Guid`, `uuid`) é o identificador público: rotas, respostas HTTP (no campo `id`), claims e tipos de `Contracts` usam somente ele.
- **`ExternalId` é um UUIDv7 gerado no domínio**, com `Guid.CreateVersion7()`, na criação do objeto, antes de qualquer gravação. A coluna `external_id` é `uuid not null` com índice único. Por ser ordenado no tempo, o UUIDv7 fragmenta menos o índice que um UUIDv4.
- **Datas em `DateTimeOffset`, UTC, colunas `timestamptz`.** `CreatedAt` e `UpdatedAt` são preenchidos na gravação por um interceptor do EF Core, nunca pelo código de domínio:
  - entidade nova: as duas datas recebem o mesmo instante;
  - entidade alterada: só `UpdatedAt` muda, e `CreatedAt` nunca é regravado, mesmo que a propriedade seja marcada como alterada;
  - alterar só um owned type (value object), inclusive aninhado ou item de `OwnsMany`, também atualiza o `UpdatedAt` da entidade dona.
  - `UpdatedAt` não é token de concorrência.
- **O relógio vem do `TimeProvider`.** O módulo registra `TimeProvider.System` com `TryAddSingleton`, então um `TimeProvider` registrado antes (por exemplo, um `FakeTimeProvider` em testes) prevalece.
- **Nomes em snake_case.** Tabelas, colunas, chaves, foreign keys e índices do modelo do módulo ficam em snake_case (`ExternalId` → `external_id`). Owned types na tabela do dono herdam o nome e a chave do dono, e suas colunas levam o prefixo da propriedade (`HomeAddress.StreetName` → `home_address_street_name`). Nomes de schema não são convertidos: já são explícitos, como `identity`.
- **Value objects e owned types não seguem esta convenção**: não têm `Id`, `ExternalId` nem datas próprias.

### Onde fica

Tudo está no módulo Identity, único com persistência:

| Tipo | Papel |
| --- | --- |
| [`Domain/Entity`](../../src/Modules/Ouroboros.Modules.Identity/Domain/Entity.cs) | Base `internal abstract` com `Id`, `ExternalId`, `CreatedAt` e `UpdatedAt`, todos com `private set`. |
| [`EntityConfiguration<TEntity>`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Persistence/EntityConfiguration.cs) | Mapeamento comum: chave `Id` identity, `ExternalId` `uuid` com índice único e datas `timestamptz`; as subclasses implementam `ConfigureEntity`. |
| [`AuditTimestampsInterceptor`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Persistence/AuditTimestampsInterceptor.cs) | `SaveChangesInterceptor` que preenche as datas a partir do `TimeProvider`. |
| [`SnakeCaseNaming`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Persistence/SnakeCaseNaming.cs) | Converte os nomes do modelo do módulo; é a última chamada do `OnModelCreating`. |

A base `Entity` e esses tipos ficam no próprio módulo, e não num projeto comum, até que um segundo módulo justifique extraí-los. Quando Customers, Accounts ou Transactions ganharem persistência, a extração será decidida e registrada aqui.

## Como criar uma entidade nova

1. Crie a entidade em `Domain/`, `internal`, herdando `Entity`. Não atribua `Id`, `ExternalId`, `CreatedAt` nem `UpdatedAt`: o `ExternalId` nasce com o objeto e as datas são preenchidas ao salvar.
2. Crie a configuração em `Infrastructure/Persistence/`, `internal sealed`, herdando `EntityConfiguration<TEntity>` e mapeando só o que é próprio da entidade em `ConfigureEntity`. Não renomeie colunas à mão: o `SnakeCaseNaming` cuida disso.
3. Aplique a configuração no `OnModelCreating` do DbContext (por exemplo, `modelBuilder.ApplyConfiguration(new UserConfiguration())`), sempre antes de `SnakeCaseNaming.Apply(modelBuilder)`.
4. Gere a migration, conforme [Persistência](persistence.md#padrão-para-um-módulo-novo).
5. Na resposta HTTP, use um DTO cujo nome termine em `Response` e exponha o `ExternalId` como `id`, sem o `Id` interno. Rotas e buscas recebem o `ExternalId`.

```csharp
internal sealed class User : Entity
{
    public string Email { get; private set; } = string.Empty;
}

internal sealed class UserConfiguration : EntityConfiguration<User>
{
    protected override void ConfigureEntity(EntityTypeBuilder<User> builder) =>
        builder.Property(user => user.Email).HasMaxLength(254);
}

internal sealed record UserResponse(Guid Id, string Email);
```

O exemplo é ilustrativo; o `User` real será definido na [#8](https://github.com/adnerscarpelini/ouroboros-server/issues/8).

## Alternativas descartadas

- **UUIDv7 como única chave:** dispensaria o segundo identificador, mas deixaria chaves primárias, foreign keys e índices internos com 16 bytes em vez de 8.
- **Pacote `EFCore.NamingConventions` para o snake_case:** a convenção global renomeia também as colunas da tabela `__EFMigrationsHistory` (`MigrationId` → `migration_id`), o que quebra `dotnet ef database update` em bancos já criados. O `SnakeCaseNaming` age só sobre as entidades do modelo do módulo e deixa o histórico com `MigrationId` e `ProductVersion`.
- **Preencher as datas no domínio ou nos casos de uso:** cada caso de uso precisaria lembrar de atualizar `UpdatedAt`, e a regra para owned types se espalharia pelo código.

## Consequências

- Todo DbContext de módulo com entidades precisa registrar o `AuditTimestampsInterceptor` e chamar `SnakeCaseNaming.Apply` por último, conforme [Persistência](persistence.md#padrão-para-um-módulo-novo).
- Mapear uma entidade exige herdar `EntityConfiguration<TEntity>`; uma configuração que não herde dela fica sem o índice único e os tipos de coluna da convenção.
- O teste que protege a exposição do `Id` depende de nomes. Ele falha se uma propriedade `long` ou `long?` com nome terminado em `Id` aparecer num tipo de `Contracts` ou num tipo cujo nome termine em `Response`. Não detecta campos, propriedades terminadas em `ID` ou `Key`, nem DTOs com outro sufixo; por isso, DTOs de resposta devem terminar em `Response`.
- O módulo Identity concede `InternalsVisibleTo` a `Ouroboros.IntegrationTests` para que os testes usem a base e a configuração com uma entidade de teste; nunca à Api (veja [Visibilidade dos tipos](module-structure.md#visibilidade-dos-tipos)).

## Testes

Em [`tests/Ouroboros.ArchitectureTests`](../../tests/Ouroboros.ArchitectureTests/Ouroboros.ArchitectureTests.csproj):

- [`IdentifierExposureTests`](../../tests/Ouroboros.ArchitectureTests/IdentifierExposureTests.cs): para cada módulo, nenhuma propriedade `long`/`long?` terminada em `Id` em `Contracts` ou em tipos terminados em `Response`.

Em [`tests/Ouroboros.IntegrationTests`](../../tests/Ouroboros.IntegrationTests/Ouroboros.IntegrationTests.csproj) (exigem Docker, veja [Testes de integração](integration-tests.md)):

- [`EntityConventionsTests`](../../tests/Ouroboros.IntegrationTests/EntityConventionsTests.cs): contra o PostgreSQL real, com uma entidade de teste `Widget` que tem `OwnsOne` e `OwnsMany` e um `FakeTimeProvider`: datas iguais na criação, só `UpdatedAt` na alteração, `CreatedAt` protegido, owned atualizando o dono, `ExternalId` UUID versão 7 com índice único e tipos das colunas.
- [`IdentityPersistenceWiringTests`](../../tests/Ouroboros.IntegrationTests/IdentityPersistenceWiringTests.cs): o `IdentityDbContext` real usa o interceptor, o histórico de migrations mantém as colunas `MigrationId` e `ProductVersion`, e o modelo real está em snake_case.
- [`SnakeCaseNamingTests`](../../tests/Ouroboros.IntegrationTests/SnakeCaseNamingTests.cs): conversão de nomes (inclusive siglas) e um modelo com owned types e relacionamentos.

Na validação da #6, a suíte tinha 49 testes.

## Origem

- [#6](https://github.com/adnerscarpelini/ouroboros-server/issues/6): identificadores e datas das entidades.

[Voltar à arquitetura](README.md)
