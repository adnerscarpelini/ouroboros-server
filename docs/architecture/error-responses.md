# Respostas de erro com ProblemDetails

## Contexto

Os endpoints usam um formato único de erro. Validações, falhas de negócio e erros inesperados devem ser previsíveis para quem consome a API, sem expor detalhes internos como mensagens de exceção ou stack traces.

## Decisão

- Todo erro HTTP sai em `application/problem+json`, no formato da [RFC 9457: Problem Details for HTTP APIs](https://www.rfc-editor.org/rfc/rfc9457.html), com o suporte nativo do ASP.NET Core.
- O cliente identifica o erro pelo `type`, nunca pelo texto de `title` ou `detail`, que pode mudar.
  - Erros genéricos (validação, rota inexistente, erro inesperado) usam o `type` padrão do ASP.NET Core.
  - Falhas de negócio usam um URI estável por erro, que aponta para o [catálogo de erros](../errors/README.md).
- Toda resposta de erro traz a extensão `traceId`, que liga a resposta aos logs da requisição.
- As falhas de negócio são do módulo, mas a tradução para HTTP fica no controller da API proprietária: ele recebe o resultado do caso de uso exposto em `Contracts` e o converte em status HTTP e `type` do catálogo do módulo ([Controllers HTTP nas APIs dos serviços](http-controllers.md)).

Cada API de serviço configura o tratamento HTTP de erros, com mecanismos técnicos comuns quando úteis:

- `AddProblemDetails`, com `CustomizeProblemDetails` preenchendo `traceId` com `Activity.Current?.Id` ou, sem `Activity`, com `HttpContext.TraceIdentifier`;
- `app.UseExceptionHandler()`, que converte exceções não tratadas em ProblemDetails;
- `app.UseStatusCodePages()`, que dá corpo ProblemDetails às respostas de erro sem corpo. O `429` do limite de requisições também sai em ProblemDetails, escrito pelo próprio rate limiter do Identity ([Limite de requisições (429)](#limite-de-requisições-429)).

## Formatos

### Erro inesperado (500)

Uma exceção não tratada responde `500` com título genérico, sem mensagem, tipo de exceção ou stack trace. O diagnóstico segue o pipeline de [observabilidade sanitizada](microservices.md#operação-e-qualidade), com o mesmo trace da resposta e sem segredos ou dados pessoais.

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

Uma regra conferida pelo caso de uso também pode virar `400` de validação: nas solicitações de [cadastro](../identity/registration.md#e-mail-inválido-400) e de [redefinição de senha](../identity/password-reset.md#e-mail-inválido-400), um e-mail fora da regra estrita de e-mail é devolvido pelo controller com `ModelState.AddModelError` e `ValidationProblem(ModelState)`, com a mensagem `Informe um e-mail válido.` no campo `Email`, no mesmo formato acima.

### Falha de negócio

O controller da API proprietária traduz o resultado do caso de uso com `Problem(...)`, informando o `type` documentado no catálogo do módulo:

```csharp
Response.Headers["WWW-Authenticate"] = "Bearer";
return Problem(
    type: "https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/identity.md#invalid-credentials",
    title: "E-mail ou senha inválidos.",
    statusCode: StatusCodes.Status401Unauthorized);
```

O exemplo usa a falha [`invalid-credentials`](../errors/identity.md#invalid-credentials) do login, traduzida pelo controller da API proprietária ([Fazer login com e-mail e senha](../identity/login.md)).

A conclusão do cadastro traduz dois resultados em `400`: [`invalid-registration-token`](../errors/identity.md#invalid-registration-token), sem `detail` e com a mesma resposta para todos os motivos, e [`password-rejected`](../errors/identity.md#password-rejected), com `detail`, que traz a regra da política de senha violada ([Concluir o cadastro](../identity/registration.md#concluir-o-cadastro)). Os dois se distinguem do `400` de validação pelo `type` e pela ausência de `errors`: o de validação só aparece quando o token ou a senha excedem o tamanho máximo do DTO.

A conclusão da redefinição de senha segue o mesmo padrão: [`invalid-password-reset-token`](../errors/identity.md#invalid-password-reset-token), sem `detail` e igual para todos os motivos, e o mesmo [`password-rejected`](../errors/identity.md#password-rejected), cujo `detail` traz a regra da política violada ou `A nova senha não pode ser igual à atual.` ([Concluir a redefinição](../identity/password-reset.md#concluir-a-redefinição)). O sucesso é `204`, sem corpo.

### Respostas sem corpo (401, 403, 404, 405, 415)

Token ausente ou inválido (`401`), falta de papel ou política (`403`), rota ou versão inexistente (`404`), método não permitido (`405`) e media type não suportado (`415`) passam pelo `UseStatusCodePages` e respondem em ProblemDetails, com `traceId`, para clientes que aceitam JSON. Com `Accept: text/html`, o comportamento padrão do ASP.NET Core devolve `text/plain`.

A [política padrão de autorização](authentication.md) nega por padrão e vale também para requisições sem rota correspondente: sem token, até uma rota ou versão inexistente responde `401`, e não `404` (só as rotas de login, de renovação do access token e de logout e, na API externa, as de solicitação e de conclusão do cadastro e da redefinição de senha são anônimas); o `404` só aparece para requisições autenticadas (no backoffice, de um `Admin`). O `401` não explica por que o token foi recusado.

### Limite de requisições (429)

Os endpoints anônimos de autenticação têm limite de requisições por IP. Acima do limite, o `OnRejected` do rate limiter, registrado pelo Identity, responde `429` pelo `IProblemDetailsService`, com o `traceId` da mesma configuração, o header `Retry-After` em segundos e um log `Warning` com o caminho e o IP:

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

O handler explícito trata a exceção antes da página de exceção do desenvolvedor. Por isso, também em `Development` a resposta de um erro inesperado é o ProblemDetails genérico; o diagnóstico interno recebe o tratamento sanitizado do pipeline de observabilidade. A decisão é consciente: o contrato é o mesmo em todos os ambientes e nenhum detalhe interno vaza por configuração de ambiente.

## Alternativas descartadas

- Formato próprio de erro convivendo com ProblemDetails, como o `messages[].codigo` usado em parte da API Pix do Itaú: dois formatos no mesmo contrato obrigam o cliente a tratar ambos, sem ganho sobre as extensões que a RFC 9457 já permite.

## Testes

[`ProblemDetailsTests`](../../tests/Ouroboros.ArchitectureTests/ProblemDetailsTests.cs) usa o `ProblemProbeController` de [`ProbeControllers.cs`](../../tests/Ouroboros.ArchitectureTests/ProbeControllers.cs) e verifica:

- `500` em `application/problem+json`, sem a mensagem nem o tipo da exceção nem stack trace, com o `TraceId` do log de erro contido no `traceId` do corpo (capturado pelo [`CapturingLoggerProvider`](../../tests/Ouroboros.ArchitectureTests/CapturingLoggerProvider.cs));
- `400` com `errors` por campo e `traceId`;
- `409` com o `type` documentado e `traceId`;
- `404` de versão inexistente em ProblemDetails, com `traceId`.

O comando para executar esses testes está no [guia de desenvolvimento](../development.md#testes).

## Issues relacionadas

- [#4](https://github.com/adnerscarpelini/ouroboros-server/issues/4): decisão e implementação.
- [#9](https://github.com/adnerscarpelini/ouroboros-server/issues/9): `401` e `403` da autenticação e da autorização.
- [#47](https://github.com/adnerscarpelini/ouroboros-server/issues/47): tradução das falhas de negócio nos controllers dos hosts.
- [#12](https://github.com/adnerscarpelini/ouroboros-server/issues/12): falha de negócio do login, `invalid-credentials`.
- [#14](https://github.com/adnerscarpelini/ouroboros-server/issues/14): `429` do limite de requisições por IP, com `Retry-After`.
- [#21](https://github.com/adnerscarpelini/ouroboros-server/issues/21): falhas `invalid-registration-token` e `password-rejected` da conclusão do cadastro, a segunda com `detail`.
- [#22](https://github.com/adnerscarpelini/ouroboros-server/issues/22): solicitação de redefinição de senha com `400` de validação para e-mail inválido e `202` igual para os demais casos, sem falha de negócio própria.
- [#23](https://github.com/adnerscarpelini/ouroboros-server/issues/23): falha `invalid-password-reset-token` da conclusão da redefinição de senha e reuso do `password-rejected`.

[Voltar à arquitetura](README.md)
