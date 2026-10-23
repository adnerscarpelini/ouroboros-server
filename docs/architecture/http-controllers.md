# Controllers HTTP nos módulos

## Contexto

A autenticação trará os primeiros endpoints do Ouroboros. Pela [regra de visibilidade](module-structure.md#visibilidade-dos-tipos), só `DependencyInjection` e `Contracts` são `public` em um módulo. Se os endpoints ficassem em `Ouroboros.Api`, o host não conseguiria chamar os casos de uso `internal` sem expô-los.

## Decisão

- Cada módulo declara seus endpoints em controllers explícitos (`ControllerBase`, `[ApiController]` e attribute routing), na pasta `Controllers` do módulo, como tipos `internal`.
- Os controllers ficam enxutos: cada action valida a entrada (formato dos DTOs, campos obrigatórios), chama o caso de uso injetado e traduz o resultado em resposta HTTP; falhas de negócio viram ProblemDetails com o `type` do [catálogo de erros](../errors/README.md), conforme [Respostas de erro com ProblemDetails](error-responses.md). Regras de negócio ficam nos casos de uso e no domínio, nunca no controller, para que outras entradas (como a CLI da #11) reutilizem a mesma regra. Controllers dependem de `Application` e não acessam `Infrastructure` diretamente.
- As rotas são separadas por público, por módulo:

  | Pasta | Rota | Público |
  | --- | --- | --- |
  | `Controllers/Customer` | `api/v{version:apiVersion}/<module>/...` | Aplicação do cliente e endpoints anônimos (login, cadastro, recuperação de senha), servidos pela API externa (`Ouroboros.Api`). |
  | `Controllers/Admin` | `api/v{version:apiVersion}/admin/<module>/...` | Backoffice, servido pelo host de backoffice (`Ouroboros.Backoffice.Api`). |

- O segmento `v{version:apiVersion}` e o atributo `[ApiVersion]` de cada controller seguem o [versionamento da API](api-versioning.md).
- Controllers de backoffice herdam a `internal abstract class AdminControllerBase : ControllerBase` do próprio módulo, que já aplica `[ApiController]` e `[Authorize(Roles = "Admin")]` (veja [Autorização](#autorização)).
- Endpoints de backoffice nunca ficam nas rotas do cliente, e cada público tem seus próprios DTOs de resposta.
- Cada público tem seu próprio host: a API externa registra os controllers de cliente e anônimos, e o backoffice, só os que herdam a `AdminControllerBase`, conforme [Hosts da API externa e do backoffice](hosts.md). Essa separação (#32) substitui a API única com rotas por público decidida na #3; como os controllers já ficavam nos módulos, ela não mudou módulos nem casos de uso.

## Autorização

Os controllers seguem a [autenticação JWT e autorização por políticas](authentication.md) (#9):

- **Nega por padrão.** A política de fallback de cada host exige usuário autenticado e, no backoffice, o papel `Admin`. Uma action ou controller sem anotação de autorização já exige token; só fica anônimo quem declara `[AllowAnonymous]` (por exemplo, o login da #12).
- **A base de backoffice exige `Admin`.** A `AdminControllerBase` do módulo declara `[Authorize(Roles = "Admin")]`, então todo o backoffice do módulo fica protegido mesmo que uma action esqueça de declarar sua política. Um token de `Customer` recebe `403` em qualquer endpoint `api/v1/admin/<module>`.
- **Actions exigem capacidades, não papéis.** Quando uma action precisa de permissão específica, declara uma política nomeada por capacidade, como `[Authorize(Policy = IdentityPolicies.AdminsInvite)]` (`identity:admins.invite`). O mapeamento de papel para política fica num único lugar do módulo dono, em `Controllers/<Module>Policies`, registrado em `Add<Module>Module` com `services.AddAuthorization(...)`.

```csharp
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/identity/admins")]
internal sealed class AdminsController : AdminControllerBase
{
    [HttpPost("invitations")]
    [Authorize(Policy = IdentityPolicies.AdminsInvite)]
    public IActionResult Invite(InviteAdminRequest request) => ...;
}
```

O exemplo é ilustrativo: ainda não há esse controller.

## Como os controllers internal são descobertos

O `ControllerFeatureProvider` padrão do ASP.NET Core só reconhece controllers `public`. Por isso:

- Cada host chama `builder.AddOuroborosHost(audience)` e `app.UseOuroborosHost()`, de [`OuroborosHostExtensions`](../../src/Ouroboros.Hosting/OuroborosHostExtensions.cs) no projeto `Ouroboros.Hosting`. O primeiro chama `AddControllers().ConfigureApplicationPartManager(...)` e troca o provider padrão pelo [`AudienceControllerFeatureProvider`](../../src/Ouroboros.Hosting/AudienceControllerFeatureProvider.cs); o segundo mapeia as rotas com `app.MapControllers()`. Esse provider aceita qualquer classe concreta, não genérica, derivada de `ControllerBase` e sem `[NonController]` das [application parts](https://learn.microsoft.com/en-us/aspnet/core/mvc/advanced/app-parts) registradas e, entre elas, só as do público do host: no backoffice, as que herdam uma `AdminControllerBase` de namespace terminado em `.Controllers.Admin`; na API externa, todas as demais (veja [Hosts](hosts.md#ouroboroshosting)).
- Cada módulo com controllers registra o próprio assembly em `Add<Module>Module`, com `services.AddControllers().AddApplicationPart(typeof(DependencyInjection).Assembly)`, troca o pacote `Microsoft.Extensions.DependencyInjection.Abstractions` por `<FrameworkReference Include="Microsoft.AspNetCore.App" />` e referencia `Asp.Versioning.Abstractions` para declarar `[ApiVersion]`.

O SDK também gera `[assembly: ApplicationPart]` em cada host para os módulos referenciados que usam ASP.NET Core, então hoje o registro explícito é redundante. Ele é mantido para deixar claro que o módulo publica controllers, sem depender desse detalhe do SDK.

Os testes sobem os hosts com `WebApplicationFactory`, por meio da [`OuroborosApiFactory` e da `BackofficeApiFactory`](../../tests/Ouroboros.ArchitectureTests/OuroborosApiFactory.cs), que fornecem a connection string fictícia (veja [Persistência](persistence.md#testes)) e a `SigningKey` de teste exigidas na subida. A da API externa usa o `Program` da `Ouroboros.Api`; a do backoffice usa o tipo `BackofficeApiEntryPoint`, porque o .NET 10 gera um `Program` público em cada host web.

## Alternativas descartadas

- Controllers em `Ouroboros.Api`: exigiriam tornar públicos os casos de uso, contrariando a regra de visibilidade.
- Minimal APIs com grupos de rota por público: a Microsoft as recomenda para projetos novos ([APIs overview](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/apis)), mas o projeto prefere controllers explícitos por familiaridade e por agruparem as ações de um recurso. Em troca, a separação do backoffice depende de herança verificada por teste, e não de um grupo de rota; a exigência do papel `Admin` não depende só dela, porque a política padrão do backoffice também a aplica.

## Estado atual

Somente o Identity está preparado para controllers: registra sua application part, usa `Microsoft.AspNetCore.App`, referencia `Asp.Versioning.Abstractions` e `Microsoft.AspNetCore.Authentication.JwtBearer`, tem a [`AdminControllerBase`](../../src/Modules/Ouroboros.Modules.Identity/Controllers/Admin/AdminControllerBase.cs), que exige o papel `Admin`, e as políticas por capacidade em [`IdentityPolicies`](../../src/Modules/Ouroboros.Modules.Identity/Controllers/IdentityPolicies.cs) (`identity:admins.invite`, ainda sem endpoint que a use). Ainda não há controllers concretos nem a pasta `Controllers/Customer`; o primeiro endpoint será o login (#12). Por isso, nos dois hosts, qualquer rota responde `401` sem token e `404` com token válido (no backoffice, de um `Admin`), em ProblemDetails (veja [Respostas de erro](error-responses.md#respostas-sem-corpo-401-403-404-405-415)). Customers, Accounts e Transactions ainda usam o pacote de abstrações de DI.

## Testes de arquitetura

O projeto [`tests/Ouroboros.ArchitectureTests`](../../tests/Ouroboros.ArchitectureTests/Ouroboros.ArchitectureTests.csproj) usa xUnit v3 e `Microsoft.AspNetCore.Mvc.Testing`:

- [`ModuleVisibilityTests`](../../tests/Ouroboros.ArchitectureTests/ModuleVisibilityTests.cs): descobre os módulos pelas referências dos dois hosts (um módulo novo entra nos testes ao ser registrado em qualquer um deles) e verifica que há ao menos um módulo, que cada módulo só expõe `DependencyInjection` e tipos em `<Module>.Contracts` (além das [migrations geradas pelo EF Core](persistence.md#migrations-geradas)) e que nenhum módulo concede `InternalsVisibleTo` a `Ouroboros.Api` nem a `Ouroboros.Backoffice.Api`. Juntas, as duas últimas condições garantem pelo compilador que nenhum host usa tipos internos dos módulos; o acesso por reflexão em runtime não é coberto.
- [`ControllerDiscoveryTests`](../../tests/Ouroboros.ArchitectureTests/ControllerDiscoveryTests.cs): registra na API externa uma application part com um controller `internal` que depende de um serviço `internal` e verifica que ele responde na rota declarada (`/api/v1/probe`). Remover o `AudienceControllerFeatureProvider` faz esse teste falhar.
- [`HostSeparationTests`](../../tests/Ouroboros.ArchitectureTests/HostSeparationTests.cs): verifica que cada host só registra os controllers do seu público; detalhes em [Hosts](hosts.md#testes).
- [`ControllerRouteTests`](../../tests/Ouroboros.ArchitectureTests/ControllerRouteTests.cs): sobre as actions que os dois hosts realmente registram (a união deles), exige que todo controller de módulo declare ao menos um `[ApiVersion]` (`[ApiVersionNeutral]` não é aceito) e use attribute routing com prefixo `api/v{version:apiVersion}/<module>/` ou `api/v{version:apiVersion}/admin/<module>/`, e que o segmento `admin/` apareça se e somente se o controller herdar a `AdminControllerBase` do módulo. Como só enxerga controllers descobertos, depende da garantia do teste anterior.
- [`AuthorizationTests`](../../tests/Ouroboros.ArchitectureTests/AuthorizationTests.cs): política padrão, validação do token, `403` de `Customer` no backoffice e exigência do papel `Admin` na `AdminControllerBase` de cada módulo; detalhes em [Autenticação](authentication.md#testes). Os controllers de teste que não exercitam autorização declaram `[AllowAnonymous]` ou recebem um token válido.
- [`ApiVersioningTests`](../../tests/Ouroboros.ArchitectureTests/ApiVersioningTests.cs): verifica os headers de versão, a rejeição de versão não declarada ou ausente e a versão descontinuada; detalhes em [Versionamento da API HTTP](api-versioning.md#testes).

Na raiz do repositório:

```powershell
dotnet test --solution Ouroboros.slnx
```

O [`global.json`](../../global.json) define `"test": {"runner": "Microsoft.Testing.Platform"}` porque o SDK do .NET 10 não executa xUnit v3 pelo VSTest.

## Origem

- [#3](https://github.com/adnerscarpelini/ouroboros-server/issues/3): decisão e implementação inicial.
- [#9](https://github.com/adnerscarpelini/ouroboros-server/issues/9): autenticação JWT, política padrão que nega por padrão, exigência do papel `Admin` na `AdminControllerBase` e políticas por capacidade.
- [#26](https://github.com/adnerscarpelini/ouroboros-server/issues/26): segmento de versão nas rotas e `[ApiVersion]` nos controllers.
- [#4](https://github.com/adnerscarpelini/ouroboros-server/issues/4): tradução de falhas de negócio em ProblemDetails.
- [#32](https://github.com/adnerscarpelini/ouroboros-server/issues/32): hosts separados para a API externa e o backoffice, substituindo a API única.

[Voltar à arquitetura](README.md)
