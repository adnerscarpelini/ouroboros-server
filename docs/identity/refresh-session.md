# Renovar o access token com rotação do refresh token

## Objetivo

O access token dura 15 minutos. A renovação troca o refresh token entregue no [login](login.md) por um par novo (access token e refresh token), sem pedir a senha de novo. O refresh token é de uso único: cada renovação revoga o token recebido, o que limita o efeito de um vazamento. Apresentar de novo um token já revogado encerra a sessão inteira ([Revogar a sessão ao detectar reuso de refresh token](refresh-token-reuse.md)). O usuário encerra a sessão atual pelo [logout](logout.md), que revoga o refresh token sem emitir outro.

Serviço responsável: Identity. A regra fica no caso de uso `IRefreshSession` do serviço; os controllers dos dois hosts só validam o tamanho da entrada, chamam o caso de uso com o canal do host e traduzem o resultado em resposta HTTP ([Controllers HTTP nos hosts](../architecture/http-controllers.md)).

## Rotas

| Host | Rota | Sessões aceitas | Audiência do token novo |
| --- | --- | --- | --- |
| API externa (`Ouroboros.Api`) | `POST /api/v1/identity/sessions/refresh` | De usuários `Customer` | `ouroboros-api` |
| Backoffice (`Ouroboros.Backoffice.Api`) | `POST /api/v1/admin/identity/sessions/refresh` | De usuários `Admin` | `ouroboros-backoffice` |

