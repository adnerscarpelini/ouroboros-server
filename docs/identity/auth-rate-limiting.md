# Limitar requisições de autenticação por IP

## Objetivo

Conter força bruta, credential stuffing e consumo de CPU pelos endpoints anônimos de autenticação. O [bloqueio de conta](account-lockout.md) protege uma conta; o limite por IP protege o servidor contra tentativas espalhadas por muitas contas a partir do mesmo endereço.

Serviço responsável: Identity. O serviço registra o rate limiter e as políticas em `AddIdentityModule`; a API ativa o middleware ([`OuroborosHostExtensions`](../../src/Ouroboros.Hosting/OuroborosHostExtensions.cs)) e aplicam a política em cada action com `[EnableRateLimiting]`. Hoje têm política o [login](login.md), a [renovação do access token](refresh-session.md), o [logout](logout.md), a solicitação e a conclusão do [cadastro de cliente](registration.md) e a solicitação e a conclusão da [redefinição de senha](password-reset.md); o mesmo mecanismo será reutilizado pelos próximos endpoints anônimos.

## Comportamento

- **Rate limiter nativo do ASP.NET Core** (`Microsoft.AspNetCore.RateLimiting`), com janela fixa (`FixedWindowRateLimiter`) por IP e sem fila (`QueueLimit = 0`): acima do limite, a requisição é recusada na hora, sem esperar a próxima janela.
- **Um contador por público, por IP e por política.** A partição é o público do listener (`Public` ou `Admin`) mais o `HttpContext.Connection.RemoteIpAddress`. Cada política tem os próprios contadores: estourar o limite do login não afeta outra política, e um IP que estourou não afeta outro IP.
- **Toda requisição ao endpoint conta**, com sucesso ou não: login bem-sucedido, `401`, `400` de entrada inválida e as próprias recusas dentro da janela.
- **Recusa antes de qualquer trabalho.** O middleware fica antes da autenticação, então o excesso é recusado sem validar token, sem ler o corpo, sem consultar o banco e sem calcular hash de senha.
- **Os dois públicos têm o mesmo limite, com contadores separados.** A API de Identity atende os dois no mesmo processo, mas o escopo do contador é o público do listener que recebeu a requisição ([APIs e workers](../architecture/hosts.md#rate-limiting-e-cabeçalhos-encaminhados)): o mesmo IP tem um contador de login no listener público e outro no administrativo, como quando eram processos distintos. Nos dois, o login usa a mesma política, `identity-login`; a renovação, a `identity-refresh`; e o logout, a `identity-logout`. A solicitação de cadastro e a conclusão, que existem só no listener público, usam a `identity-registration` e a `identity-registration-completion`, e a solicitação e a conclusão da redefinição de senha, também só no público, usam a `identity-password-reset` e a `identity-password-reset-completion`; a configuração é única e valida a lista completa de políticas obrigatórias.

### Resposta acima do limite (429)

```http
HTTP/1.1 429 Too Many Requests
Content-Type: application/problem+json
Retry-After: 42

{
  "type": "https://www.rfc-editor.org/rfc/rfc6585#section-4",
  "title": "Muitas requisições. Tente novamente mais tarde.",
  "status": 429,
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

- O corpo segue o [formato de erro do projeto](../architecture/error-responses.md#limite-de-requisições-429), com `traceId`. O `type` é o da [RFC 6585, seção 4](https://www.rfc-editor.org/rfc/rfc6585#section-4); não é uma falha de negócio do [catálogo](../errors/README.md).
- `Retry-After` traz os segundos inteiros, arredondados para cima, até o fim da janela atual do IP.
- Cada recusa gera um log `Warning` com o caminho e o IP: `Limite de requisições excedido em /api/v1/identity/sessions pelo IP 203.0.113.10.` O log não contém o corpo da requisição.

O cliente deve esperar o `Retry-After` antes de tentar de novo, sem repetir a requisição automaticamente.

## Configuração

Cada política fica em `Identity:RateLimiting:Policies:<nome>`, no [`appsettings.json` da API de Identity](../../src/Services/Identity/Ouroboros.Identity.Api/appsettings.json):

```json
"Identity": {
  "RateLimiting": {
    "Policies": {
      "identity-login": { "PermitLimit": 10, "Window": "00:01:00" },
      "identity-refresh": { "PermitLimit": 30, "Window": "00:01:00" },
      "identity-logout": { "PermitLimit": 30, "Window": "00:01:00" },
      "identity-registration": { "PermitLimit": 5, "Window": "00:01:00" },
      "identity-registration-completion": { "PermitLimit": 10, "Window": "00:01:00" },
      "identity-password-reset": { "PermitLimit": 5, "Window": "00:01:00" },
      "identity-password-reset-completion": { "PermitLimit": 10, "Window": "00:01:00" }
    }
  }
}
```

| Campo | Significado | Login (`identity-login`) | Renovação (`identity-refresh`) | Logout (`identity-logout`) | Cadastro (`identity-registration`) | Conclusão do cadastro (`identity-registration-completion`) | Redefinição de senha (`identity-password-reset`) | Conclusão da redefinição (`identity-password-reset-completion`) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `PermitLimit` | Requisições aceitas por IP dentro de uma janela; inteiro positivo. | `10` | `30` | `30` | `5` | `10` | `5` | `10` |
| `Window` | Duração da janela, no formato `hh:mm:ss` (ou `d.hh:mm:ss`); positiva. | `00:01:00` (1 minuto) | `00:01:00` (1 minuto) | `00:01:00` (1 minuto) | `00:01:00` (1 minuto) | `00:01:00` (1 minuto) | `00:01:00` (1 minuto) | `00:01:00` (1 minuto) |

A política do logout aplica limite por IP, assim como os demais endpoints anônimos de autenticação, com o mesmo valor da renovação. A do cadastro (#20) é a mais restrita porque cada solicitação aceita pode gerar um token, uma gravação e um e-mail. A da conclusão do cadastro (#21) aceita 10 por minuto, o mesmo valor do login; como no login, cada conclusão com token válido e senha aceita pela política calcula um hash de senha. A da solicitação de redefinição de senha (#22) tem o mesmo valor da solicitação de cadastro, pelo mesmo motivo. A da conclusão da redefinição (#23) tem o mesmo valor da conclusão do cadastro: cada conclusão com token válido e senha aceita pela política verifica a senha atual e calcula um hash.

Como qualquer configuração do ASP.NET Core, os valores podem ser sobrescritos por outras fontes, como variáveis de ambiente. Alterar um valor exige reiniciar a API: as políticas são montadas uma vez, na subida.

### Validação na subida

As opções são validadas com `ValidateOnStart` pelo [`AuthRateLimitOptionsValidator`](../../src/Services/Identity/Ouroboros.Identity/Infrastructure/RateLimiting/AuthRateLimitOptions.cs). O host não sobe, com `OptionsValidationException` e uma mensagem que cita a chave, quando:

- uma política usada por algum endpoint (listada em `AuthRateLimitOptions.RequiredPolicies`) não está configurada: `A política 'Identity:RateLimiting:Policies:identity-login' não foi configurada.`;
- `PermitLimit` ou `Window` de qualquer política configurada é zero ou negativo: `'Identity:RateLimiting:Policies:identity-login:PermitLimit' deve ser positivo.`;
- um valor não está no formato esperado, como `PermitLimit = "abc"` ou `Window = "xyz"`: `'Identity:RateLimiting:Policies:identity-login:Window' tem o valor inválido 'xyz'; use o formato hh:mm:ss.`

O formato é conferido na configuração crua, e não nas opções já preenchidas, porque o binder descarta em silêncio uma política cujo valor não converte, e o erro apareceria apenas como política ausente. Os erros de formato são informados primeiro; corrigidos eles, a validação aponta os demais.

### Por que configuração, e não parâmetros do sistema

Os limites do [bloqueio de conta](account-lockout.md#configuração) ficam nos [parâmetros do sistema](../architecture/system-parameters.md), lidos a cada login. Os do rate limiter ficam em configuração porque as políticas do ASP.NET Core são montadas uma vez, na subida, e não seriam relidas a cada requisição; além disso, valores inválidos devem impedir a subida, o que a tabela de parâmetros, lida em tempo de execução, não garante.

## Como adicionar a política de um novo endpoint

Para um endpoint anônimo de autenticação, seguir o mesmo procedimento usado na renovação, no logout, no cadastro e na redefinição de senha:

1. Crie a constante do nome em [`IdentityRateLimitPolicies`](../../src/Services/Identity/Ouroboros.Identity/Contracts/IdentityRateLimitPolicies.cs), em `Contracts`, seguindo o padrão `identity-<endpoint>`.
2. Inclua a constante em `AuthRateLimitOptions.RequiredPolicies`, para que a falta de configuração impeça a subida.
3. Configure `PermitLimit` e `Window` em `Identity:RateLimiting:Policies:<nome>` no `appsettings.json` da API de Identity (a API valida a lista de políticas obrigatórias, mesmo que só um dos públicos exponha o endpoint).
4. Marque a action do controller com `[EnableRateLimiting(IdentityRateLimitPolicies.<Nome>)]` e declare `[ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]`.

```csharp
[HttpPost]
[AllowAnonymous]
[EnableRateLimiting(IdentityRateLimitPolicies.Login)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
public async Task<IActionResult> Create(CreateSessionRequest request, CancellationToken cancellationToken)
```

Cada política configurada vira uma política nomeada com o próprio contador; não é preciso alterar o `AuthRateLimiting`. Nos testes de integração, aumente o limite da nova política na `IntegrationApiFactory`, como já é feito para o login, a renovação, o logout, a solicitação e a conclusão do cadastro e a solicitação e a conclusão da redefinição de senha (veja [Como verificar](#como-verificar)).

## Implementação

| Tipo | Local | Papel |
| --- | --- | --- |
| [`IdentityRateLimitPolicies`](../../src/Services/Identity/Ouroboros.Identity/Contracts/IdentityRateLimitPolicies.cs) | `Contracts` (`public`) | Nomes das políticas usados pelos controllers da API; hoje, `Login = "identity-login"`, `Refresh = "identity-refresh"`, `Logout = "identity-logout"`, `Registration = "identity-registration"`, `RegistrationCompletion = "identity-registration-completion"`, `PasswordReset = "identity-password-reset"` e `PasswordResetCompletion = "identity-password-reset-completion"`. |
| [`AuthRateLimitOptions`, `FixedWindowPolicy` e `AuthRateLimitOptionsValidator`](../../src/Services/Identity/Ouroboros.Identity/Infrastructure/RateLimiting/AuthRateLimitOptions.cs) | `Infrastructure/RateLimiting` (`internal`) | Seção `Identity:RateLimiting`, lista das políticas obrigatórias e validação na subida. |
| [`AuthRateLimiting`](../../src/Services/Identity/Ouroboros.Identity/Infrastructure/RateLimiting/AuthRateLimiting.cs) | `Infrastructure/RateLimiting` (`internal`) | Chamado por `AddIdentityModule`: registra as opções, o `AddRateLimiter` com a resposta `429` (ProblemDetails, `Retry-After` e log) e uma política de janela fixa, por público e IP, para cada política configurada. |
| [`OuroborosHostExtensions.UseOuroborosHost`](../../src/Ouroboros.Hosting/OuroborosHostExtensions.cs) | `Ouroboros.Hosting` | `app.UseRateLimiter()` depois de `UseStatusCodePages()` e antes de `UseAuthentication()`. |
| [`IdentityHttpItems.RateLimitScope`](../../src/Services/Identity/Ouroboros.Identity/Contracts/IdentityHttpItems.cs) | `Contracts` (`public`) | Chave de `HttpContext.Items` com o público do listener, definida pelo pipeline da API ([`IdentityApiHost.UsePipeline`](../../src/Services/Identity/Ouroboros.Identity.Api/IdentityApiHost.cs)) antes do rate limiter e usada na partição do contador. |

Como o `AddRateLimiter` vem do Identity e o `UseRateLimiter` está na configuração comum dos hosts, todo host que use `UseOuroborosHost` precisa registrar o Identity; sem ele, o host não sobe.

## Limitações

- **Contadores em memória, por instância.** Com mais de uma instância da API, cada uma teria os próprios contadores, e o limite efetivo seria multiplicado; seria preciso um armazenamento compartilhado (fora do escopo da #14). Reiniciar a API zera os contadores.
- **Proxy reverso só se configurado.** Sem `Identity:ForwardedHeaders`, o middleware de cabeçalhos encaminhados nem entra no pipeline: o IP é o da conexão (`RemoteIpAddress`) e `X-Forwarded-For` é ignorado, de modo que um cabeçalho forjado não troca o contador. Atrás de um proxy ou balanceador, todos os clientes teriam o IP do proxy e dividiriam o mesmo contador, até que `KnownProxies` (IPs) ou `KnownNetworks` (CIDR) o identifiquem; então `X-Forwarded-For` e `X-Forwarded-Proto` valem, de um salto, apenas se a conexão vier de uma origem listada, e um IP ou CIDR inválido impede a subida. Requisições sem `RemoteIpAddress` (como no `TestServer`) dividem um único contador por público, `desconhecido`.
- **IPv6 conta por endereço.** Um cliente com um bloco `/64` pode alternar endereços e ganhar um contador novo a cada um; não há agrupamento por prefixo.
- **Rajada na virada da janela.** Na janela fixa, um IP pode fazer `PermitLimit` requisições no fim de uma janela e outras `PermitLimit` no início da seguinte, até o dobro do limite em pouco tempo.
- **IPs compartilhados.** Clientes atrás do mesmo NAT (rede corporativa, operadora móvel) dividem o limite.
- **O IP vai para o log** em cada recusa. É o dado necessário para investigar abuso, mas é dado pessoal e deve ser considerado na retenção dos logs.

## Como verificar

Execute a suíte de integração pelo [guia de desenvolvimento](../development.md#testes) (exige Docker).

[`RateLimitingTests`](../../tests/Ouroboros.IntegrationTests/RateLimitingTests.cs), contra o PostgreSQL real, com limites próprios e janela de 1 hora. O IP de cada requisição é simulado pelo header `X-Test-Client-IP`, aplicado por um startup filter que existe só nesses testes (a aplicação não lê esse header):

- com limite 3, as três primeiras tentativas de login recebem `401` e a quarta recebe `429` em `application/problem+json`, com `Retry-After`, o `type` da RFC 6585 e `traceId`;
- a recusa gera log com o caminho e o IP;
- outro IP não é afetado pelo IP que estourou o limite;
- cada política tem o próprio contador: com o login estourado, o mesmo IP ainda é aceito pela política `integration-probe` do `RateLimitedProbeController`, um endpoint anônimo declarado só no assembly de testes;
- o login do listener administrativo também é limitado;
- `PermitLimit` `0` ou `-1` e `Window` `00:00:00` ou `-00:01:00` impedem a subida com mensagem que cita a chave;
- `PermitLimit` `abc` e `Window` `xyz` impedem a subida com a mensagem de valor inválido;
- sem a política do login, o validador falha citando a chave;
- a renovação tem a própria política (`Refresh_has_its_own_policy`): com `identity-refresh` limitada a 1, a segunda renovação do mesmo IP recebe `429`, e o login do mesmo IP continua aceito.

[`ForwardedHeadersTests`](../../tests/Ouroboros.IntegrationTests/ForwardedHeadersTests.cs), com o login limitado a 1 por hora e o IP da conexão simulado pelo mesmo header de teste:

- `X-Forwarded-For` de uma origem sem configuração é ignorado: o segundo login da mesma conexão recebe `429`, mesmo com outro IP no cabeçalho;
- `X-Forwarded-For` de um proxy configurado (`KnownProxies`) identifica o cliente: IPs diferentes no cabeçalho têm contadores diferentes, e o mesmo IP repetido recebe `429`;
- uma conexão fora das `KnownNetworks` configuradas tem o cabeçalho ignorado;
- cada público mantém o próprio contador de login: o mesmo IP que estourou o limite no listener público ainda é aceito no administrativo, que tem o próprio limite.

A política `identity-logout` não tem teste próprio de `429`; usa o mesmo mecanismo das demais, e a sua ausência na configuração impede a subida pela lista `RequiredPolicies`. A `identity-registration` tem: com o limite em 1, a segunda solicitação de cadastro recebe `429` ([Cadastrar cliente por e-mail](registration.md#como-verificar)). A `identity-registration-completion` não tem teste próprio de `429`, pelo mesmo motivo da `identity-logout`. A `identity-password-reset` tem: com o limite em 1, a segunda solicitação de redefinição recebe `429` ([Redefinir a senha por e-mail](password-reset.md#como-verificar)). A `identity-password-reset-completion` não tem teste próprio de `429`, pelo mesmo motivo da `identity-logout`.

No `TestServer`, todas as requisições chegam sem IP e dividiriam o mesmo contador. Por isso, a [`IntegrationApiFactory`](../../tests/Ouroboros.IntegrationTests/IntegrationApiFactory.cs) eleva os limites do login, da renovação, do logout, da solicitação e da conclusão do cadastro e da solicitação de redefinição de senha para 100.000, para não interferir nos demais testes ([Testes de integração](../architecture/integration-tests.md)).

## Referências

- [Rate limiting middleware in ASP.NET Core (Microsoft Learn)](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit)
- [OWASP Authentication Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html)
- [RFC 6585, seção 4: 429 Too Many Requests](https://www.rfc-editor.org/rfc/rfc6585#section-4)

## Issues relacionadas

- [#14](https://github.com/adnerscarpelini/ouroboros-server/issues/14): limitar requisições de autenticação por IP.
- [#15](https://github.com/adnerscarpelini/ouroboros-server/issues/15): política `identity-refresh` da renovação do access token.
- [#17](https://github.com/adnerscarpelini/ouroboros-server/issues/17): política `identity-logout` do logout.
- [#20](https://github.com/adnerscarpelini/ouroboros-server/issues/20): política `identity-registration` da solicitação de cadastro.
- [#21](https://github.com/adnerscarpelini/ouroboros-server/issues/21): política `identity-registration-completion` da conclusão do cadastro.
- [#22](https://github.com/adnerscarpelini/ouroboros-server/issues/22): política `identity-password-reset` da solicitação de redefinição de senha.
- [#23](https://github.com/adnerscarpelini/ouroboros-server/issues/23): política `identity-password-reset-completion` da conclusão da redefinição de senha.

[Voltar à documentação](../README.md)
