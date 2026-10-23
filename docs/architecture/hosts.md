# Hosts da API externa e do backoffice

## Contexto

Na #3, o Ouroboros adotou uma API única, com rotas separadas por público (`api/v1/<module>` para o cliente e `api/v1/admin/<module>` para o backoffice). Funções de backoffice, como alterar parâmetros, criar administradores e cadastrar clients de integração, não devem ficar expostas na mesma API pública usada pelo app e por integrações de terceiros: interfaces administrativas são procuradas por caminhos previsíveis como `/admin` ([OWASP WSTG-CONF-05](https://owasp.org/www-project-web-security-testing-guide/stable/4-Web_Application_Security_Testing/02-Configuration_and_Deployment_Management_Testing/05-Enumerate_Infrastructure_and_Application_Admin_Interfaces)). Um host próprio permite publicar o backoffice só na rede interna.

## Decisão

Esta decisão substitui a "API única" registrada na #3. O Ouroboros tem dois hosts ASP.NET Core, um por público, no espírito do padrão [Backends for Frontends](https://learn.microsoft.com/en-us/azure/architecture/patterns/backends-for-frontends):

| Projeto | Público | Controllers registrados | Portas em desenvolvimento |
| --- | --- | --- | --- |
| [`Ouroboros.Api`](../../src/Ouroboros.Api/Program.cs) | Externo: app do cliente, endpoints anônimos e integrações de terceiros. | Os controllers do próprio projeto, em `Controllers/<Module>/`. | `http://localhost:5141` (perfil `http`); `https://localhost:7135` no perfil `https`. |
| [`Ouroboros.Backoffice.Api`](../../src/Ouroboros.Backoffice.Api/Program.cs) | Interno: backoffice. | Os controllers do próprio projeto, em `Controllers/<Module>/`. | `http://localhost:5142` (perfil `http`); `https://localhost:7136` no perfil `https`. |

- Desde a #47, os controllers são declarados nos próprios hosts, organizados por módulo, e chamam os casos de uso expostos em `Contracts` dos módulos, conforme [Controllers HTTP nos hosts](http-controllers.md). A separação entre os públicos é por projeto: cada host usa a descoberta padrão do ASP.NET Core e só conhece os próprios controllers. Até a #47, os controllers ficavam nos módulos e um provider em `Ouroboros.Hosting` escolhia os de cada host ([histórico](http-controllers.md#histórico)).
- As rotas mantêm os prefixos `api/v{version:apiVersion}/<module>/...` (externa) e `api/v{version:apiVersion}/admin/<module>/...` (backoffice), com o [versionamento da API](api-versioning.md).
- Cada host registra os módulos no próprio `Program.cs` (`Add<Module>Module`), para que a composição de cada um fique visível ali. Hoje os dois registram os cinco módulos (Identity, Settings, Customers, Accounts e Transactions).
- A configuração HTTP comum fica no projeto [`Ouroboros.Hosting`](../../src/Ouroboros.Hosting/OuroborosHostExtensions.cs), uma class library que não referencia nenhum módulo.

### Ouroboros.Hosting

