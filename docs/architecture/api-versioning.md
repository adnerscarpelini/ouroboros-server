# Versionamento da API HTTP

## Contexto

A API ainda não tem endpoints nem clientes. Incluir um prefixo de versão depois do primeiro cliente mudaria todas as rotas de uma vez; com o versionamento desde já, uma mudança incompatível vira uma nova versão sem quebrar quem usa a anterior.

## Decisão

- A versão fica no caminho da URL, com o pacote [`Asp.Versioning.Mvc`](https://dotnet.github.io/aspnet-api-versioning/aspnet-core/how-to/version-by-url.html):

  | Público | Rota |
  | --- | --- |
  | Cliente e anônimos | `api/v{version:apiVersion}/<module>/...` |
  | Backoffice | `api/v{version:apiVersion}/admin/<module>/...` |

- Só a versão maior é exposta (`/api/v1/...`), como no [AIP-185](https://google.aip.dev/185) do Google. Mudanças compatíveis não mudam a versão.
- Não há versão padrão implícita: toda requisição informa a versão na URL. O Asp.Versioning não permite versão padrão quando a versão está no caminho.
- O [host](../../src/Ouroboros.Api/Program.cs) configura `AddApiVersioning` com `UrlSegmentApiVersionReader` e `ReportApiVersions = true`, seguido de `.AddMvc()`.
- Os módulos com controllers referenciam apenas `Asp.Versioning.Abstractions`, que traz os atributos `[ApiVersion]` e `[MapToApiVersion]`. O Identity já referencia o pacote.

## Como declarar versões

Cada controller de módulo declara as versões que atende:

```csharp
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/identity/sessions")]
internal sealed class SessionsController : ControllerBase
{
    [HttpPost]
    public IActionResult Create(...) => ...;
}
```

- Uma action que existe só em algumas versões do controller usa `[MapToApiVersion(n)]`.
- Para descontinuar uma versão, marque-a com `[ApiVersion(n, Deprecated = true)]` e declare a nova versão. A versão descontinuada continua respondendo normalmente até ser removida.
- `[ApiVersionNeutral]` não é aceito em controllers de módulo: o teste de rotas exige `[ApiVersion]`.

O exemplo acima é ilustrativo; ainda não há controllers concretos (veja [Controllers HTTP nos módulos](http-controllers.md#estado-atual)).

## Comportamento

- As respostas trazem o header `api-supported-versions` (por exemplo `1.0`) e, se houver versões descontinuadas, `api-deprecated-versions`.
- Versão não declarada (`/api/v9/...`), versão inválida (`/api/vX/...`) ou URL sem segmento de versão respondem `404` sem executar a action. Desde a [#4](https://github.com/adnerscarpelini/ouroboros-server/issues/4), o corpo é um ProblemDetails com `traceId` (veja [Respostas de erro com ProblemDetails](error-responses.md#respostas-sem-corpo-404-405-415)).
- `v1.0` e `V1` são aceitos como sinônimos de `v1`.
- A query string `api-version` é ignorada; só o segmento da URL define a versão.

## Alternativas descartadas

- Versão por header ou por data, como a [Stripe](https://docs.stripe.com/api-versions) e o Azure: exige negociação ou transformação entre versões, sem necessidade aqui.
- Versão por query string (`?api-version=1`) ou por media type: menos visíveis e menos comuns em APIs .NET.

Ficam fora do escopo a documentação OpenAPI por versão e a política de prazo de descontinuação (header `Sunset`).

## Testes

Em [`tests/Ouroboros.ArchitectureTests`](../../tests/Ouroboros.ArchitectureTests/Ouroboros.ArchitectureTests.csproj):

- [`ApiVersioningTests`](../../tests/Ouroboros.ArchitectureTests/ApiVersioningTests.cs): `v1` responde com `api-supported-versions`; versão não declarada e URL sem versão não executam a action; versão descontinuada responde e informa `api-deprecated-versions`. Usa os controllers de teste de [`ProbeControllers.cs`](../../tests/Ouroboros.ArchitectureTests/ProbeControllers.cs).
- [`ControllerRouteTests`](../../tests/Ouroboros.ArchitectureTests/ControllerRouteTests.cs): exige o segmento de versão no prefixo das rotas e `[ApiVersion]` em todo controller de módulo (veja [Testes de arquitetura](http-controllers.md#testes-de-arquitetura)).

## Origem

- [#26](https://github.com/adnerscarpelini/ouroboros-server/issues/26): decisão e implementação.
- [#4](https://github.com/adnerscarpelini/ouroboros-server/issues/4): `404` de versão inexistente em ProblemDetails.

[Voltar à arquitetura](README.md)
