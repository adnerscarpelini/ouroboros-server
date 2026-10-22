# Controllers HTTP nos módulos

## Contexto

A autenticação trará os primeiros endpoints do Ouroboros. Pela [regra de visibilidade](module-structure.md#visibilidade-dos-tipos), só `DependencyInjection` e `Contracts` são `public` em um módulo. Se os endpoints ficassem em `Ouroboros.Api`, o host não conseguiria chamar os casos de uso `internal` sem expô-los.

## Decisão

- Cada módulo declara seus endpoints em controllers explícitos (`ControllerBase`, `[ApiController]` e attribute routing), na pasta `Controllers` do módulo, como tipos `internal`.
- Os controllers ficam enxutos: cada action valida a entrada (formato dos DTOs, campos obrigatórios), chama o caso de uso injetado e traduz o resultado em resposta HTTP. Regras de negócio ficam nos casos de uso e no domínio, nunca no controller, para que outras entradas (como a CLI da #11) reutilizem a mesma regra. Controllers dependem de `Application` e não acessam `Infrastructure` diretamente.
- As rotas são separadas por público, por módulo:

  | Pasta | Rota | Público |
  | --- | --- | --- |
  | `Controllers/Customer` | `api/v{version:apiVersion}/<module>/...` | Aplicação do cliente e endpoints anônimos (login, cadastro, recuperação de senha). |
  | `Controllers/Admin` | `api/v{version:apiVersion}/admin/<module>/...` | Backoffice. |

- O segmento `v{version:apiVersion}` e o atributo `[ApiVersion]` de cada controller seguem o [versionamento da API](api-versioning.md).
- Controllers de backoffice herdam a `internal abstract class AdminControllerBase : ControllerBase` do próprio módulo, que já aplica `[ApiController]`. A exigência do papel `Admin` será declarada nessa base e protegerá todo o backoffice do módulo.
- Endpoints de backoffice nunca ficam nas rotas do cliente, e cada público tem seus próprios DTOs de resposta.
- Há uma única API, com rotas separadas por público, em vez de um host por frontend ([Backends for Frontends](https://learn.microsoft.com/en-us/azure/architecture/patterns/backends-for-frontends)): não há frontend nem ambiente de deploy que justifiquem um segundo host. Como os controllers ficam nos módulos, separar o backoffice depois exigiria apenas um novo projeto de host registrando os controllers de admin, sem mudar casos de uso.

## Como os controllers internal são descobertos

O `ControllerFeatureProvider` padrão do ASP.NET Core só reconhece controllers `public`. Por isso:

- O [host](../../src/Ouroboros.Api/Program.cs) chama `AddControllers().ConfigureApplicationPartManager(...)`, troca o provider padrão pelo [`ModuleControllerFeatureProvider`](../../src/Ouroboros.Api/ModuleControllerFeatureProvider.cs) e mapeia as rotas com `app.MapControllers()`. Esse provider aceita qualquer classe concreta, não genérica, derivada de `ControllerBase` e sem `[NonController]` das [application parts](https://learn.microsoft.com/en-us/aspnet/core/mvc/advanced/app-parts) registradas.
- Cada módulo com controllers registra o próprio assembly em `Add<Module>Module`, com `services.AddControllers().AddApplicationPart(typeof(DependencyInjection).Assembly)`, troca o pacote `Microsoft.Extensions.DependencyInjection.Abstractions` por `<FrameworkReference Include="Microsoft.AspNetCore.App" />` e referencia `Asp.Versioning.Abstractions` para declarar `[ApiVersion]`.

O SDK também gera `[assembly: ApplicationPart]` em `Ouroboros.Api` para os módulos referenciados que usam ASP.NET Core, então hoje o registro explícito é redundante. Ele é mantido para deixar claro que o módulo publica controllers, sem depender desse detalhe do SDK.

`Program.cs` termina com `public partial class Program;` para que os testes usem `WebApplicationFactory<Program>`, por meio da [`OuroborosApiFactory`](../../tests/Ouroboros.ArchitectureTests/OuroborosApiFactory.cs), que fornece a connection string fictícia exigida na subida (veja [Persistência](persistence.md#testes)).

## Alternativas descartadas

- Controllers em `Ouroboros.Api`: exigiriam tornar públicos os casos de uso, contrariando a regra de visibilidade.
- Minimal APIs com grupos de rota por público: a Microsoft as recomenda para projetos novos ([APIs overview](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/apis)), mas o projeto prefere controllers explícitos por familiaridade e por agruparem as ações de um recurso. Em troca, a proteção do backoffice depende de herança verificada por teste, e não de um grupo de rota.

## Estado atual

Somente o Identity está preparado para controllers: registra sua application part, usa `Microsoft.AspNetCore.App`, referencia `Asp.Versioning.Abstractions` e tem a [`AdminControllerBase`](../../src/Modules/Ouroboros.Modules.Identity/Controllers/Admin/AdminControllerBase.cs). Ainda não há controllers concretos nem a pasta `Controllers/Customer`; o primeiro endpoint será o login (#12). Por isso, qualquer rota responde 404. Customers, Accounts e Transactions ainda usam o pacote de abstrações de DI.

A exigência do papel `Admin` na `AdminControllerBase` (token de `Customer` recebe 403) entra com a autenticação JWT na #9 e ainda não está implementada: hoje a base não aplica nenhuma restrição.

## Testes de arquitetura

O projeto [`tests/Ouroboros.ArchitectureTests`](../../tests/Ouroboros.ArchitectureTests/Ouroboros.ArchitectureTests.csproj) usa xUnit v3 e `Microsoft.AspNetCore.Mvc.Testing`:

- [`ModuleVisibilityTests`](../../tests/Ouroboros.ArchitectureTests/ModuleVisibilityTests.cs): descobre os módulos pelas referências de `Ouroboros.Api` (um módulo novo entra nos testes ao ser registrado no host) e verifica que a Api referencia ao menos um módulo, que cada módulo só expõe `DependencyInjection` e tipos em `<Module>.Contracts` (além das [migrations geradas pelo EF Core](persistence.md#migrations-geradas)) e que nenhum módulo concede `InternalsVisibleTo` à Api. Juntas, as duas últimas condições garantem pelo compilador que a Api não usa tipos internos dos módulos; o acesso por reflexão em runtime não é coberto.
- [`ControllerDiscoveryTests`](../../tests/Ouroboros.ArchitectureTests/ControllerDiscoveryTests.cs): registra uma application part com um controller `internal` que depende de um serviço `internal` e verifica que ele responde na rota declarada (`/api/v1/probe`). Remover o `ModuleControllerFeatureProvider` faz esse teste falhar.
- [`ControllerRouteTests`](../../tests/Ouroboros.ArchitectureTests/ControllerRouteTests.cs): sobre as actions que o host realmente registra, exige que todo controller de módulo declare ao menos um `[ApiVersion]` (`[ApiVersionNeutral]` não é aceito) e use attribute routing com prefixo `api/v{version:apiVersion}/<module>/` ou `api/v{version:apiVersion}/admin/<module>/`, e que o segmento `admin/` apareça se e somente se o controller herdar a `AdminControllerBase` do módulo. Como só enxerga controllers descobertos, depende da garantia do teste anterior.
- [`ApiVersioningTests`](../../tests/Ouroboros.ArchitectureTests/ApiVersioningTests.cs): verifica os headers de versão, a rejeição de versão não declarada ou ausente e a versão descontinuada; detalhes em [Versionamento da API HTTP](api-versioning.md#testes).

Na raiz do repositório:

```powershell
dotnet test --solution Ouroboros.slnx
```

O [`global.json`](../../global.json) define `"test": {"runner": "Microsoft.Testing.Platform"}` porque o SDK do .NET 10 não executa xUnit v3 pelo VSTest.

## Origem

- [#3](https://github.com/adnerscarpelini/ouroboros-server/issues/3): decisão e implementação inicial.
- [#9](https://github.com/adnerscarpelini/ouroboros-server/issues/9): autenticação JWT e exigência do papel `Admin` na `AdminControllerBase`.
- [#26](https://github.com/adnerscarpelini/ouroboros-server/issues/26): segmento de versão nas rotas e `[ApiVersion]` nos controllers.

[Voltar à arquitetura](README.md)