- `HostAudience`: o público do host, `External` ou `Backoffice`, que define a política padrão de autorização. A audiência dos tokens não vem dele, e sim de `Authentication:Jwt:Audience` no `appsettings.json` de cada host ([Autenticação](authentication.md#configuração)).
- `OuroborosHostExtensions.AdminRole`: o papel `Admin`, exigido pela política padrão do backoffice.
- `OuroborosHostExtensions`:
  - `builder.AddOuroborosHost(audience)`: registra os controllers com a descoberta padrão (`AddControllers()`), o [versionamento por URL](api-versioning.md), o [ProblemDetails](error-responses.md) com `traceId` e a [política padrão de autorização](authentication.md#política-padrão-dos-hosts) do público: usuário autenticado na API externa; autenticado e com o papel `Admin` no backoffice.
  - `app.UseOuroborosHost()`: `UseExceptionHandler()`, `UseStatusCodePages()`, `UseRateLimiter()`, `UseAuthentication()`, `UseAuthorization()` e `MapControllers()`.

O esquema JwtBearer e a validação dos tokens são registrados pelo Identity em `AddIdentityModule`, que os dois hosts chamam ([Autenticação](authentication.md#validação-do-token)). O mesmo vale para o rate limiter (`AddRateLimiter`) e suas políticas, desde a #14 ([Limitar requisições de autenticação por IP](../identity/auth-rate-limiting.md)).

### Ordem do pipeline

1. `UseExceptionHandler()` e `UseStatusCodePages()`: toda resposta de erro, inclusive das etapas seguintes, sai em [ProblemDetails](error-responses.md).
2. `UseRateLimiter()`: aplica as políticas declaradas por endpoint com `[EnableRateLimiting]`. Fica antes da autenticação para que o excesso seja recusado com `429` sem validar token, ler o corpo nem chegar ao controller. Endpoints sem a anotação não são limitados.
3. `UseAuthentication()` e `UseAuthorization()`: validação do token e [política padrão](authentication.md#política-padrão-dos-hosts) do host.
4. `MapControllers()`.

Como o `UseRateLimiter()` exige os serviços do `AddRateLimiter`, registrados pelo Identity, um host que use `UseOuroborosHost` precisa registrar o Identity; sem ele, o host não sobe.

O [`Program.cs` da API externa](../../src/Ouroboros.Api/Program.cs) fica assim (o do backoffice muda o público e, antes de `UseOuroborosHost`, desvia para os [comandos de operação](#comandos-de-operação)):

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddOuroborosHost(HostAudience.External);

builder.Services.AddIdentityModule();
builder.Services.AddSettingsModule();
builder.Services.AddCustomersModule();
builder.Services.AddAccountsModule();
builder.Services.AddTransactionsModule();

var app = builder.Build();
app.UseOuroborosHost();
app.Run();
```

### Comandos de operação

Desde a [#11](https://github.com/adnerscarpelini/ouroboros-server/issues/11), o `Ouroboros.Backoffice.Api` também roda comandos de operação, executados por quem opera a aplicação, em `CommandLine/`. Hoje há um, o [`create-admin`](../../src/Ouroboros.Backoffice.Api/CommandLine/CreateAdminCommand.cs), que cria o primeiro administrador ([Criar o primeiro administrador](../identity/first-admin.md)):

```powershell
dotnet run --project src/Ouroboros.Backoffice.Api -- create-admin --email <e-mail>   # senha em OUROBOROS_ADMIN_PASSWORD
```

- O `Program.cs` monta a aplicação normalmente (`builder.Build()`, com os módulos e a configuração do host) e, se o primeiro argumento for o nome de um comando, executa-o e retorna o código de saída, sem chamar `UseOuroborosHost` nem `Run`: nenhuma porta é aberta. Sem comando, o host sobe como antes.
- O comando fica no host porque é uma entrada da aplicação, como um controller: lê argumentos e variáveis de ambiente, chama o caso de uso exposto em `Contracts` do módulo e traduz o resultado em saída e código de saída. A regra fica no módulo.
- Os comandos ficam só no backoffice, o host interno; a API externa não os aceita.
- Segredos, como a senha do administrador, vêm de variável de ambiente, nunca de argumento.

### Configuração

- `Ouroboros.Backoffice.Api` usa o mesmo `UserSecretsId` da `Ouroboros.Api`: em desenvolvimento, os dois leem a mesma connection string `ConnectionStrings:Ouroboros` dos user-secrets. Fora de desenvolvimento, ela vem da variável `ConnectionStrings__Ouroboros` de cada processo. O mesmo vale para a chave de assinatura dos tokens, `Authentication:Jwt:SigningKey` (variável `Authentication__Jwt__SigningKey`), compartilhada pelos dois hosts; sem ela, nenhum host sobe ([README](../../README.md#chave-de-assinatura-dos-tokens)).
- Emissor, audiência e tempos de vida dos tokens ficam em `Authentication:Jwt` no `appsettings.json` de cada host; só a audiência difere (`ouroboros-api` e `ouroboros-backoffice`).
- Os limites de requisições por IP ficam em `Identity:RateLimiting:Policies` no `appsettings.json` de cada host, com os mesmos valores nos dois (inclusive as políticas `identity-registration`, `identity-registration-completion` e `identity-password-reset`, que o backoffice configura sem usar, porque valida a mesma lista de políticas obrigatórias); cada host tem os próprios contadores ([Limitar requisições](../identity/auth-rate-limiting.md#configuração)).
- As migrations continuam usando `Ouroboros.Api` como startup project (veja o [README](../../README.md#banco-de-dados-local)).
- Os dois hosts podem rodar ao mesmo tempo; os comandos estão no [README](../../README.md#executar-os-hosts).

## Camadas de proteção do backoffice

Usuários não administradores não devem conseguir usar o backoffice. A proteção é feita em camadas; as quatro primeiras estão implementadas:

1. **Implementada (#32, #47):** cada host só conhece os controllers do seu público, que ficam no seu próprio projeto. Uma rota de backoffice não existe na API externa (com token válido, responde 404), e vice-versa.
2. **Implementada (#9):** a política padrão do backoffice exige o papel `Admin` em todo endpoint, sem depender de base de controller nem de anotação na action. Só escapam endpoints marcados com `[AllowAnonymous]`; hoje, só o login de administrador (`POST /api/v1/admin/identity/sessions`, #12), a renovação do access token (`POST /api/v1/admin/identity/sessions/refresh`, #15) e o logout (`POST /api/v1/admin/identity/sessions/logout`, #17). O logout-all do backoffice (`POST /api/v1/admin/identity/sessions/logout-all`, #18) e a consulta do próprio usuário (`GET /api/v1/admin/identity/me`, #19) não são anônimos e seguem essa política, sem anotação própria. Um token de `Customer` recebe 403 no backoffice.
3. **Implementada (#9):** cada host aceita só tokens da sua audiência (`aud`): tokens da API externa (`ouroboros-api`) recebem 401 no backoffice, e tokens do backoffice (`ouroboros-backoffice`), 401 na API externa.
4. **Implementada (#12):** o login de backoffice aceita só usuários `Admin`; um `Customer` com as credenciais corretas recebe o mesmo `401` de senha errada, e o token emitido tem a audiência `ouroboros-backoffice` ([Fazer login com e-mail e senha](../identity/login.md)). Desde a #15, a renovação segue a mesma regra: o backoffice só renova sessões de `Admin`, e a API externa, só de `Customer` ([Renovar o access token](../identity/refresh-session.md)). O logout (#17) não confere o canal, porque não emite token: quem tem o refresh token encerra a sessão dele em qualquer host ([Encerrar a sessão atual](../identity/logout.md)). O logout-all (#18) exige access token, então segue as camadas 2 e 3: no backoffice, só um `Admin` encerra as próprias sessões, e um token de `Customer` da API externa recebe 401 ([Encerrar todas as sessões](../identity/logout-all.md)). A consulta do próprio usuário (#19) vai além das camadas 2 e 3: confere no banco o papel do canal, então um `Admin` rebaixado a `Customer` recebe 401 em `GET /api/v1/admin/identity/me` antes de o token expirar ([Consultar o próprio usuário](../identity/me.md)).
5. **Deploy:** o backoffice deve ser publicado só na rede interna ou VPN. Ainda não há ambiente de deploy; a restrição de rede não substitui as camadas anteriores.

Os detalhes da validação dos tokens e das políticas estão em [Autenticação JWT e autorização por políticas](authentication.md). Os tokens são emitidos pelo login de cada host, com a audiência do host; sem token, os dois hosts respondem 401 a qualquer rota, inclusive ao logout-all e à rota `me`, exceto o login, a renovação e o logout e, na API externa, a solicitação e a conclusão do cadastro (`POST /api/v1/identity/registrations`, #20, e `POST /api/v1/identity/registrations/complete`, #21), anônimas e só desse host, porque administradores não se cadastram sozinhos ([Cadastrar cliente por e-mail](../identity/registration.md)), e a solicitação de redefinição de senha (`POST /api/v1/identity/password-resets`, #22), também anônima e só desse host, que envia o link só a contas `Customer` ativas ([Redefinir a senha por e-mail](../identity/password-reset.md)).

## Alternativas descartadas

- **API única com rotas por público** (decisão anterior, da #3): mais simples, mas expõe o backoffice na mesma superfície pública e torna a separação dependente só de autorização. Foi adequada enquanto não havia funções de backoffice reais; deixou de ser com a necessidade de publicar o backoffice só na rede interna.

## Consequências

- Há dois processos para executar, configurar e, no futuro, publicar.
- Um módulo novo precisa ser registrado no `Program.cs` de cada host que deve expô-lo.
- Todo host precisa registrar o Identity, porque o `UseRateLimiter()` da configuração comum depende do `AddRateLimiter` registrado por ele.
- Os dois hosts compartilham a chave de assinatura dos tokens; a separação entre eles vem da audiência, não da chave.
- Um controller pertence ao host em cujo projeto é declarado; não há convenção de herança para classificá-lo. A pasta, o namespace e o prefixo de rota de cada host são verificados pelos testes de arquitetura.
- O .NET 10 gera um `Program` público em cada host web. Por isso, os testes referenciam o backoffice com `extern alias backoffice` e localizam seu ponto de entrada pelo tipo [`BackofficeApiEntryPoint`](../../src/Ouroboros.Backoffice.Api/BackofficeApiEntryPoint.cs).

## Testes

Em [`tests/Ouroboros.ArchitectureTests`](../../tests/Ouroboros.ArchitectureTests/Ouroboros.ArchitectureTests.csproj):

- [`OuroborosApiFactory.cs`](../../tests/Ouroboros.ArchitectureTests/OuroborosApiFactory.cs): a base `TestHostFactory<TEntryPoint>` sobe um host no ambiente `Testing` com connection string e `SigningKey` fictícias; `OuroborosApiFactory` sobe a API externa e `BackofficeApiFactory`, o backoffice.
- [`HostSeparationTests`](../../tests/Ouroboros.ArchitectureTests/HostSeparationTests.cs): nenhum módulo declara controllers nem é application part de um host; a API externa não registra controllers do backoffice, e vice-versa; todo controller dos hosts é `public` (detalhes em [Controllers HTTP nos hosts](http-controllers.md#testes-de-arquitetura)).
- [`ControllerRouteTests`](../../tests/Ouroboros.ArchitectureTests/ControllerRouteTests.cs): um teste por host verifica namespace `<Host>.Controllers.<Module>`, `[ApiVersion]` e o prefixo de rota do público (veja [Controllers HTTP nos hosts](http-controllers.md#testes-de-arquitetura)).
- [`AuthorizationTests`](../../tests/Ouroboros.ArchitectureTests/AuthorizationTests.cs): camadas 2 e 3, com `Customer` recebendo 403 no backoffice e tokens de audiência cruzada recebendo 401 nos dois sentidos (veja [Autenticação](authentication.md#testes)).

```powershell
dotnet test --project tests/Ouroboros.ArchitectureTests
```

## Origem

- [#3](https://github.com/adnerscarpelini/ouroboros-server/issues/3): API única com rotas por público (substituída).
- [#32](https://github.com/adnerscarpelini/ouroboros-server/issues/32): separação em dois hosts.
- [#47](https://github.com/adnerscarpelini/ouroboros-server/issues/47): controllers declarados nos hosts, substituindo a descoberta por público.
- [#9](https://github.com/adnerscarpelini/ouroboros-server/issues/9): autenticação JWT, audiência por host e política padrão do backoffice (camadas 2 e 3).
- [#11](https://github.com/adnerscarpelini/ouroboros-server/issues/11): comandos de operação no backoffice (`create-admin`).
- [#12](https://github.com/adnerscarpelini/ouroboros-server/issues/12): login de backoffice só para `Admin` (camada 4).
- [#15](https://github.com/adnerscarpelini/ouroboros-server/issues/15): renovação do access token no backoffice só para sessões de `Admin`.
- [#17](https://github.com/adnerscarpelini/ouroboros-server/issues/17): logout anônimo também no backoffice, para o `Admin` poder sair.
- [#18](https://github.com/adnerscarpelini/ouroboros-server/issues/18): logout-all também no backoffice, autenticado com token de `Admin`.
- [#19](https://github.com/adnerscarpelini/ouroboros-server/issues/19): consulta do próprio usuário também no backoffice, com o papel `Admin` conferido no banco.
- [#20](https://github.com/adnerscarpelini/ouroboros-server/issues/20): solicitação de cadastro anônima só na API externa.
- [#21](https://github.com/adnerscarpelini/ouroboros-server/issues/21): conclusão do cadastro anônima só na API externa; o usuário criado é sempre `Customer`, que o login do backoffice recusa.
- [#22](https://github.com/adnerscarpelini/ouroboros-server/issues/22): solicitação de redefinição de senha anônima só na API externa, sem recuperação de contas de backoffice por ela.
- [#14](https://github.com/adnerscarpelini/ouroboros-server/issues/14): `UseRateLimiter()` no pipeline comum, antes da autenticação.

[Voltar à arquitetura](README.md)
