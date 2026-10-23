# Testes de integração com PostgreSQL

## Contexto

Os [testes de arquitetura](http-controllers.md#testes-de-arquitetura) sobem o host com uma connection string fictícia e nunca acessam o banco. Faltava provar que a API sobe contra um PostgreSQL real, com as migrations de cada módulo aplicadas, e dar aos testes futuros um banco isolado para cada teste, sem depender do banco de desenvolvimento nem da configuração da máquina.

## Decisão

O projeto [`tests/Ouroboros.IntegrationTests`](../../tests/Ouroboros.IntegrationTests/Ouroboros.IntegrationTests.csproj) (xUnit v3, `Microsoft.AspNetCore.Mvc.Testing` e [Testcontainers](https://dotnet.testcontainers.org/) para PostgreSQL) testa a API contra um PostgreSQL de verdade, em container:

- **Um container por execução da suíte.** A [`PostgresFixture`](../../tests/Ouroboros.IntegrationTests/PostgresFixture.cs) é uma assembly fixture: sobe um container `postgres:18-alpine` (a mesma imagem do [`compose.yaml`](../../compose.yaml)) no início e o remove no fim. Ela não usa o banco do Docker Compose; os testes passam com ele parado.
- **Migrations aplicadas uma vez, num banco modelo.** A fixture cria o banco `ouroboros_template`, sobe a API contra ele e aplica as migrations de todos os DbContexts de módulo.
- **Um banco por teste, clonado do modelo.** Antes de cada teste, `CREATE DATABASE ... TEMPLATE ouroboros_template` cria um banco com nome único, já migrado; ao final, `DROP DATABASE ... WITH (FORCE)` o remove. As criações são serializadas, porque o PostgreSQL recusa clonar um modelo que esteja em uso.
- **DbContexts descobertos automaticamente.** [`ModuleDbContexts`](../../tests/Ouroboros.IntegrationTests/ModuleDbContexts.cs) encontra por reflexão os DbContexts `internal` dos assemblies `Ouroboros.Modules.*` referenciados pela Api e os resolve pela DI. A aplicação das migrations não precisa de `InternalsVisibleTo`, e o DbContext de um módulo novo é migrado sem alterar os testes. Testes que usam tipos internos de um módulo exigem que ele conceda `InternalsVisibleTo` a este projeto, como fazem o Identity e o Settings; os tipos de [BuildingBlocks](building-blocks.md) são `public` e não precisam disso.
- **Configuração independente da máquina.** A [`IntegrationApiFactory`](../../tests/Ouroboros.IntegrationTests/IntegrationApiFactory.cs) sobe a API no ambiente `Testing` com a connection string do banco do teste e uma `SigningKey` fictícia como última fonte de configuração, acima de user-secrets e variáveis de ambiente como `ConnectionStrings__Ouroboros`. Ela também registra o assembly de testes como application part, para que controllers declarados nos testes sejam mapeados. Desde a #14, eleva o limite do login (`Identity:RateLimiting:Policies:identity-login:PermitLimit`) para 100.000, e, desde a #15 e a #17, também os da renovação (`identity-refresh`) e do logout (`identity-logout`): no `TestServer` todas as requisições chegam sem IP e dividem o mesmo contador, que os outros testes atingiriam ([Limitar requisições](../identity/auth-rate-limiting.md#como-verificar)).
- **Backoffice com a mesma configuração.** Desde a #12, a [`BackofficeIntegrationApiFactory`](../../tests/Ouroboros.IntegrationTests/BackofficeIntegrationApiFactory.cs) sobe o host de backoffice contra o mesmo banco do teste, reaproveitando a configuração da `IntegrationApiFactory` (`IntegrationApiFactory.Configure`). Ela não faz parte da base `IntegrationTest`: o teste a cria com `ConnectionString` quando precisa do backoffice.

## Pré-requisitos

Docker em execução, o mesmo usado pelo [banco local](../../README.md#banco-de-dados-local). Não é preciso `.env`, user-secrets nem `docker compose up`. Os testes de arquitetura não usam Docker.

A primeira execução baixa a imagem `postgres:18-alpine`; as seguintes levam poucos segundos. O comportamento sem Docker disponível não foi verificado.

## Como executar

Na raiz do repositório:

```powershell
dotnet test --solution Ouroboros.slnx                     # toda a suíte: arquitetura e integração
dotnet test --project tests/Ouroboros.IntegrationTests    # só integração (exige Docker)
dotnet test --project tests/Ouroboros.ArchitectureTests   # só arquitetura (sem Docker)
```

Na validação da [#7](https://github.com/adnerscarpelini/ouroboros-server/issues/7), a suíte tinha 23 testes: 18 de arquitetura e 5 de integração. Ao final, nenhum banco de teste fica no container, e o container é removido; o container auxiliar do Testcontainers (Ryuk) desaparece alguns segundos depois.

## Como escrever um teste

Herde de [`IntegrationTest`](../../tests/Ouroboros.IntegrationTests/IntegrationTest.cs), recebendo a `PostgresFixture` no construtor primário. Cada teste ganha um banco próprio e uma instância própria da API, e dispõe de:

| Membro | Uso |
| --- | --- |
| `Api` | Fábrica da API do teste; `Api.Services` resolve serviços, como DbContexts e casos de uso. |
| `Client` | `HttpClient` da API apontada para o banco do teste. |
| `ConnectionString` | Connection string do banco do teste. |
| `QueryScalarAsync<T>(sql)` | Executa uma consulta e devolve o primeiro valor. |
| `ExecuteAsync(sql)` | Executa um comando SQL sem retorno. |

```csharp
public sealed class ExampleTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Endpoint_responds()
    {
        var response = await Client.GetAsync("/api/v1/<rota>", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
    }
}
```

O estado gravado por um teste nunca é visto por outro, independentemente da ordem de execução, então não é preciso limpar dados.

Para testar persistência sem endpoint, um teste pode montar um DbContext próprio sobre `ConnectionString`, criando as tabelas com `IRelationalDatabaseCreator.CreateTablesAsync` num schema do teste, como faz o `EntityConventionsTests`. Para controlar as datas das entidades, use o `FakeTimeProvider` (pacote `Microsoft.Extensions.TimeProvider.Testing`) no `AuditTimestampsInterceptor` (`Ouroboros.BuildingBlocks.Persistence`).

Para testar um caso de uso ou um DbContext real sem endpoint, resolva-o de um escopo da API do teste (`Api.Services.CreateAsyncScope()`), como fazem os `SystemParametersTests`.

## Testes existentes

O [`MigrationsProbeController`](../../tests/Ouroboros.IntegrationTests/MigrationsProbeController.cs), que existe só no assembly de testes, expõe `GET /api/v1/integration-probe/migrations` com as migrations aplicadas de cada DbContext de módulo. Os [`DatabaseIntegrationTests`](../../tests/Ouroboros.IntegrationTests/DatabaseIntegrationTests.cs) verificam que:

- a API responde por essa rota com a migration `InitialCreate` do Identity aplicada;
- o schema `identity` tem a tabela `__EFMigrationsHistory`;
- cada teste tem banco próprio: três casos criam a mesma tabela, o que falharia num banco compartilhado.

Os [`ModuleDataBoundaryTests`](../../tests/Ouroboros.IntegrationTests/ModuleDataBoundaryTests.cs) verificam que cada DbContext de módulo define schema padrão e só mapeia tabelas nele, e que os DbContexts do Identity e do Settings são descobertos.

Os testes das convenções das entidades (`EntityConventionsTests`, `IdentityPersistenceWiringTests` e `SnakeCaseNamingTests`) estão descritos em [Identificadores, datas e nomes das entidades](entity-conventions.md#testes); os do hash de senha e da persistência do usuário (`PasswordHashingTests` e `UserPersistenceTests`), em [Domínio de identidade próprio e hash de senha](identity.md#testes); os dos parâmetros do sistema (`SystemParametersTests` e `ParameterRulesTests`), em [Parâmetros do sistema](system-parameters.md#testes); os do primeiro administrador (`CreateFirstAdminTests`), em [Criar o primeiro administrador](../identity/first-admin.md#como-verificar); os do login (`LoginTests`), os primeiros a exercitar endpoints reais dos dois hosts, em [Fazer login com e-mail e senha](../identity/login.md#como-verificar); os do bloqueio de conta (`AccountLockoutTests`), em [Bloquear a conta](../identity/account-lockout.md#como-verificar); os do limite de requisições (`RateLimitingTests`), em [Limitar requisições](../identity/auth-rate-limiting.md#como-verificar); os da renovação do access token (`RefreshSessionTests`), em [Renovar o access token](../identity/refresh-session.md#como-verificar); os da detecção de reuso de refresh token (`RefreshTokenReuseTests`), em [Revogar a sessão ao detectar reuso](../identity/refresh-token-reuse.md#como-verificar); os do logout (`LogoutTests`), em [Encerrar a sessão atual](../identity/logout.md#como-verificar); os do logout-all (`LogoutAllTests`), os primeiros a chamar um endpoint autenticado com o access token emitido pelo login, em [Encerrar todas as sessões](../identity/logout-all.md#como-verificar); e os da consulta do próprio usuário (`MeTests`), que alteram o usuário no banco com SQL direto depois do login, em [Consultar o próprio usuário](../identity/me.md#como-verificar).

Para verificar que segredos não vão para os logs, o [`ListLoggerProvider`](../../tests/Ouroboros.IntegrationTests/ListLoggerProvider.cs) guarda o texto formatado de cada log (com a exceção, se houver); os testes o registram com `Api.WithWebHostBuilder` e `ConfigureLogging`, como fazem o `CreateFirstAdminTests`, o `LoginTests` (este com o nível mínimo em `Trace`), o `AccountLockoutTests`, o `RateLimitingTests` e o `RefreshTokenReuseTests`.

Para controlar o tempo ou trocar uma dependência do host num teste, sobrescreva o serviço com `Api.WithWebHostBuilder` e `ConfigureTestServices`, como faz o `AccountLockoutTests`, que registra um `FakeTimeProvider` como `TimeProvider` (para avançar o fim do bloqueio) e um `IPasswordHasher` de poucas iterações; o `RefreshSessionTests`, o `RefreshTokenReuseTests` e o `LogoutTests` fazem o mesmo para expirar refresh tokens.

Para simular uma falha do banco no meio de um caso de uso, o `RefreshSessionTests` cria, com SQL direto no banco do teste, um trigger que lança exceção no `INSERT` em `identity.refresh_tokens`, verifica que nada foi gravado e remove o trigger em seguida. Como cada teste tem banco próprio, o trigger não afeta outros testes.

Para testar uma corrida com uma operação em andamento, o `RefreshTokenReuseTests` abre uma `NpgsqlConnection` própria sobre `ConnectionString`, executa nela, numa transação ainda não confirmada, os mesmos passos e locks do caso de uso (travar a sessão, revogar e inserir um refresh token) e dispara a requisição concorrente. Antes de confirmar, espera a requisição ficar bloqueada, consultando `pg_stat_activity` até aparecer uma sessão com `wait_event_type = 'Lock'`; assim o teste não depende de atrasos fixos.

Para simular o IP do cliente, que o `TestServer` não preenche, o `RateLimitingTests` registra com `ConfigureTestServices` um `IStartupFilter` que copia o header `X-Test-Client-IP` para `HttpContext.Connection.RemoteIpAddress` no início do pipeline. O filtro existe só nesses testes; a aplicação não lê o header. Para sobrescrever configuração num teste, como limites próprios, use `ConfigureAppConfiguration` com `AddInMemoryCollection` no `Api.WithWebHostBuilder`.

## Alternativas descartadas

- **Usar o banco do Docker Compose:** exigiria `.env`, user-secrets e o banco no ar, e os testes disputariam dados com o desenvolvimento.
- **Limpar as tabelas entre testes** (por exemplo, com Respawn): acrescenta uma dependência e compartilha o banco entre testes, o que dificulta a execução paralela e deixa passar estado que a limpeza não cubra (sequências, schemas, objetos criados pelo teste).
- **Reaplicar as migrations em cada teste:** isolamento igual ao do clone, mas o custo cresce com o número de migrations e de testes.

O clone do banco modelo dá isolamento total sem dependência extra e é rápido, porque as migrations rodam uma vez.

## Consequências

- `NpgsqlConnection.ClearAllPools()` é chamado ao fim de cada teste para liberar o banco antes do `DROP`; ele afeta os pools de todo o processo.
- A criação dos bancos é serializada; o restante dos testes pode rodar em paralelo.
- Os testes exigem Docker. Não há workflow de CI nesta etapa.

## Origem

- [#7](https://github.com/adnerscarpelini/ouroboros-server/issues/7): testes de integração contra PostgreSQL real.
- [#33](https://github.com/adnerscarpelini/ouroboros-server/issues/33): DbContext do Settings migrado pela fixture e teste das fronteiras de schema.
- [#12](https://github.com/adnerscarpelini/ouroboros-server/issues/12): factory do backoffice e provider de logs compartilhado.
- [#13](https://github.com/adnerscarpelini/ouroboros-server/issues/13): relógio falso e hasher substituídos pela DI nos testes do bloqueio de conta.
- [#14](https://github.com/adnerscarpelini/ouroboros-server/issues/14): limite do login elevado na factory e IP simulado por startup filter de teste.
- [#17](https://github.com/adnerscarpelini/ouroboros-server/issues/17): limite do logout elevado na factory.

[Voltar à arquitetura](README.md)
