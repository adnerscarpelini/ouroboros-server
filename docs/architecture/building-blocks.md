# Código compartilhado entre módulos (BuildingBlocks)

## Contexto

As [convenções das entidades](entity-conventions.md) criaram, no Identity, a base `Entity` e os tipos de persistência que a acompanham (`EntityConfiguration<TEntity>`, `AuditTimestampsInterceptor` e `SnakeCaseNaming`). A regra era mantê-los no módulo até que um segundo módulo precisasse deles e, então, decidir entre copiá-los ou extraí-los.

O módulo Settings ([#33](https://github.com/adnerscarpelini/ouroboros-server/issues/33), [Parâmetros do sistema](system-parameters.md)) foi o segundo módulo com entidades e precisou dos mesmos tipos.

## Decisão

Os quatro tipos foram movidos do Identity para um projeto novo, [`src/Ouroboros.BuildingBlocks`](../../src/Ouroboros.BuildingBlocks/Ouroboros.BuildingBlocks.csproj):

| Tipo | Namespace | Papel |
| --- | --- | --- |
| [`Entity`](../../src/Ouroboros.BuildingBlocks/Domain/Entity.cs) | `Ouroboros.BuildingBlocks.Domain` | Base `public abstract` com `Id`, `ExternalId`, `CreatedAt` e `UpdatedAt`, todos com `private set`. |
| [`EntityConfiguration<TEntity>`](../../src/Ouroboros.BuildingBlocks/Persistence/EntityConfiguration.cs) | `Ouroboros.BuildingBlocks.Persistence` | Mapeamento comum das entidades. |
| [`AuditTimestampsInterceptor`](../../src/Ouroboros.BuildingBlocks/Persistence/AuditTimestampsInterceptor.cs) | `Ouroboros.BuildingBlocks.Persistence` | Preenche `CreatedAt` e `UpdatedAt` na gravação. |
| [`SnakeCaseNaming`](../../src/Ouroboros.BuildingBlocks/Persistence/SnakeCaseNaming.cs) | `Ouroboros.BuildingBlocks.Persistence` | Converte os nomes do modelo para snake_case. |

O comportamento de cada tipo não mudou e continua descrito em [Identificadores, datas e nomes das entidades](entity-conventions.md).

### Regras

- **Só código técnico transversal.** BuildingBlocks guarda a infraestrutura comum das entidades, não regras de negócio, entidades concretas, DbContexts nem contratos de módulos. Dados e regras de um módulo continuam nele.
- **Dependências em um só sentido.** Os módulos referenciam BuildingBlocks; BuildingBlocks não referencia nenhum projeto `Ouroboros.*` (módulos, hosts ou `Ouroboros.Hosting`). Assim ele não vira um atalho entre módulos.
- **O namespace `Domain` não depende do EF Core.** `Entity` não usa tipos de `Microsoft.EntityFrameworkCore`; o EF fica no namespace `Persistence`. O projeto depende só do `Npgsql.EntityFrameworkCore.PostgreSQL` (e, por ele, do EF Core).
- **Tipos `public`.** Como é uma biblioteca compartilhada e não um módulo, seus tipos são `public`; a regra de visibilidade `internal` dos [módulos](module-structure.md#visibilidade-dos-tipos) não se aplica a ele, e o teste de visibilidade só inspeciona os assemblies `Ouroboros.Modules.*`. As entidades concretas que herdam `Entity` continuam `internal` nos módulos.
- **Interceptor registrado uma vez.** Cada módulo com persistência registra o `AuditTimestampsInterceptor` com `TryAddSingleton` (e o `TimeProvider.System` também com `TryAddSingleton`), então o primeiro registro vale para todos e a ordem de `Add<Module>Module` nos hosts não importa.

## Alternativas descartadas

- **Copiar os tipos para o Settings:** cada correção (por exemplo, no preenchimento das datas ou na conversão de nomes) precisaria ser repetida em cada módulo, e as convenções acabariam divergindo.
- **Colocar os tipos em `Contracts` do Identity e fazer o Settings referenciá-lo:** criaria uma dependência entre módulos só por infraestrutura e exporia tipos técnicos na fronteira pública do Identity.
- **Usar `Ouroboros.Hosting`:** esse projeto é a configuração HTTP dos hosts; os módulos teriam de referenciar ASP.NET Core de host para usar a base de entidade.

## Consequências

- Um módulo novo com persistência referencia `Ouroboros.BuildingBlocks` no `.csproj` em vez de criar os próprios tipos ([Persistência](persistence.md#padrão-para-um-módulo-novo)).
- Os testes usam os tipos de BuildingBlocks diretamente; o `InternalsVisibleTo` dos módulos só é necessário para os tipos internos de cada módulo (entidades, DbContexts, casos de uso).
- Mudar um tipo de BuildingBlocks afeta todos os módulos de uma vez; a mudança deve ser compatível com as migrations já geradas.
- Novo código só entra em BuildingBlocks quando for técnico e necessário em mais de um módulo; a decisão deve ser registrada nesta nota.

## Testes

- [`BuildingBlocksTests`](../../tests/Ouroboros.ArchitectureTests/BuildingBlocksTests.cs) (arquitetura): BuildingBlocks não referencia nenhum assembly `Ouroboros.*`, e os tipos do namespace `Ouroboros.BuildingBlocks.Domain` não dependem de tipos do EF Core.
- Os testes das convenções (`EntityConventionsTests`, `IdentityPersistenceWiringTests` e `SnakeCaseNamingTests`) passaram a usar os tipos de BuildingBlocks; estão descritos em [Identificadores, datas e nomes das entidades](entity-conventions.md#testes).

## Origem

- [#6](https://github.com/adnerscarpelini/ouroboros-server/issues/6): criação dos tipos no Identity.
- [#33](https://github.com/adnerscarpelini/ouroboros-server/issues/33): extração para BuildingBlocks, motivada pelo módulo Settings.

[Voltar à arquitetura](README.md)
