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
- `CurrentHost`: registro singleton na DI com o público do host, para quem precisar saber em qual host está rodando (por exemplo, a futura audiência dos tokens).
- [`AudienceControllerFeatureProvider`](../../src/Ouroboros.Hosting/AudienceControllerFeatureProvider.cs) (`internal`): substitui o `ControllerFeatureProvider` padrão. Aceita qualquer classe concreta, não genérica, derivada de `ControllerBase` e sem `[NonController]`, inclusive `internal`, e filtra pelo público. Um controller é de backoffice se alguma classe na sua cadeia de herança se chama `AdminControllerBase` e está num namespace terminado em `.Controllers.Admin`. O backoffice registra só esses; a API externa registra todos os demais. Uma base chamada `AdminControllerBase` fora de `Controllers.Admin` não conta: o controller vai para a API externa.
- `OuroborosHostExtensions`:
  - `builder.AddOuroborosHost(audience)`: registra o `CurrentHost`, os controllers com o provider acima, o [versionamento por URL](api-versioning.md) e o [ProblemDetails](error-responses.md) com `traceId`.
  - `app.UseOuroborosHost()`: `UseExceptionHandler()`, `UseStatusCodePages()` e `MapControllers()`.

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

- `Ouroboros.Backoffice.Api` usa o mesmo `UserSecretsId` da `Ouroboros.Api`: em desenvolvimento, os dois leem a mesma connection string `ConnectionStrings:Ouroboros` dos user-secrets. Fora de desenvolvimento, ela vem da variável `ConnectionStrings__Ouroboros` de cada processo.
- As migrations continuam usando `Ouroboros.Api` como startup project (veja o [README](../../README.md#banco-de-dados-local)).
- Os dois hosts podem rodar ao mesmo tempo; os comandos estão no [README](../../README.md#executar-os-hosts).

## Camadas de proteção do backoffice

Usuários não administradores não devem conseguir usar o backoffice. A proteção é prevista em camadas, e só a primeira existe hoje:

1. **Implementada:** cada host só conhece os controllers do seu público. Uma rota de backoffice responde 404 na API externa, e uma rota de cliente responde 404 no backoffice.
2. **Prevista (#9/#12):** política global no backoffice que exige o papel `Admin` em todo endpoint, mesmo que um controller esqueça de herdar a base; só endpoints anônimos marcados explicitamente (login de administrador e conclusão de convite) escapam dela.
3. **Prevista (#9/#12):** cada host aceita só tokens da sua audiência (`aud`): tokens da API externa são recusados no backoffice, e tokens de administrador, na API externa.
4. **Prevista (#12):** o login de backoffice aceita só usuários `Admin`.
5. **Deploy:** o backoffice deve ser publicado só na rede interna ou VPN. Ainda não há ambiente de deploy; a restrição de rede não substitui as camadas anteriores.

Até a #9, nenhum dos hosts tem autenticação: quem alcança a porta do backoffice alcança seus endpoints.

## Alternativas descartadas

- **API única com rotas por público** (decisão anterior, da #3): mais simples, mas expõe o backoffice na mesma superfície pública e torna a separação dependente só de autorização. Foi adequada enquanto não havia funções de backoffice reais; deixou de ser com a necessidade de publicar o backoffice só na rede interna.

## Consequências

- Há dois processos para executar, configurar e, no futuro, publicar.
- Um módulo novo precisa ser registrado no `Program.cs` de cada host que deve expô-lo.
- A classificação depende de convenção de nome (`AdminControllerBase` em `.Controllers.Admin`), verificada pelos testes de arquitetura.
- O .NET 10 gera um `Program` público em cada host web. Por isso, os testes referenciam o backoffice com `extern alias backoffice` e localizam seu ponto de entrada pelo tipo [`BackofficeApiEntryPoint`](../../src/Ouroboros.Backoffice.Api/BackofficeApiEntryPoint.cs).

## Testes

Em [`tests/Ouroboros.ArchitectureTests`](../../tests/Ouroboros.ArchitectureTests/Ouroboros.ArchitectureTests.csproj):

- [`OuroborosApiFactory.cs`](../../tests/Ouroboros.ArchitectureTests/OuroborosApiFactory.cs): a base `TestHostFactory<TEntryPoint>` sobe um host no ambiente `Testing` com connection string fictícia; `OuroborosApiFactory` sobe a API externa e `BackofficeApiFactory`, o backoffice.
- [`HostSeparationTests`](../../tests/Ouroboros.ArchitectureTests/HostSeparationTests.cs): uma rota de backoffice responde 200 só no backoffice (404 na externa), uma rota de cliente responde 200 só na externa (404 no backoffice), e um oráculo explícito confere os controllers registrados por host, inclusive o [`MisplacedAdminController`](../../tests/Ouroboros.ArchitectureTests/Misplaced/MisplacedAdminController.cs), cuja base `AdminControllerBase` fica fora de `Controllers.Admin` e, por isso, vai para a API externa.
- [`ControllerRouteTests`](../../tests/Ouroboros.ArchitectureTests/ControllerRouteTests.cs): aplica a regra de rotas à união dos controllers dos dois hosts (veja [Controllers HTTP nos módulos](http-controllers.md#testes-de-arquitetura)).

```powershell
dotnet test --project tests/Ouroboros.ArchitectureTests
```

## Origem

- [#3](https://github.com/adnerscarpelini/ouroboros-server/issues/3): API única com rotas por público (substituída).
- [#32](https://github.com/adnerscarpelini/ouroboros-server/issues/32): separação em dois hosts.
- [#9](https://github.com/adnerscarpelini/ouroboros-server/issues/9) e [#12](https://github.com/adnerscarpelini/ouroboros-server/issues/12): autenticação, audiência dos tokens e login de backoffice (camadas 2 a 4).

[Voltar à arquitetura](README.md)
