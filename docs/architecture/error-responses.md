# Respostas de erro com ProblemDetails

## Contexto

Os primeiros endpoints precisam de um formato único de erro. Validações, falhas de negócio e erros inesperados devem ser previsíveis para quem consome a API, sem expor detalhes internos como mensagens de exceção ou stack traces.

## Decisão

- Todo erro HTTP sai em `application/problem+json`, no formato da [RFC 9457: Problem Details for HTTP APIs](https://www.rfc-editor.org/rfc/rfc9457.html), com o suporte nativo do ASP.NET Core.
- O cliente identifica o erro pelo `type`, nunca pelo texto de `title` ou `detail`, que pode mudar.
  - Erros genéricos (validação, rota inexistente, erro inesperado) usam o `type` padrão do ASP.NET Core.
  - Falhas de negócio usam um URI estável por erro, que aponta para o [catálogo de erros](../errors/README.md).
- Toda resposta de erro traz a extensão `traceId`, que liga a resposta aos logs da requisição.
- As falhas de negócio são do módulo, mas a tradução para HTTP fica no controller do host: ele recebe o resultado do caso de uso exposto em `Contracts` e o converte em status HTTP e `type` do catálogo do módulo ([Controllers HTTP nos hosts](http-controllers.md)).

Os dois hosts ([Hosts](hosts.md)) recebem a mesma configuração de [`OuroborosHostExtensions`](../../src/Ouroboros.Hosting/OuroborosHostExtensions.cs), em `Ouroboros.Hosting`: `AddOuroborosHost` e `UseOuroborosHost` configuram:

- `AddProblemDetails`, com `CustomizeProblemDetails` preenchendo `traceId` com `Activity.Current?.Id` ou, sem `Activity`, com `HttpContext.TraceIdentifier`;
- `app.UseExceptionHandler()`, que converte exceções não tratadas em ProblemDetails;
- `app.UseStatusCodePages()`, que dá corpo ProblemDetails às respostas de erro sem corpo.

Desde a #14, o `429` do limite de requisições também sai em ProblemDetails, escrito pelo próprio rate limiter do Identity ([Limite de requisições (429)](#limite-de-requisições-429)).

## Formatos

### Erro inesperado (500)

Uma exceção não tratada responde `500` com título genérico, sem mensagem, tipo de exceção ou stack trace. A exceção completa vai para o log, com o mesmo trace da resposta.

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.6.1",
  "title": "An error occurred while processing your request.",
  "status": 500,
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

### Validação (400)

Controllers com `[ApiController]` validam a entrada antes da action e respondem `400` com o `ValidationProblemDetails` do ASP.NET Core: `errors` traz, por campo, a lista de mensagens.

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "Email": ["The Email field is not a valid e-mail address."]
  },
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

Em `record` posicional, os atributos de validação vão no parâmetro, e não na propriedade gerada:

```csharp
public sealed record SignUpRequest([Required, EmailAddress] string Email); // correto
public sealed record SignUpRequest([property: Required] string Email);     // o ASP.NET lança erro
```

### Falha de negócio

O controller do host traduz o resultado do caso de uso com `Problem(...)`, informando o `type` documentado no catálogo do módulo:

```csharp
return Problem(
    type: "https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/identity.md#email-already-registered",
    title: "E-mail já cadastrado.",
    statusCode: StatusCodes.Status409Conflict);
```

