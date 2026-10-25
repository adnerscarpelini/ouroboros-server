# Encerrar a sessão atual (logout)

## Objetivo

O usuário encerra a sessão do dispositivo atual, por exemplo ao sair de um computador compartilhado. O logout revoga o refresh token informado, e a sessão deixa de renovar o access token. As outras sessões do usuário (outros logins, outros dispositivos) continuam funcionando; para encerrar todas de uma vez, use o [logout-all](logout-all.md).

Serviço responsável: Identity. A regra fica no caso de uso `ILogout` do serviço; os controllers da API de Identity (um por público) só validam o tamanho da entrada, chamam o caso de uso e traduzem o resultado em resposta HTTP ([Controllers HTTP nas APIs dos serviços](../architecture/http-controllers.md)).

## Rotas

| Listener | Rota |
| --- | --- |
| Público | `POST /api/v1/identity/sessions/logout` |
| Administrativo | `POST /api/v1/admin/identity/sessions/logout` |

As duas rotas são anônimas (`[AllowAnonymous]`): quem chama não envia access token, só o refresh token no corpo. Quem tem o refresh token pode encerrar a sessão dele. Têm limite de requisições por IP com a política `identity-logout` (30 por minuto por IP em cada público, veja [Limite de requisições](#limite-de-requisições-429)).

Duas decisões vão além da issue, que pedia só a rota da API externa (listener público):

- **Rota no listener administrativo**, para que o `Admin` também possa sair.
- **Política de rate limit própria** (`identity-logout`), como as do login e da renovação.

Ao contrário do [login](login.md) e da [renovação](refresh-session.md), o logout não confere o canal: qualquer um dos dois listeners encerra a sessão do token informado, seja de `Customer` ou de `Admin`, porque nenhum token é emitido.

## Pré-requisitos

- O banco local com as migrations do Identity aplicadas ([Desenvolvimento](../development.md#banco-de-dados-local)). O logout não cria migration: usa a coluna `revoked_at` da [renovação](refresh-session.md#persistência).
- A política `identity-logout` configurada em `Identity:RateLimiting:Policies` da API; sem ela, a API não sobe ([Validação na subida](auth-rate-limiting.md#validação-na-subida)).
- Um refresh token, obtido no [login](login.md) ou na última [renovação](refresh-session.md).

## Como usar

Com a API de Identity no ar (listener público em `http://localhost:5141` no perfil padrão) e o refresh token atual de um `Customer`:

```http
POST /api/v1/identity/sessions/logout HTTP/1.1
Host: localhost:5141
Content-Type: application/json

{
  "refreshToken": "q3Jm0f2yQyWm8kV6vY0bq1u2b9c4wJx7kP5sT8nZ1aE"
}
```

Exemplo executável no [guia de desenvolvimento](../development.md#encerrar-a-sessão).

No listener administrativo (`http://localhost:5142`), o pedido é o mesmo, na rota `/api/v1/admin/identity/sessions/logout`. O valor do exemplo é fictício.

Depois do `204`, descarte o access token e o refresh token guardados. Envie sempre o refresh token mais recente, devolvido pela última renovação: um token antigo, já rotacionado, não encerra a sessão ([Limitações](#limitações)).

### Entrada

O corpo é o mesmo da renovação (DTO `RefreshSessionBody`):

| Campo | Regra |
| --- | --- |
| `refreshToken` | O refresh token da sessão a encerrar. Até 256 caracteres. |

- Valor vazio, só com espaços, `null` ou campo ausente responde o mesmo `401` [`invalid-refresh-token`](#token-inválido-401) de um token inexistente, e não `400`.
- Acima de 256 caracteres, responde `400` com `ValidationProblemDetails` ([Respostas de erro](../architecture/error-responses.md#validação-400)).
- Corpo ausente, sem `Content-Type` JSON ou com JSON malformado recebe as respostas padrão do ASP.NET Core (`415` ou `400`, em ProblemDetails).

### Sessão encerrada (204)

Sem corpo. A resposta é a mesma quando o token estava ativo e foi revogado agora e quando já estava revogado ou expirado. Em todos esses casos, o token informado não vale mais.

### Token inválido (401)

```json
{
  "type": "https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/identity.md#invalid-refresh-token",
  "title": "Refresh token inválido.",
  "status": 401,
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

O código [`invalid-refresh-token`](../errors/identity.md#invalid-refresh-token), o mesmo da renovação, responde a token vazio, ausente ou inexistente. Nada é gravado.

### Limite de requisições (429)

Acima de 30 requisições do mesmo IP na mesma janela fixa de 1 minuto, o logout responde `429` em ProblemDetails, com `Retry-After`, antes de ler o corpo ou consultar o banco. O contador é próprio da política `identity-logout`, separado do login e da renovação. Formato, configuração e limitações em [Limitar requisições de autenticação por IP](auth-rate-limiting.md#resposta-acima-do-limite-429).

## Regras

- **Busca pelo hash.** O token recebido é convertido em SHA-256 e buscado pelo índice único `ix_refresh_tokens_token_hash`, como na renovação; o valor em texto nunca é gravado nem comparado.
- **Token ativo é revogado.** Se o token existe, não está revogado e não está expirado, um `UPDATE` condicional (`ExecuteUpdateAsync`, com `revoked_at IS NULL` e não expirado na cláusula `WHERE`) preenche `revoked_at` e `updated_at`. A renovação com esse token passa a responder `401`.
- **Idempotente.** Um token já revogado (por logout, renovação ou reuso) ou expirado responde `204` sem alterar nenhuma linha: o `UPDATE` não encontra o token ativo e afeta 0 linhas.
- **Logout não é reuso.** Apresentar ao logout um token já revogado não revoga a sessão, ao contrário da renovação ([Revogar a sessão ao detectar reuso](refresh-token-reuse.md)). Já a renovação com um token encerrado por logout é tratada como reuso e revoga os tokens ainda ativos da sessão, se houver.
- **Só o token informado.** O logout revoga só o refresh token recebido, que, na sessão em uso, é o único ativo dela. As outras sessões do usuário não são afetadas.
- **Sem checagem do dono nem do canal.** O caso de uso não confere se o usuário está ativo nem o papel dele; basta o token existir.
- **Sessão travada.** A transação do logout começa travando a linha da sessão (pela porta `ISessionStore.LockSessionAsync`, com `SELECT ... FOR UPDATE` em Infrastructure), antes do `UPDATE` do token, como toda escrita em `identity.refresh_tokens` ([Atomicidade e serialização por sessão](refresh-token-reuse.md#atomicidade-e-serialização-por-sessão)). Um logout simultâneo a uma renovação da mesma sessão espera a outra operação terminar.
- **Sem logs.** O caso de uso não escreve log; o refresh token nunca é registrado.

## Caso de uso exposto

| Tipo | Local | Papel |
| --- | --- | --- |
| [`ILogout`](../../src/Services/Identity/Ouroboros.Identity/Contracts/Sessions/ILogout.cs) | `Contracts/Sessions` (`public`) | `ExecuteAsync(LogoutRequest(RefreshToken))`, que retorna `LogoutResult`: `SessionEnded` (o token não vale mais, revogado agora ou antes, ou expirado) ou `InvalidToken` (vazio ou inexistente). |
| [`Logout`](../../src/Services/Identity/Ouroboros.Identity/Application/Sessions/Logout.cs) | `Application/Sessions` (`internal`) | Orquestra a transação e a revogação por `ISessionStore`, registrada como scoped em `AddIdentityModule` ([persistência](session-persistence.md)). |
| [`IdentityDbContext.LockSessionAsync`](../../src/Services/Identity/Ouroboros.Identity/Infrastructure/Persistence/IdentityDbContext.cs) | `Infrastructure/Persistence` (`internal`) | Trava a linha da sessão até o fim da transação atual. É compartilhado pela renovação, pela revogação por reuso e pelo logout. O [logout-all](logout-all.md) (#18) usa o `LockUserSessionsAsync`, que trava todas as sessões do usuário em ordem de id. |
| `SessionsController` [externo](../../src/Services/Identity/Ouroboros.Identity.Api/Controllers/External/SessionsController.cs) e [administrativo](../../src/Services/Identity/Ouroboros.Identity.Api/Controllers/Admin/SessionsController.cs) | `Controllers/External` e `Controllers/Admin` da API de Identity | Action `Logout`, com `[EnableRateLimiting(IdentityRateLimitPolicies.Logout)]`, ao lado do login e da renovação; reutiliza o DTO `RefreshSessionBody` de [`SessionModels.cs`](../../src/Services/Identity/Ouroboros.Identity.Api/Controllers/SessionModels.cs). |

## Limitações

- **O access token já emitido continua válido.** O logout atinge só o refresh token. A validação do access token não consulta a sessão, então o access token em uso vale até expirar, no máximo 15 minutos ([Autenticação](../architecture/authentication.md#emissão-do-access-token)).
- **Logout com um token já rotacionado não encerra a sessão.** Um token antigo, já trocado numa renovação, está revogado: o logout responde `204` sem mudar nada, e o token atual da sessão continua renovando.
- **Risco residual: renovação concorrente com o logout.** Se uma renovação com o mesmo token terminar antes do logout (por exemplo, uma renovação automática em segundo plano disparada junto com o clique em "sair"), o logout encontra o token já rotacionado e responde `204` sem encerrar o token novo, que continua válido. O cliente acredita ter saído, mas a sessão segue ativa com o token devolvido pela renovação. A alternativa seria tratar esse caso como reuso e revogar a sessão inteira, o que muda o critério da issue de que nada muda para um token já revogado. O risco está registrado para decisão do responsável pelo produto. Até lá, o cliente deve cancelar ou aguardar renovações em andamento antes de chamar o logout e enviar o refresh token mais recente.
- **Só a sessão do token informado.** Encerrar todas as sessões do usuário é outro endpoint, autenticado por access token ([Encerrar todas as sessões](logout-all.md), #18).
- **A sessão não muda de estado.** A linha de `identity.sessions` continua lá, sem token ativo; não há coluna de encerramento.
- **Limite por IP, em memória e por instância**, com as mesmas limitações do login ([Limitar requisições](auth-rate-limiting.md#limitações)).

## Como verificar

Execute a suíte de integração pelo [guia de desenvolvimento](../development.md#testes) (exige Docker).

[`LogoutTests`](../../tests/Ouroboros.IntegrationTests/LogoutTests.cs), contra o PostgreSQL real, com um `FakeTimeProvider` para avançar o tempo:

- um token ativo encerra a sessão com `204`, não sobra token ativo, e a renovação com o mesmo token recebe `401` `invalid-refresh-token`;
- um segundo logout com o mesmo token responde `204` e não altera o `revoked_at` gravado no primeiro;
- o logout com um token já rotacionado responde `204` e não revoga a sessão: o token atual continua renovando;
- um token expirado responde `204` e não é revogado;
- token inexistente, vazio, `null` e campo ausente recebem `401` `invalid-refresh-token`;
- com duas sessões do mesmo usuário, encerrar uma não afeta a outra, que continua renovando;
- o logout do listener administrativo encerra uma sessão de `Admin`, e a renovação com o token recebe `401`.

O `400` acima de 256 caracteres e o limite da política `identity-logout` não têm teste próprio: o primeiro vem do `[MaxLength(256)]` do DTO compartilhado com a renovação; o segundo, do mesmo mecanismo testado em [`RateLimitingTests`](auth-rate-limiting.md#como-verificar). Como no `TestServer` todas as requisições chegam do mesmo IP, a [`IntegrationApiFactory`](../../tests/Ouroboros.IntegrationTests/IntegrationApiFactory.cs) eleva também o limite do logout para 100.000.

## Referências

- [OWASP Session Management Cheat Sheet: session expiration](https://cheatsheetseries.owasp.org/cheatsheets/Session_Management_Cheat_Sheet.html#session-expiration)
- [RFC 7009: OAuth 2.0 Token Revocation](https://www.rfc-editor.org/rfc/rfc7009)

## Issues relacionadas

- [#17](https://github.com/adnerscarpelini/ouroboros-server/issues/17): encerrar a sessão atual.
- [#18](https://github.com/adnerscarpelini/ouroboros-server/issues/18): encerrar todas as sessões, em endpoint próprio ([Encerrar todas as sessões](logout-all.md)).
- Depende de [#15](https://github.com/adnerscarpelini/ouroboros-server/issues/15) (renovação com rotação do refresh token) e segue a regra de lock da [#16](https://github.com/adnerscarpelini/ouroboros-server/issues/16) (reuso).

[Voltar à documentação](../README.md)
