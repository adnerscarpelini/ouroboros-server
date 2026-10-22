# Testes de integração com PostgreSQL

## Contexto

Os [testes de arquitetura](http-controllers.md#testes-de-arquitetura) sobem o host com uma connection string fictícia e nunca acessam o banco. Faltava provar que a API sobe contra um PostgreSQL real, com as migrations de cada módulo aplicadas, e dar aos testes futuros um banco isolado para cada teste, sem depender do banco de desenvolvimento nem da configuração da máquina.

## Decisão

O projeto [`tests/Ouroboros.IntegrationTests`](../../tests/Ouroboros.IntegrationTests/Ouroboros.IntegrationTests.csproj) (xUnit v3, `Microsoft.AspNetCore.Mvc.Testing` e [Testcontainers](https://dotnet.testcontainers.org/) para PostgreSQL) testa a API contra um PostgreSQL de verdade, em container:

- **Um container por execução da suíte.** A [`PostgresFixture`](../../tests/Ouroboros.IntegrationTests/PostgresFixture.cs) é uma assembly fixture: sobe um container `postgres:18-alpine` (a mesma imagem do [`compose.yaml`](../../compose.yaml)) no início e o remove no fim. Ela não usa o banco do Docker Compose; os testes passam com ele parado.
- **Migrations aplicadas uma vez, num banco modelo.** A fixture cria o banco `ouroboros_template`, sobe a API contra ele e aplica as migrations de todos os DbContexts de módulo.
- **Um banco por teste, clonado do modelo.** Antes de cada teste, `CREATE DATABASE ... TEMPLATE ouroboros_template` cria um banco com nome único, já migrado; ao final, `DROP DATABASE ... WITH (FORCE)` o remove. As criações são serializadas, porque o PostgreSQL recusa clonar um modelo que esteja em uso.
- **DbContexts descobertos automaticamente.** [`ModuleDbContexts`](../../tests/Ouroboros.IntegrationTests/ModuleDbContexts.cs) encontra por reflexão os DbContexts `internal` dos assemblies `Ouroboros.Modules.*` referenciados pela Api e os resolve pela DI. Os testes não precisam de `InternalsVisibleTo`, e o DbContext de um módulo novo é migrado sem alterar os testes.
- **Configuração independente da máquina.** A [`IntegrationApiFactory`](../../tests/Ouroboros.IntegrationTests/IntegrationApiFactory.cs) sobe a API no ambiente `Testing` com a connection string do banco do teste como última fonte de configuração, acima de user-secrets e variáveis de ambiente como `ConnectionStrings__Ouroboros`. Ela também registra o assembly de testes como application part, para que controllers declarados nos testes sejam mapeados.

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

## Testes existentes

Os módulos ainda não têm endpoints. Por isso, o [`MigrationsProbeController`](../../tests/Ouroboros.IntegrationTests/MigrationsProbeController.cs), que existe só no assembly de testes, expõe `GET /api/v1/integration-probe/migrations` com as migrations aplicadas de cada DbContext de módulo. Os [`DatabaseIntegrationTests`](../../tests/Ouroboros.IntegrationTests/DatabaseIntegrationTests.cs) verificam que:

- a API responde por essa rota com a migration `InitialCreate` do Identity aplicada;
- o schema `identity` tem a tabela `__EFMigrationsHistory`;
- cada teste tem banco próprio: três casos criam a mesma tabela, o que falharia num banco compartilhado.

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

[Voltar à arquitetura](README.md)