O exemplo é ilustrativo, e o código acima não está no catálogo. A primeira falha de negócio real é a [`invalid-credentials`](../errors/identity.md#invalid-credentials) do login (#12), traduzida pelos `SessionsController` dos dois hosts ([Fazer login com e-mail e senha](../identity/login.md)).

### Respostas sem corpo (401, 403, 404, 405, 415)

Token ausente ou inválido (`401`), falta de papel ou política (`403`), rota ou versão inexistente (`404`), método não permitido (`405`) e media type não suportado (`415`) passam pelo `UseStatusCodePages` e respondem em ProblemDetails, com `traceId`, para clientes que aceitam JSON. Com `Accept: text/html`, o comportamento padrão do ASP.NET Core devolve `text/plain`.

A [política padrão de autorização](authentication.md#política-padrão-dos-hosts) nega por padrão e vale também para requisições sem rota correspondente: sem token, até uma rota ou versão inexistente responde `401`, e não `404` (só as rotas de login e de renovação do access token são anônimas); o `404` só aparece para requisições autenticadas (no backoffice, de um `Admin`). O `401` não explica por que o token foi recusado.

### Limite de requisições (429)

Desde a [#14](https://github.com/adnerscarpelini/ouroboros-server/issues/14), os endpoints anônimos de autenticação têm limite de requisições por IP. Acima do limite, o `OnRejected` do rate limiter, registrado pelo Identity, responde `429` pelo `IProblemDetailsService`, com o `traceId` da mesma configuração, o header `Retry-After` em segundos e um log `Warning` com o caminho e o IP:

```json
{
  "type": "https://www.rfc-editor.org/rfc/rfc6585#section-4",
  "title": "Muitas requisições. Tente novamente mais tarde.",
  "status": 429,
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

O `type` aponta para a [RFC 6585, seção 4](https://www.rfc-editor.org/rfc/rfc6585#section-4), e não para o catálogo, porque o limite não é uma falha de negócio de um módulo. Detalhes em [Limitar requisições de autenticação por IP](../identity/auth-rate-limiting.md).

## Convenção de `type` para falhas de negócio

```text
https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/<module>.md#<codigo>
```

- `<module>` é o nome do módulo em minúsculas, como as pastas de `docs/` (por exemplo, `identity`).
- `<codigo>` é o código do erro em kebab-case, único na página do módulo, e é a âncora da seção que o descreve.
- Um `type` publicado não muda nem é reaproveitado para outro erro. O formato das páginas está no [catálogo de erros](../errors/README.md).

## Correlação com logs

O `traceId` do corpo é o id W3C completo da requisição, no formato `00-<trace-id>-<span-id>-<flags>`. O escopo de log da requisição traz `TraceId` (32 caracteres hexadecimais) e `SpanId`, que correspondem ao segundo e ao terceiro segmentos do `traceId`. Para achar os logs de uma resposta de erro, busque pelo `<trace-id>`.

A formatação dos logs com os scopes visíveis no console fica com a [#28](https://github.com/adnerscarpelini/ouroboros-server/issues/28).

## Comportamento em Development

O handler explícito trata a exceção antes da página de exceção do desenvolvedor. Por isso, também em `Development` a resposta de um erro inesperado é o ProblemDetails genérico; o stack trace aparece apenas no log. A decisão é consciente: o contrato é o mesmo em todos os ambientes e nenhum detalhe interno vaza por configuração de ambiente.

## Alternativas descartadas

- Formato próprio de erro convivendo com ProblemDetails, como o `messages[].codigo` usado em parte da API Pix do Itaú: dois formatos no mesmo contrato obrigam o cliente a tratar ambos, sem ganho sobre as extensões que a RFC 9457 já permite.

## Testes

[`ProblemDetailsTests`](../../tests/Ouroboros.ArchitectureTests/ProblemDetailsTests.cs) usa o `ProblemProbeController` de [`ProbeControllers.cs`](../../tests/Ouroboros.ArchitectureTests/ProbeControllers.cs) e verifica:

- `500` em `application/problem+json`, sem a mensagem nem o tipo da exceção nem stack trace, com o `TraceId` do log de erro contido no `traceId` do corpo (capturado pelo [`CapturingLoggerProvider`](../../tests/Ouroboros.ArchitectureTests/CapturingLoggerProvider.cs));
- `400` com `errors` por campo e `traceId`;
- `409` com o `type` documentado e `traceId`;
- `404` de versão inexistente em ProblemDetails, com `traceId`.

Na raiz do repositório:

```powershell
dotnet test --project tests/Ouroboros.ArchitectureTests
```

## Origem

- [#4](https://github.com/adnerscarpelini/ouroboros-server/issues/4): decisão e implementação.
- [#9](https://github.com/adnerscarpelini/ouroboros-server/issues/9): `401` e `403` da autenticação e da autorização.
- [#47](https://github.com/adnerscarpelini/ouroboros-server/issues/47): tradução das falhas de negócio nos controllers dos hosts.
- [#12](https://github.com/adnerscarpelini/ouroboros-server/issues/12): primeira falha de negócio do catálogo, `invalid-credentials`.
- [#14](https://github.com/adnerscarpelini/ouroboros-server/issues/14): `429` do limite de requisições por IP, com `Retry-After`.

[Voltar à arquitetura](README.md)
