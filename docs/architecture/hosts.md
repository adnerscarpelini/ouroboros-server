# Hosts da API externa e do backoffice

## Contexto

Na #3, o Ouroboros adotou uma API única, com rotas separadas por público (`api/v1/<module>` para o cliente e `api/v1/admin/<module>` para o backoffice). Funções de backoffice, como alterar parâmetros, criar administradores e cadastrar clients de integração, não devem ficar expostas na mesma API pública usada pelo app e por integrações de terceiros: interfaces administrativas são procuradas por caminhos previsíveis como `/admin` ([OWASP WSTG-CONF-05](https://owasp.org/www-project-web-security-testing-guide/stable/4-Web_Application_Security_Testing/02-Configuration_and_Deployment_Management_Testing/05-Enumerate_Infrastructure_and_Application_Admin_Interfaces)). Um host próprio permite publicar o backoffice só na rede interna.

## Decisão

Esta decisão substitui a "API única" registrada na #3. O Ouroboros tem dois hosts ASP.NET Core, um por público, no espírito do padrão [Backends for Frontends](https://learn.microsoft.com/en-us/azure/architecture/patterns/backends-for-frontends):

| Projeto | Público | Controllers registrados | Portas em desenvolvimento |
| --- | --- | --- | --- |
| [`Ouroboros.Api`](../../src/Ouroboros.Api/Program.cs) | Externo: app do cliente, endpoints anônimos e integrações de terceiros. | Todos os controllers de módulo que não são de backoffice. | `http://localhost:5141` (perfil `http`); `https://localhost:7135` no perfil `https`. |
| [`Ouroboros.Backoffice.Api`](../../src/Ouroboros.Backoffice.Api/Program.cs) | Interno: backoffice. | Só os controllers que herdam a `AdminControllerBase` de um módulo. | `http://localhost:5142` (perfil `http`); `https://localhost:7136` no perfil `https`. |

- Os módulos não mudam: continuam declarando controllers `internal` em `Controllers/Customer` e `Controllers/Admin`, conforme [Controllers HTTP nos módulos](http-controllers.md). Cada host escolhe os do seu público.
- As rotas mantêm os prefixos `api/v{version:apiVersion}/<module>/...` (externa) e `api/v{version:apiVersion}/admin/<module>/...` (backoffice), com o [versionamento da API](api-versioning.md).
- Cada host registra os módulos no próprio `Program.cs` (`Add<Module>Module`), para que a composição de cada um fique visível ali. Hoje os dois registram os quatro módulos.
- A configuração HTTP comum fica no projeto [`Ouroboros.Hosting`](../../src/Ouroboros.Hosting/OuroborosHostExtensions.cs), uma class library que não referencia nenhum módulo.

### Ouroboros.Hosting