As duas rotas são anônimas (`[AllowAnonymous]`): quem chama não envia access token, só o refresh token no corpo. Respondem com `Cache-Control: no-store`, como o login, e têm limite de requisições por IP com a política `identity-refresh` (30 por minuto por IP em cada host, veja [Limite de requisições](#limite-de-requisições-429)).

Cada host renova somente sessões do seu papel, como no login. A checagem de canal impede que um refresh token de `Admin` gere access token com a audiência externa; a rota do backoffice permite renovar a sessão administrativa sem repetir o login a cada 15 minutos.

## Pré-requisitos

- O banco local com as migrations do Identity aplicadas, inclusive a `AddRefreshTokenRevocation`, que cria a coluna `revoked_at` ([Desenvolvimento](../development.md#banco-de-dados-local)). O login também grava essa coluna ao criar o primeiro refresh token da sessão.
- A chave `Authentication:Jwt:SigningKey` configurada ([README](../development.md#chave-de-assinatura-dos-tokens)).
- A política `identity-refresh` configurada em `Identity:RateLimiting:Policies` nos dois hosts; sem ela, o host não sobe ([Validação na subida](auth-rate-limiting.md#validação-na-subida)).
- Um refresh token ativo, obtido no login do mesmo host ([Fazer login com e-mail e senha](login.md)).

## Como usar

Com o backoffice no ar (`http://localhost:5142` no perfil padrão) e um refresh token recebido no login de um `Admin`:

```http
POST /api/v1/admin/identity/sessions/refresh HTTP/1.1
Host: localhost:5142
Content-Type: application/json

{
  "refreshToken": "q3Jm0f2yQyWm8kV6vY0bq1u2b9c4wJx7kP5sT8nZ1aE"
}
```

Exemplo executável no [guia de desenvolvimento](../development.md#renovar-a-sessão).

Na API externa (`http://localhost:5141`), o pedido é o mesmo, na rota `/api/v1/identity/sessions/refresh`, com o refresh token de um `Customer`.

Guarde o par devolvido e descarte o anterior: o refresh token enviado deixa de valer assim que a renovação é concluída. O cliente deve fazer uma renovação por vez com o mesmo token, sem janela de tolerância: renovações simultâneas com o mesmo token têm um único vencedor, as demais recebem `401`, e a sessão inteira é revogada, inclusive o refresh token do vencedor ([Renovações simultâneas](refresh-token-reuse.md#renovações-simultâneas-com-o-mesmo-token)).

### Entrada

| Campo | Regra |
| --- | --- |
| `refreshToken` | O valor recebido no login ou na última renovação. Até 256 caracteres. |

- Valor vazio, só com espaços, `null` ou campo ausente responde o mesmo `401` [`invalid-refresh-token`](#token-inválido-401) de um token inexistente, e não `400`.
- Acima de 256 caracteres, responde `400` com `ValidationProblemDetails` ([Respostas de erro](../architecture/error-responses.md#validação-400)). O limite só protege o servidor de entradas enormes, como no login; o token emitido tem 43 caracteres.
- Corpo ausente, sem `Content-Type` JSON ou com JSON malformado recebe as respostas padrão do ASP.NET Core (`415` ou `400`, em ProblemDetails).

### Resposta de sucesso (200)

O formato é o mesmo do [login](login.md#resposta-de-sucesso-200):

```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "tokenType": "Bearer",
  "accessTokenExpiresAt": "2026-10-23T12:25:00+00:00",
  "refreshToken": "Zx8Lr4pVt0Qm2wN6sB1cY7kD3fH9jG5aE0uT2iO4lP8",
  "refreshTokenExpiresAt": "2026-10-30T12:10:00+00:00"
}
```

- O access token novo tem o mesmo `sid` da sessão aberta no login, `jti` novo e o papel (`role`) lido do banco no momento da renovação, com validade de 15 minutos.
- O refresh token novo tem validade cheia (7 dias, `RefreshTokenLifetime`) contada a partir da renovação, e não do login. Os valores do exemplo são fictícios.

### Token inválido (401)

```json
{
  "type": "https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/identity.md#invalid-refresh-token",
  "title": "Refresh token inválido.",
  "status": 401,
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

O código [`invalid-refresh-token`](../errors/identity.md#invalid-refresh-token) é o mesmo para token vazio, inexistente, já revogado (inclusive por uma renovação simultânea que venceu) ou expirado, para dono inativo e para dono de outro papel (sessão de `Admin` na API externa, de `Customer` no backoffice). Nenhum token é emitido. Quando o token já estava revogado, a sessão dele também é revogada ([reuso](refresh-token-reuse.md)), sem mudança na resposta. O cliente deve descartar os tokens guardados e pedir um novo login.

### Limite de requisições (429)

Acima de 30 requisições do mesmo IP na mesma janela fixa de 1 minuto, a renovação responde `429` em ProblemDetails, com `Retry-After`, antes de ler o corpo ou consultar o banco. O contador é próprio da política `identity-refresh`: estourar o limite da renovação não afeta o do login, e vice-versa. Formato, configuração e limitações em [Limitar requisições de autenticação por IP](auth-rate-limiting.md#resposta-acima-do-limite-429).

### Falha ao gravar (500)

Uma falha inesperada ao gravar o token novo responde `500` ([Respostas de erro](../architecture/error-responses.md)) e desfaz a transação inteira: o token enviado continua válido e pode ser usado de novo.

## Regras

- **Busca pelo hash.** O token recebido é convertido em SHA-256 e buscado pelo índice único `ix_refresh_tokens_token_hash`; o valor em texto nunca é gravado nem comparado.
- **Token ativo.** O token precisa existir, não estar revogado (`revoked_at` nulo) e não estar expirado (`expires_at` posterior ao momento da renovação). Um token expirado recebe `401` e não é revogado.
- **Token revogado é reuso.** Um token encontrado e já revogado revoga todos os tokens ainda não revogados da sessão, numa transação própria, antes das checagens do dono; outras sessões do usuário não são afetadas ([Revogar a sessão ao detectar reuso](refresh-token-reuse.md#comportamento)).
- **Dono ativo e do papel do canal.** Cada controller informa o seu `LoginChannel`, e o caso de uso exige o papel correspondente, pelo mesmo `LoginChannels.RoleOf` do login: `CustomerApp` aceita só `Customer`, e `Backoffice`, só `Admin`. Um dono inativo ou de outro papel recebe `401` sem consumir o token, que volta a funcionar se o usuário for reativado ou se for usado no host certo.
- **Sessão travada.** A transação da renovação começa travando a linha da sessão (`SELECT ... FOR UPDATE`), antes de mexer nos tokens, para que renovação e revogação da mesma sessão não se cruzem ([Atomicidade e serialização por sessão](refresh-token-reuse.md#atomicidade-e-serialização-por-sessão)).
- **Revogar e gravar na mesma transação.** Numa transação, um `UPDATE` condicional (`ExecuteUpdateAsync`, com `revoked_at IS NULL` e não expirado na cláusula `WHERE`) preenche `revoked_at` e `updated_at` do token antigo, e o token novo é inserido na mesma sessão. Se a gravação do novo falhar, a transação é desfeita e a revogação também.
- **Uma renovação simultânea vence.** Se duas requisições trazem o mesmo token ao mesmo tempo, as duas passam pelas checagens, mas só uma atualiza a linha: a segunda espera o lock da sessão e, depois que a primeira confirma, o seu `UPDATE` não encontra mais o token ativo e afeta 0 linhas. Ela desfaz a transação e, como o token foi revogado, revoga a sessão inteira, inclusive o token novo da primeira. Ela recebe `401`, e só um par novo é emitido, com um refresh token que já não renova.
- **Mesma sessão, par novo.** A sessão não muda: o access token novo traz o mesmo `sid`, e o refresh token novo é ligado à mesma linha de `identity.sessions`. Os tokens antigos ficam na tabela, revogados.
- **Papel atual do banco.** O access token novo é emitido a partir do usuário lido do banco na renovação, então reflete o papel gravado; como o canal exige o papel do host, uma mudança de papel faz a renovação seguinte responder `401`.
- **Mesma resposta para toda falha.** O resultado do caso de uso não diz o motivo da falha, e o controller responde sempre o mesmo `401`.
- **Sem segredos nos logs.** Nenhum código do projeto registra o refresh token ou o access token. O caso de uso escreve um único log, o aviso de reuso, só com os ids externos do usuário e da sessão, verificado pelos `RefreshTokenReuseTests` ([Log](refresh-token-reuse.md#log)).

## Caso de uso exposto

| Tipo | Local | Papel |
| --- | --- | --- |
| [`IRefreshSession`](../../src/Services/Identity/Ouroboros.Identity/Contracts/Sessions/IRefreshSession.cs) | `Contracts/Sessions` (`public`) | `ExecuteAsync(RefreshSessionRequest(RefreshToken, Channel))`, que retorna `RefreshSessionResult` (`Tokens`, `Succeeded` e `InvalidToken`, sem motivo da falha). Reutiliza `LoginChannel` e `SessionTokens`, declarados com o [`ILogin`](../../src/Services/Identity/Ouroboros.Identity/Contracts/Sessions/ILogin.cs). |
| [`RefreshSession`](../../src/Services/Identity/Ouroboros.Identity/Application/Sessions/RefreshSession.cs) | `Application/Sessions` (`internal`) | Implementação, registrada como scoped em `AddIdentityModule`. Também orquestra a revogação por reuso (`RevokeSessionAsync`), a transação e o lock pela porta `ISessionStore`; `SessionStore`, em Infrastructure, executa o lock compartilhado com o [logout](logout.md) e as escritas. Veja [Persistência do ciclo de sessões](session-persistence.md). |
| [`LoginChannels`](../../src/Services/Identity/Ouroboros.Identity/Application/Sessions/LoginChannels.cs) | `Application/Sessions` (`internal`) | `RoleOf(channel)`: papel aceito por canal, compartilhado pelo login e pela renovação. |
| [`RefreshToken`](../../src/Services/Identity/Ouroboros.Identity/Domain/Sessions/RefreshToken.cs) | `Domain/Sessions` (`internal`) | `RevokedAt`, `IsActiveAt(now)` (não revogado e não expirado) e `Issue(sessionId, tokenHash, expiresAt)`, que cria o token seguinte de uma sessão já gravada. |
| [`SessionsController` da API externa](../../src/Ouroboros.Api/Controllers/Identity/SessionsController.cs) e [do backoffice](../../src/Ouroboros.Backoffice.Api/Controllers/Identity/SessionsController.cs) | `Controllers/Identity` de cada host | Action `Refresh`, com `[EnableRateLimiting(IdentityRateLimitPolicies.Refresh)]`; o DTO `RefreshSessionBody` e o `type` do erro ficam em `SessionModels.cs`, um por host, ao lado dos DTOs do login. O mesmo DTO é o corpo da action `Logout` ([Encerrar a sessão atual](logout.md)). |

## Persistência

A migration `AddRefreshTokenRevocation` acrescenta uma coluna à tabela `identity.refresh_tokens` ([colunas criadas no login](login.md#persistência)):

| Coluna | Tipo | Observação |
| --- | --- | --- |
| `revoked_at` | `timestamptz`, nulo | Instante em que o token deixou de valer; nulo enquanto ativo. Preenchida pela renovação, pela [detecção de reuso](refresh-token-reuse.md) ([#16](https://github.com/adnerscarpelini/ouroboros-server/issues/16)) pelo [logout](logout.md) ([#17](https://github.com/adnerscarpelini/ouroboros-server/issues/17)) e pelo [logout-all](logout-all.md) ([#18](https://github.com/adnerscarpelini/ouroboros-server/issues/18)), todos travando antes a sessão ([regra](refresh-token-reuse.md#atomicidade-e-serialização-por-sessão)). |

Uma sessão acumula um refresh token por renovação: só o último está ativo, e os anteriores ficam com `revoked_at` preenchido. Depois de um reuso, nenhum fica ativo.

## Limitações

- **Sem tolerância a renovações concorrentes.** Um cliente que envie duas renovações com o mesmo token perde a sessão ([Revogar a sessão ao detectar reuso](refresh-token-reuse.md#limitações)).
- **Sessão sem expiração por inatividade.** A sessão não tem estado de encerramento nem expiração por inatividade (fora do escopo): enquanto houver renovação a cada 7 dias, ela continua, até o [logout](logout.md) ([#17](https://github.com/adnerscarpelini/ouroboros-server/issues/17)) ou o [logout-all](logout-all.md) ([#18](https://github.com/adnerscarpelini/ouroboros-server/issues/18)), que encerra todas as sessões do usuário.
- **Renovação concorrente com o logout.** Uma renovação que termine antes de um logout com o mesmo token deixa a sessão ativa com o token novo, e o logout responde `204` ([Encerrar a sessão atual](logout.md#limitações)).
- **O access token anterior continua válido.** A renovação não invalida o access token emitido antes; a validação do token não consulta a sessão, então ele vale até expirar ([Autenticação](../architecture/authentication.md#emissão-do-access-token)).
- **Checagem do usuário antes da transação.** O usuário ativo e o papel são verificados antes de abrir a transação. Um usuário desativado ou com o papel alterado nos milissegundos entre a checagem e o `UPDATE` ainda recebe o par novo. Essa janela é uma limitação conhecida; o par novo deixa de renovar na chamada seguinte.
- **Tokens revogados e expirados não são apagados.** Não há limpeza das linhas antigas de `identity.refresh_tokens`; a tabela cresce com cada renovação.
- **Limite por IP, em memória e por instância**, com as mesmas limitações do login ([Limitar requisições](auth-rate-limiting.md#limitações)).

## Como verificar

Execute a suíte de integração pelo [guia de desenvolvimento](../development.md#testes) (exige Docker).

[`RefreshSessionTests`](../../tests/Ouroboros.IntegrationTests/RefreshSessionTests.cs), contra o PostgreSQL real, com um `FakeTimeProvider` para avançar o tempo:

- um token ativo renovado depois de 10 minutos responde `200` com `Cache-Control: no-store`, par novo e o mesmo `sid`; o token antigo fica com `revoked_at` preenchido e o novo, nulo; o refresh token novo expira 7 dias depois da renovação; continua havendo uma sessão, agora com dois refresh tokens;
- o token já rotacionado não pode ser usado de novo (a revogação da sessão nesse caso é coberta pelos [`RefreshTokenReuseTests`](refresh-token-reuse.md#como-verificar));
- token inexistente, vazio, só com espaços, `null` e campo ausente recebem `401` `invalid-refresh-token`, sem tokens no corpo;
- um token expirado recebe `401` e não é revogado;
- um usuário inativo recebe `401` sem consumir o token, que volta a renovar depois da reativação;
- uma falha ao gravar o token novo, provocada por um trigger no banco, responde `500`, não revoga o antigo nem grava o novo; sem o trigger, o mesmo token renova;
- o access token novo traz o papel e o `sub` do banco; com o papel alterado para `Admin`, a API externa responde `401` e não consome o token;
- uma sessão de `Admin` não renova pela API externa (sem consumir o token) e renova pelo backoffice, com audiência `ouroboros-backoffice` e papel `Admin`;
- uma sessão de `Customer` não renova pelo backoffice;
- cinco renovações simultâneas com o mesmo token têm um único `200`, as demais recebem `401`, e só um token novo é gravado.

Em [`RateLimitingTests`](../../tests/Ouroboros.IntegrationTests/RateLimitingTests.cs), `Refresh_has_its_own_policy` confere que, com a política `identity-refresh` limitada a 1, a segunda renovação do mesmo IP recebe `429` e o login do mesmo IP continua aceito. Como no `TestServer` todas as requisições chegam do mesmo IP, a [`IntegrationApiFactory`](../../tests/Ouroboros.IntegrationTests/IntegrationApiFactory.cs) eleva também o limite da renovação para 100.000.

## Referências

- [RFC 9700, seção 2.2.2: refresh tokens](https://www.rfc-editor.org/rfc/rfc9700.html#section-2.2.2)
- [RFC 9700, seção 4.14: proteção de refresh tokens](https://www.rfc-editor.org/rfc/rfc9700.html#section-4.14)
- [RFC 6749, seção 5.1: resposta de token](https://www.rfc-editor.org/rfc/rfc6749#section-5.1)

## Issues relacionadas

- [#15](https://github.com/adnerscarpelini/ouroboros-server/issues/15): renovar o access token com rotação do refresh token.
- [#16](https://github.com/adnerscarpelini/ouroboros-server/issues/16): revogar a sessão ao detectar reuso, com lock da sessão na renovação ([Revogar a sessão ao detectar reuso de refresh token](refresh-token-reuse.md)).
- [#17](https://github.com/adnerscarpelini/ouroboros-server/issues/17): logout com o mesmo corpo da renovação e lock da sessão compartilhado pelo `IdentityDbContext` ([Encerrar a sessão atual](logout.md)).
- [#18](https://github.com/adnerscarpelini/ouroboros-server/issues/18): logout-all revoga os refresh tokens de todas as sessões do usuário, travando-as em ordem de id ([Encerrar todas as sessões](logout-all.md)).

[Voltar à documentação](../README.md)
