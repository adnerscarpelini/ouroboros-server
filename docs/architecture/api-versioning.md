# Versionamento da API HTTP

## Contexto

Cada API de serviço versiona seus contratos HTTP para que mudanças incompatíveis não quebrem consumidores existentes.

## Decisão

- A versão fica no caminho da URL, com o pacote [`Asp.Versioning.Mvc`](https://dotnet.github.io/aspnet-api-versioning/aspnet-core/how-to/version-by-url.html):

  | Público | Rota |
  | --- | --- |
  | Cliente e anônimos | `api/v{version:apiVersion}/<module>/...` |
  | Administrativo | `api/v{version:apiVersion}/admin/<module>/...` |

- Só a versão maior é exposta (`/api/v1/...`), como no [AIP-185](https://google.aip.dev/185) do Google. Mudanças compatíveis não mudam a versão.
- Não há versão padrão implícita: toda requisição informa a versão na URL. O Asp.Versioning não permite versão padrão quando a versão está no caminho.
- A API do serviço configura AddApiVersioning com UrlSegmentApiVersionReader, ReportApiVersions e AddMvc.
- [ApiVersion] e [MapToApiVersion] pertencem aos controllers da API proprietária, usando Asp.Versioning.Mvc.

## Como declarar versões

Exemplo de organização aprovada: cada controller, na API do serviço (`Controllers/<Module>/`, conforme [Controllers HTTP nas APIs dos serviços](http-controllers.md)), declara as versões que atende:

```csharp
namespace Ouroboros.Identity.Api.Controllers.Identity;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/identity/sessions")]
public sealed class SessionsController : ControllerBase
{
    [HttpPost]
    public IActionResult Create(...) => ...;
}
```

- Uma action que existe só em algumas versões do controller usa `[MapToApiVersion(n)]`.
- Para descontinuar uma versão, marque-a com `[ApiVersion(n, Deprecated = true)]` e declare a nova versão. A versão descontinuada continua respondendo normalmente até ser removida.
- `[ApiVersionNeutral]` não é aceito nos controllers das APIs: o teste de rotas exige `[ApiVersion]`.

O exemplo acima ilustra a declaração da versão 1 para o login (veja [Controllers HTTP nas APIs dos serviços](http-controllers.md)).

## Comportamento

- As respostas trazem o header `api-supported-versions` (por exemplo `1.0`) e, se houver versões descontinuadas, `api-deprecated-versions`.
- Versão não declarada (`/api/v9/...`), versão inválida (`/api/vX/...`) ou URL sem segmento de versão respondem `404` sem executar a action. O corpo é um ProblemDetails com `traceId` (veja [Respostas de erro com ProblemDetails](error-responses.md#respostas-sem-corpo-401-403-404-405-415)). Sem token, essas requisições respondem `401` antes, pela [política padrão de autorização](authentication.md).
- `v1.0` e `V1` são aceitos como sinônimos de `v1`.
- A query string `api-version` é ignorada; só o segmento da URL define a versão.

## Alternativas descartadas

- Versão por header ou por data, como a [Stripe](https://docs.stripe.com/api-versions) e o Azure: exige negociação ou transformação entre versões, sem necessidade aqui.
- Versão por query string (`?api-version=1`) ou por media type: menos visíveis e menos comuns em APIs .NET.

Ficam fora do escopo a documentação OpenAPI por versão e a política de prazo de descontinuação (header `Sunset`).

## Testes

Em [`tests/Ouroboros.ArchitectureTests`](../../tests/Ouroboros.ArchitectureTests/Ouroboros.ArchitectureTests.csproj):

- [`ApiVersioningTests`](../../tests/Ouroboros.ArchitectureTests/ApiVersioningTests.cs): `v1` responde com `api-supported-versions`; versão não declarada e URL sem versão não executam a action; versão descontinuada responde e informa `api-deprecated-versions`. Usa os controllers de teste de [`ProbeControllers.cs`](../../tests/Ouroboros.ArchitectureTests/ProbeControllers.cs).
- [`ControllerRouteTests`](../../tests/Ouroboros.ArchitectureTests/ControllerRouteTests.cs): exige, em cada API, o segmento de versão no prefixo das rotas e `[ApiVersion]` em todo controller (veja [Testes de arquitetura](http-controllers.md#testes-de-arquitetura)).

## Issues relacionadas

- [#26](https://github.com/adnerscarpelini/ouroboros-server/issues/26): decisão e implementação.
- [#4](https://github.com/adnerscarpelini/ouroboros-server/issues/4): `404` de versão inexistente em ProblemDetails.
- [#47](https://github.com/adnerscarpelini/ouroboros-server/issues/47): `[ApiVersion]` declarado nos controllers das APIs.

[Voltar à arquitetura](README.md)