- `HostAudience`: o público do host, `External` ou `Backoffice`.
- `CurrentHost`: registro singleton na DI com o público do host, para quem precisar saber em qual host está rodando. A audiência dos tokens não vem dele, e sim de `Authentication:Jwt:Audience` no `appsettings.json` de cada host ([Autenticação](authentication.md#configuração)).
- `OuroborosHostExtensions.AdminRole`: o papel `Admin`, exigido pela política padrão do backoffice.
- [`AudienceControllerFeatureProvider`](../../src/Ouroboros.Hosting/AudienceControllerFeatureProvider.cs) (`internal`): substitui o `ControllerFeatureProvider` padrão. Aceita qualquer classe concreta, não genérica, derivada de `ControllerBase` e sem `[NonController]`, inclusive `internal`, e filtra pelo público. Um controller é de backoffice se alguma classe na sua cadeia de herança se chama `AdminControllerBase` e está num namespace terminado em `.Controllers.Admin`. O backoffice registra só esses; a API externa registra todos os demais. Uma base chamada `AdminControllerBase` fora de `Controllers.Admin` não conta: o controller vai para a API externa.
- `OuroborosHostExtensions`:
  - `builder.AddOuroborosHost(audience)`: registra o `CurrentHost`, os controllers com o provider acima, o [versionamento por URL](api-versioning.md), o [ProblemDetails](error-responses.md) com `traceId` e a [política padrão de autorização](authentication.md#política-padrão-dos-hosts) do público: usuário autenticado na API externa; autenticado e com o papel `Admin` no backoffice.
  - `app.UseOuroborosHost()`: `UseExceptionHandler()`, `UseStatusCodePages()`, `UseAuthentication()`, `UseAuthorization()` e `MapControllers()`.

O esquema JwtBearer e a validação dos tokens são registrados pelo Identity em `AddIdentityModule`, que os dois hosts chamam ([Autenticação](authentication.md#validação-do-token)).

O [`Program.cs` da API externa](../../src/Ouroboros.Api/Program.cs) fica assim (o do backoffice muda só o público):

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddOuroborosHost(HostAudience.External);

builder.Services.AddIdentityModule();
builder.Services.AddCustomersModule();
builder.Services.AddAccountsModule();
builder.Services.AddTransactionsModule();

var app = builder.Build();
app.UseOuroborosHost();
app.Run();
```

### Configuração

- `Ouroboros.Backoffice.Api` usa o mesmo `UserSecretsId` da `Ouroboros.Api`: em desenvolvimento, os dois leem a mesma connection string `ConnectionStrings:Ouroboros` dos user-secrets. Fora de desenvolvimento, ela vem da variável `ConnectionStrings__Ouroboros` de cada processo. O mesmo vale para a chave de assinatura dos tokens, `Authentication:Jwt:SigningKey` (variável `Authentication__Jwt__SigningKey`), compartilhada pelos dois hosts; sem ela, nenhum host sobe ([README](../../README.md#chave-de-assinatura-dos-tokens)).
- Emissor, audiência e tempos de vida dos tokens ficam em `Authentication:Jwt` no `appsettings.json` de cada host; só a audiência difere (`ouroboros-api` e `ouroboros-backoffice`).
- As migrations continuam usando `Ouroboros.Api` como startup project (veja o [README](../../README.md#banco-de-dados-local)).
- Os dois hosts podem rodar ao mesmo tempo; os comandos estão no [README](../../README.md#executar-os-hosts).

## Camadas de proteção do backoffice

Usuários não administradores não devem conseguir usar o backoffice. A proteção é feita em camadas; as três primeiras estão implementadas:

1. **Implementada (#32):** cada host só conhece os controllers do seu público. Com um token válido, uma rota de backoffice responde 404 na API externa, e uma rota de cliente responde 404 no backoffice.
2. **Implementada (#9):** a política padrão do backoffice exige o papel `Admin` em todo endpoint, mesmo que um controller esqueça de herdar a base, e a `AdminControllerBase` também o exige. Só escapam endpoints marcados com `[AllowAnonymous]`; ainda não há nenhum (o login de administrador e a conclusão de convite chegam com a #12). Um token de `Customer` recebe 403 no backoffice.
3. **Implementada (#9):** cada host aceita só tokens da sua audiência (`aud`): tokens da API externa (`ouroboros-api`) recebem 401 no backoffice, e tokens do backoffice (`ouroboros-backoffice`), 401 na API externa.
4. **Prevista (#12):** o login de backoffice aceita só usuários `Admin`.
5. **Deploy:** o backoffice deve ser publicado só na rede interna ou VPN. Ainda não há ambiente de deploy; a restrição de rede não substitui as camadas anteriores.

Os detalhes da validação dos tokens e das políticas estão em [Autenticação JWT e autorização por políticas](authentication.md). Como ainda não há login, nenhum endpoint emite tokens; sem token, os dois hosts respondem 401 a qualquer rota.

## Alternativas descartadas

- **API única com rotas por público** (decisão anterior, da #3): mais simples, mas expõe o backoffice na mesma superfície pública e torna a separação dependente só de autorização. Foi adequada enquanto não havia funções de backoffice reais; deixou de ser com a necessidade de publicar o backoffice só na rede interna.

## Consequências

- Há dois processos para executar, configurar e, no futuro, publicar.
- Um módulo novo precisa ser registrado no `Program.cs` de cada host que deve expô-lo.
- Os dois hosts compartilham a chave de assinatura dos tokens; a separação entre eles vem da audiência, não da chave.
- A classificação depende de convenção de nome (`AdminControllerBase` em `.Controllers.Admin`), verificada pelos testes de arquitetura.
- O .NET 10 gera um `Program` público em cada host web. Por isso, os testes referenciam o backoffice com `extern alias backoffice` e localizam seu ponto de entrada pelo tipo [`BackofficeApiEntryPoint`](../../src/Ouroboros.Backoffice.Api/BackofficeApiEntryPoint.cs).

## Testes

Em [`tests/Ouroboros.ArchitectureTests`](../../tests/Ouroboros.ArchitectureTests/Ouroboros.ArchitectureTests.csproj):

- [`OuroborosApiFactory.cs`](../../tests/Ouroboros.ArchitectureTests/OuroborosApiFactory.cs): a base `TestHostFactory<TEntryPoint>` sobe um host no ambiente `Testing` com connection string e `SigningKey` fictícias; `OuroborosApiFactory` sobe a API externa e `BackofficeApiFactory`, o backoffice.
- [`HostSeparationTests`](../../tests/Ouroboros.ArchitectureTests/HostSeparationTests.cs): com token válido do público de cada host, uma rota de backoffice responde 200 só no backoffice (404 na externa), uma rota de cliente responde 200 só na externa (404 no backoffice), e um oráculo explícito confere os controllers registrados por host, inclusive o [`MisplacedAdminController`](../../tests/Ouroboros.ArchitectureTests/Misplaced/MisplacedAdminController.cs), cuja base `AdminControllerBase` fica fora de `Controllers.Admin` e, por isso, vai para a API externa.
- [`ControllerRouteTests`](../../tests/Ouroboros.ArchitectureTests/ControllerRouteTests.cs): aplica a regra de rotas à união dos controllers dos dois hosts (veja [Controllers HTTP nos módulos](http-controllers.md#testes-de-arquitetura)).
- [`AuthorizationTests`](../../tests/Ouroboros.ArchitectureTests/AuthorizationTests.cs): camadas 2 e 3, com `Customer` recebendo 403 no backoffice e tokens de audiência cruzada recebendo 401 nos dois sentidos (veja [Autenticação](authentication.md#testes)).

```powershell
dotnet test --project tests/Ouroboros.ArchitectureTests
```

## Origem

- [#3](https://github.com/adnerscarpelini/ouroboros-server/issues/3): API única com rotas por público (substituída).
- [#32](https://github.com/adnerscarpelini/ouroboros-server/issues/32): separação em dois hosts.
- [#9](https://github.com/adnerscarpelini/ouroboros-server/issues/9): autenticação JWT, audiência por host e política padrão do backoffice (camadas 2 e 3).
- [#12](https://github.com/adnerscarpelini/ouroboros-server/issues/12): login de backoffice só para `Admin` (camada 4, pendente).

[Voltar à arquitetura](README.md)
