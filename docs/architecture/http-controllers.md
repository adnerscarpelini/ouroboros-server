# Controllers HTTP nos hosts

## Histórico

Esta nota manteve o nome do arquivo para não quebrar links, mas a decisão mudou duas vezes:

1. **#3:** controllers `internal` dentro de cada módulo, em `Controllers/Customer` e `Controllers/Admin`, numa API única com rotas por público.
2. **#32:** dois hosts, a API externa e o backoffice ([Hosts](hosts.md)). Os controllers continuaram nos módulos, e um `AudienceControllerFeatureProvider` em `Ouroboros.Hosting` escolhia os de cada host pela herança da `AdminControllerBase`.
3. **#47 (atual):** os controllers saem dos módulos e passam a ser declarados nos próprios hosts. Os módulos expõem seus casos de uso em `Contracts`. Esta decisão substitui as duas anteriores.

## Contexto

Com os controllers nos módulos, a apresentação HTTP e a regra de negócio ficavam no mesmo projeto, e a separação entre os hosts dependia de uma convenção de herança e de um provider próprio de descoberta. O projeto prefere camadas explícitas: a apresentação HTTP no host e a regra de negócio no módulo, como no [modular-monolith-with-ddd](https://github.com/kgrzybek/modular-monolith-with-ddd), em que os controllers ficam no projeto da API, organizados por módulo, e chamam a fronteira pública de cada módulo. Pela [regra de visibilidade](module-structure.md#visibilidade-dos-tipos), essa fronteira só pode estar em `Contracts`.

## Decisão

- Os controllers são `public` e ficam no projeto de cada host, numa pasta por módulo:

  | Host | Pasta e namespace | Rota |
  | --- | --- | --- |
  | `Ouroboros.Api` (externo: app do cliente, anônimos e integrações) | `src/Ouroboros.Api/Controllers/<Module>/`, namespace `Ouroboros.Api.Controllers.<Module>` | `api/v{version:apiVersion}/<module>/...` |
  | `Ouroboros.Backoffice.Api` (interno) | `src/Ouroboros.Backoffice.Api/Controllers/<Module>/`, namespace `Ouroboros.Backoffice.Api.Controllers.<Module>` | `api/v{version:apiVersion}/admin/<module>/...` |

- Os controllers são explícitos: `ControllerBase`, `[ApiController]`, `[ApiVersion]` ([versionamento](api-versioning.md)) e attribute routing.
- Cada host usa a descoberta padrão do ASP.NET Core (`AddControllers()` em [`OuroborosHostExtensions`](../../src/Ouroboros.Hosting/OuroborosHostExtensions.cs)) e só conhece os controllers do próprio projeto. A separação entre a API externa e o backoffice é por projeto, não por convenção de herança.
- Os controllers ficam enxutos: cada action valida a entrada (formato dos DTOs, campos obrigatórios), chama o caso de uso e traduz o resultado em resposta HTTP. Falhas de negócio viram ProblemDetails com o `type` do [catálogo de erros](../errors/README.md), conforme [Respostas de erro com ProblemDetails](error-responses.md). Regra de negócio fica no módulo, nunca no controller, para que outras entradas (como a CLI da #11) reutilizem a mesma regra.
- Cada caso de uso exposto é uma interface `public` em `Contracts/<Funcionalidade>/` do módulo, com request e response `public`. Eles só usam `ExternalId` como identificador e nunca expõem entidades ([convenções das entidades](entity-conventions.md)). A implementação fica `internal` em `Application/<Funcionalidade>/` e é registrada em `Add<Module>Module`.
- Controllers dependem só de `Contracts` dos módulos. `Application`, `Domain` e `Infrastructure` continuam `internal`, e o compilador impede o host de usá-los (erro CS0122).
- Endpoints de backoffice nunca ficam na API externa, e cada host tem seus próprios DTOs de resposta HTTP quando o formato diferir do contrato do módulo.

## Autorização

Os controllers seguem a [autenticação JWT e autorização por políticas](authentication.md) (#9):

- **Nega por padrão.** A política de fallback de cada host exige usuário autenticado e, no backoffice, também o papel `Admin`, em todo endpoint. Não há base de controller: um controller de backoffice sem nenhuma anotação já exige `Admin`, e um token de `Customer` recebe `403` em qualquer endpoint `api/v1/admin/<module>`. Só fica anônimo quem declara `[AllowAnonymous]` (por exemplo, o login da #12).
- **Actions exigem capacidades, não papéis.** Quando uma action precisa de permissão específica, declara `[Authorize(Policy = ...)]` com o nome da capacidade. Os nomes são constantes `public` em `Contracts` do módulo dono, como [`IdentityPolicies.AdminsInvite`](../../src/Modules/Ouroboros.Modules.Identity/Contracts/IdentityPolicies.cs) (`identity:admins.invite`). O mapeamento de capacidade para papel continua `internal`, num único lugar do módulo, registrado em `Add<Module>Module` com `services.AddAuthorization(...)`.

## Como criar um endpoint

O exemplo abaixo é **ilustrativo**: ainda não há casos de uso expostos nem controllers reais. O primeiro será o login (#12), e os nomes podem mudar quando ele for implementado.

1. **Contrato no módulo.** Em `src/Modules/Ouroboros.Modules.Identity/Contracts/Login/`, declare a interface do caso de uso e seus tipos de entrada e saída, todos `public`, com `ExternalId` como identificador e sem entidades:

   ```csharp
   namespace Ouroboros.Modules.Identity.Contracts.Login;

   public interface ILogin
   {
       Task<LoginResult> ExecuteAsync(LoginRequest request, CancellationToken cancellationToken);
   }

   public sealed record LoginRequest(string Email, string Password);

   public sealed record LoginResult(bool Succeeded, string? AccessToken, DateTimeOffset? ExpiresAt);
   ```

2. **Implementação interna.** Em `Application/Login/`, implemente a interface num tipo `internal sealed`. A regra de negócio, o domínio e a persistência ficam aqui e nas demais pastas internas do módulo.

   ```csharp
   namespace Ouroboros.Modules.Identity.Application.Login;

   internal sealed class Login(/* dependências internas */) : ILogin
   {
       public Task<LoginResult> ExecuteAsync(LoginRequest request, CancellationToken cancellationToken) => ...;
   }
   ```

3. **Registro na DI do módulo.** Em `AddIdentityModule`, registre a interface pública com a implementação interna, por exemplo `services.AddScoped<ILogin, Login>()`. Se o endpoint exigir uma capacidade nova, adicione a constante em `Contracts/<Module>Policies` e o mapeamento para o papel no mesmo `Add<Module>Module`.

4. **Erros de negócio.** Se o caso de uso tiver falhas de negócio, documente cada uma no [catálogo de erros](../errors/README.md#como-adicionar-um-erro) do módulo antes de traduzi-las no controller.

5. **Controller no host.** Na pasta do módulo dentro do host do público, crie um controller `public`. Ele recebe a interface por injeção, valida a entrada, chama o caso de uso e traduz o resultado:

   ```csharp
   namespace Ouroboros.Api.Controllers.Identity;

   [ApiController]
   [ApiVersion(1)]
   [Route("api/v{version:apiVersion}/identity/sessions")]
   public sealed class SessionsController(ILogin login) : ControllerBase
   {
       [HttpPost]
       [AllowAnonymous]
       public async Task<IActionResult> Create(LoginRequest request, CancellationToken cancellationToken)
       {
           var result = await login.ExecuteAsync(request, cancellationToken);
           return result.Succeeded
               ? Ok(result)
               : Problem(type: "<URI do catálogo>", title: "...", statusCode: StatusCodes.Status401Unauthorized);
       }
   }
   ```

   No backoffice, o controller fica em `src/Ouroboros.Backoffice.Api/Controllers/<Module>/`, com rota `api/v{version:apiVersion}/admin/<module>/...`, e declara a capacidade quando houver:

   ```csharp
   namespace Ouroboros.Backoffice.Api.Controllers.Identity;

   [ApiController]
   [ApiVersion(1)]
   [Route("api/v{version:apiVersion}/admin/identity/admins")]
   public sealed class AdminsController(IInviteAdmin inviteAdmin) : ControllerBase
   {
       [HttpPost("invitations")]
       [Authorize(Policy = IdentityPolicies.AdminsInvite)]
       public async Task<IActionResult> Invite(InviteAdminRequest request, CancellationToken cancellationToken) => ...;
   }
   ```

6. **Registro do módulo no host.** O host precisa chamar `Add<Module>Module` no seu `Program.cs`; hoje os dois hosts registram os quatro módulos ([Hosts](hosts.md#decisão)).

7. **Testes.** Rode os [testes de arquitetura](#testes-de-arquitetura): eles reprovam controller fora de `<Host>.Controllers.<Module>`, sem `[ApiVersion]`, com rota fora do prefixo do host ou `internal`. Os testes do comportamento do endpoint acompanham a funcionalidade.

Erros comuns que o compilador ou os testes apontam:

- Injetar no controller um tipo de `Application`, `Domain` ou `Infrastructure`: não compila (CS0122). Exponha a interface em `Contracts`.
- Declarar o controller como `internal`: a descoberta padrão o ignora sem aviso, e a rota responderia `404`. `HostSeparationTests` reprova.
- Colocar o controller em `Controllers/` sem a pasta do módulo, ou numa pasta de módulo inexistente: `ControllerRouteTests` reprova.

## Alternativas descartadas

- **Controllers `internal` nos módulos** (decisão da #3, mantida na #32): evitava expor casos de uso, mas misturava apresentação HTTP e regra de negócio no módulo e exigia um provider de descoberta próprio, com a separação dos hosts dependendo de convenção de herança.
- **Minimal APIs com grupos de rota por público:** a Microsoft as recomenda para projetos novos ([APIs overview](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/apis)), mas o projeto prefere controllers explícitos por familiaridade e por agruparem as ações de um recurso.

## Consequências

- As camadas ficam explícitas, e cada API reúne todos os seus endpoints num único projeto.
- Cada funcionalidade com endpoint toca dois projetos: o módulo (contrato, implementação e DI) e o host (controller).
- A superfície pública do módulo cresce com cada caso de uso exposto, e mudar a assinatura de um caso de uso afeta os hosts que o usam.
- Os casos de uso em `Contracts` também ficam visíveis a outros módulos que referenciem o dono; as [fronteiras entre módulos](module-structure.md#fronteiras-adotadas-para-a-evolução) continuam valendo.
- A proteção do backoffice não depende de herança: vem da política padrão do host.

## Estado atual

Ainda não há casos de uso expostos nem controllers nos hosts; as pastas `Controllers` dos hosts serão criadas com o primeiro endpoint, o login (#12). O único tipo em `Contracts` é o [`IdentityPolicies`](../../src/Modules/Ouroboros.Modules.Identity/Contracts/IdentityPolicies.cs), com `identity:admins.invite`, ainda sem endpoint que a use. Por isso, nos dois hosts, qualquer rota responde `401` sem token e `404` com token válido (no backoffice, de um `Admin`), em ProblemDetails (veja [Respostas de erro](error-responses.md#respostas-sem-corpo-401-403-404-405-415)).

## Testes de arquitetura

O projeto [`tests/Ouroboros.ArchitectureTests`](../../tests/Ouroboros.ArchitectureTests/Ouroboros.ArchitectureTests.csproj) usa xUnit v3 e `Microsoft.AspNetCore.Mvc.Testing`. Os testes sobem os hosts com `WebApplicationFactory`, por meio da [`OuroborosApiFactory` e da `BackofficeApiFactory`](../../tests/Ouroboros.ArchitectureTests/OuroborosApiFactory.cs), que fornecem a connection string fictícia (veja [Persistência](persistence.md#testes)) e a `SigningKey` de teste exigidas na subida. A da API externa usa o `Program` da `Ouroboros.Api`; a do backoffice usa o tipo `BackofficeApiEntryPoint`, porque o .NET 10 gera um `Program` público em cada host web.

- [`ModuleVisibilityTests`](../../tests/Ouroboros.ArchitectureTests/ModuleVisibilityTests.cs): descobre os módulos pelas referências dos dois hosts (um módulo novo entra nos testes ao ser registrado em qualquer um deles) e verifica que há ao menos um módulo, que cada módulo só expõe `DependencyInjection` e tipos em `<Module>.Contracts` e seus sub-namespaces (além das [migrations geradas pelo EF Core](persistence.md#migrations-geradas)) e que nenhum módulo concede `InternalsVisibleTo` a `Ouroboros.Api` nem a `Ouroboros.Backoffice.Api`. Juntas, essas condições garantem pelo compilador que nenhum host usa tipos internos dos módulos; o acesso por reflexão em runtime não é coberto.
- [`HostSeparationTests`](../../tests/Ouroboros.ArchitectureTests/HostSeparationTests.cs): nenhum módulo declara tipos derivados de `ControllerBase`; nenhum módulo é application part de algum host; a API externa não registra controllers do backoffice, e vice-versa; todo controller concreto dos hosts é `public`, porque a descoberta padrão ignora controllers `internal` sem avisar.
- [`ControllerRouteTests`](../../tests/Ouroboros.ArchitectureTests/ControllerRouteTests.cs), um teste por host, sobre as actions que o host realmente registra: o controller fica em `<Host>.Controllers.<Module>` de um módulo existente, declara ao menos um `[ApiVersion]` (`[ApiVersionNeutral]` não é aceito) e usa attribute routing com o prefixo do seu público, `api/v{version:apiVersion}/<module>/` na API externa e `api/v{version:apiVersion}/admin/<module>/` no backoffice.
- [`ControllerDiscoveryTests`](../../tests/Ouroboros.ArchitectureTests/ControllerDiscoveryTests.cs): um controller de uma application part registrada responde na rota declarada (`/api/v1/probe`) e é ativado com as dependências da DI.
- [`AuthorizationTests`](../../tests/Ouroboros.ArchitectureTests/AuthorizationTests.cs): política padrão, validação do token e `403` de `Customer` no backoffice, inclusive num controller sem nenhuma política própria; detalhes em [Autenticação](authentication.md#testes).
- [`ApiVersioningTests`](../../tests/Ouroboros.ArchitectureTests/ApiVersioningTests.cs): headers de versão, rejeição de versão não declarada ou ausente e versão descontinuada; detalhes em [Versionamento da API HTTP](api-versioning.md#testes).

Os controllers de teste ficam no assembly de testes, que os testes registram como application part para exercitar o pipeline dos hosts ([`ProbeControllers.cs`](../../tests/Ouroboros.ArchitectureTests/ProbeControllers.cs) e [`Controllers/Admin/AdminProbeController.cs`](../../tests/Ouroboros.ArchitectureTests/Controllers/Admin/AdminProbeController.cs)). Eles são `public`, como os dos hosts, e não passam pelo `ControllerRouteTests`, que só verifica controllers dos assemblies dos hosts. Os que não exercitam autorização declaram `[AllowAnonymous]` ou recebem um token válido.

Na raiz do repositório:

```powershell
dotnet test --solution Ouroboros.slnx
```

O [`global.json`](../../global.json) define `"test": {"runner": "Microsoft.Testing.Platform"}` porque o SDK do .NET 10 não executa xUnit v3 pelo VSTest. Na validação da #47, a suíte tinha 93 testes.

## Origem

- [#3](https://github.com/adnerscarpelini/ouroboros-server/issues/3): controllers `internal` nos módulos e rotas por público (substituída).
- [#26](https://github.com/adnerscarpelini/ouroboros-server/issues/26): segmento de versão nas rotas e `[ApiVersion]` nos controllers.
- [#4](https://github.com/adnerscarpelini/ouroboros-server/issues/4): tradução de falhas de negócio em ProblemDetails.
- [#32](https://github.com/adnerscarpelini/ouroboros-server/issues/32): hosts separados e descoberta dos controllers por público (descoberta substituída).
- [#9](https://github.com/adnerscarpelini/ouroboros-server/issues/9): autenticação JWT, política padrão que nega por padrão e políticas por capacidade.
- [#47](https://github.com/adnerscarpelini/ouroboros-server/issues/47): controllers nos hosts e casos de uso em `Contracts`.

[Voltar à arquitetura](README.md)
