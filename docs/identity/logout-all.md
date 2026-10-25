# Encerrar todas as sessões (logout-all)

## Objetivo

Quem suspeita que a conta foi usada em outro dispositivo encerra todas as sessões de uma vez. O logout-all revoga todos os refresh tokens ativos do usuário autenticado, em todas as sessões, inclusive a atual: nenhuma delas volta a renovar o access token. Para encerrar só a sessão do dispositivo atual, use o [logout](logout.md).

Serviço responsável: Identity. A regra fica no caso de uso `ILogoutAll` do serviço; os controllers da API de Identity (um por público) só leem o usuário do access token, chamam o caso de uso e respondem `204` ([Controllers HTTP nas APIs dos serviços](../architecture/http-controllers.md)).

## Rotas

| Listener | Rota | Access token aceito |
| --- | --- | --- |
| Público | `POST /api/v1/identity/sessions/logout-all` | De `Customer`, audiência `ouroboros-api` |
| Administrativo | `POST /api/v1/admin/identity/sessions/logout-all` | De `Admin`, audiência `ouroboros-backoffice` |

Ao contrário do login, da renovação e do logout, as duas rotas **exigem access token** (`Authorization: Bearer <accessToken>`): não declaram `[AllowAnonymous]` e seguem a política padrão da API: autenticação em ambas e, na rota administrativa, também o papel `Admin`, aplicado por convenção de rota mesmo sem política declarada no controller ([Autenticação](../architecture/authentication.md#autorização)). O usuário é o do claim `sub` do token. Não há corpo.

A rota administrativa permite que o `Admin` encerre todas as suas próprias sessões.

## Pré-requisitos

- O banco local com as migrations do Identity aplicadas ([Desenvolvimento](../development.md#banco-de-dados-local)). O logout-all não cria migration: usa a coluna `revoked_at` da [renovação](refresh-session.md#persistência).
- Um access token válido, obtido no [login](login.md) ou na última [renovação](refresh-session.md), da audiência do listener chamado.

## Como usar

Com a API de Identity no ar (listener público em `http://localhost:5141` no perfil padrão) e o access token de um `Customer`:

```http
POST /api/v1/identity/sessions/logout-all HTTP/1.1
Host: localhost:5141
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.exemplo.assinatura
```

Exemplo executável no [guia de desenvolvimento](../development.md#encerrar-todas-as-sessões).

No listener administrativo (`http://localhost:5142`), o pedido é o mesmo, na rota `/api/v1/admin/identity/sessions/logout-all`, com o access token de um `Admin`. O valor do exemplo é fictício.

Depois do `204`, descarte o access token e o refresh token guardados neste dispositivo; para voltar a usar a conta, faça um novo [login](login.md), que abre uma sessão nova normalmente.

### Sessões encerradas (204)

Sem corpo. A resposta é a mesma quando havia tokens ativos e eles foram revogados agora e quando o usuário não tinha nenhuma sessão ativa: a operação é idempotente.

### Sem token ou token inválido (401)

Sem o header `Authorization`, com um token malformado, expirado, de outra assinatura ou de outra audiência (por exemplo, um token de `Customer` no listener administrativo), a resposta é `401` em ProblemDetails, sem explicar o motivo da recusa ([Respostas de erro](../architecture/error-responses.md#respostas-sem-corpo-401-403-404-405-415)). Nada é revogado. Um token válido cujo `sub` não seja um UUID também recebe `401`; os tokens emitidos pelo Identity sempre trazem o `ExternalId` do usuário no `sub`.

## Regras

- **Todas as sessões do usuário, inclusive a atual.** São revogados todos os refresh tokens ativos (não revogados e não expirados) de todas as sessões do usuário do `sub`, num único `UPDATE` (`ExecuteUpdateAsync`) que preenche `revoked_at` e `updated_at`. A sessão de onde veio o access token não é exceção.
- **Outros usuários não são afetados.** O `UPDATE` só alcança tokens de sessões do próprio usuário.
- **Idempotente.** Sem tokens ativos, o `UPDATE` afeta 0 linhas e a resposta continua `204`. Um `sub` sem usuário correspondente também responde `204`, sem gravar nada.
- **Tokens expirados ficam como estão.** Um refresh token já expirado não é revogado; ele já não renova.
- **Sem checagem do papel nem do estado do usuário.** O caso de uso não confere se o usuário está ativo nem o papel dele; quem garante o público é a audiência do token, o listener e a política padrão.
- **Depois do logout-all, tokens revogados são reuso.** Uma renovação com um dos tokens revogados recebe `401` `invalid-refresh-token` e é tratada como [reuso](refresh-token-reuse.md); como a sessão já não tem token ativo, nada mais é revogado.
- **Sessões travadas em ordem de id.** A operação roda numa transação que começa travando todas as sessões do usuário, em ordem de id (`IdentityDbContext.LockUserSessionsAsync`, `SELECT 1 FROM identity.sessions WHERE user_id = ... ORDER BY id FOR UPDATE`), antes do `UPDATE` dos tokens, como manda a [regra das escritas em `identity.refresh_tokens`](refresh-token-reuse.md#atomicidade-e-serialização-por-sessão):
  - uma renovação em andamento numa das sessões termina antes, e o `UPDATE` seguinte já enxerga o token novo que ela gravou; nenhum token novo fica para trás;
  - dois logout-all simultâneos do mesmo usuário pegam os locks na mesma ordem e não entram em deadlock; os outros fluxos (renovação, revogação por reuso e logout) travam uma única sessão, então também não formam ciclo com o logout-all;
  - a [redefinição de senha](password-reset.md#regras-da-redefinição) trava a linha do usuário e depois as mesmas sessões, na mesma ordem de id. O logout-all não trava o usuário, então não espera nada que a redefinição segure antes das sessões, e os dois não formam ciclo ([Ordem dos locks](../architecture/identity.md#ordem-dos-locks)).
- **Sem limite de requisições por IP.** As rotas não têm política de rate limit, ao contrário dos endpoints anônimos de autenticação ([Limitar requisições](auth-rate-limiting.md)); exigem um access token válido.
- **Sem logs.** O caso de uso não escreve log.

## Caso de uso exposto

| Tipo | Local | Papel |
| --- | --- | --- |
| [`ILogoutAll`](../../src/Services/Identity/Ouroboros.Identity/Contracts/Sessions/ILogoutAll.cs) | `Contracts/Sessions` (`public`) | `ExecuteAsync(LogoutAllRequest(UserId))`, sem retorno. `UserId` é o `ExternalId` do usuário, lido do claim `sub`. |
| [`LogoutAll`](../../src/Services/Identity/Ouroboros.Identity/Application/Sessions/LogoutAll.cs) | `Application/Sessions` (`internal`) | Orquestra a transação, o lock ordenado e a revogação por `ISessionStore`, registrada como scoped em `AddIdentityModule` ([persistência](session-persistence.md)). |
| [`IdentityDbContext.LockUserSessionsAsync`](../../src/Services/Identity/Ouroboros.Identity/Infrastructure/Persistence/IdentityDbContext.cs) | `Infrastructure/Persistence` (`internal`) | Trava todas as sessões de um usuário, em ordem de id, até o fim da transação atual. |
| [`IdentityClaims`](../../src/Services/Identity/Ouroboros.Identity/Contracts/IdentityClaims.cs) | `Contracts` (`public`) | Nomes dos claims do access token (`sub`, `role`, `sid`). Permite que os controllers leiam o usuário autenticado por nomes estáveis de claims. |
| `SessionsController` [externo](../../src/Services/Identity/Ouroboros.Identity.Api/Controllers/External/SessionsController.cs) e [administrativo](../../src/Services/Identity/Ouroboros.Identity.Api/Controllers/Admin/SessionsController.cs) | `Controllers/External` e `Controllers/Admin` da API de Identity | Action `LogoutAll`, sem `[AllowAnonymous]` nem rate limit, ao lado do login, da renovação e do logout. Lê o `sub` com `IdentityClaims.Subject`. |

## Limitações

- **Os access tokens já emitidos continuam válidos.** O logout-all atinge só os refresh tokens. A validação do JWT não consulta o banco, então os access tokens em uso, inclusive o que chamou o logout-all e os de outros dispositivos, valem até expirar, no máximo 15 minutos ([Autenticação](../architecture/authentication.md#emissão-do-access-token)), inclusive na rota `me`, desde que o usuário siga ativo e com o mesmo papel ([Consultar o próprio usuário](me.md#limitações)). Quem suspeita de invasão deve considerar essa janela.
- **Login depois do logout-all abre sessão nova.** O logout-all não bloqueia a conta nem impede novos logins: quem tiver a senha entra de novo. Se a senha pode ter vazado, ela precisa ser trocada; hoje, só clientes conseguem, pela [redefinição de senha](password-reset.md), que também encerra todas as sessões. Ainda não há troca de senha para um usuário autenticado.
- **Login simultâneo.** Uma sessão aberta por um login cuja transação ainda não confirmou quando o logout-all revoga os tokens não é alcançada e continua ativa, como um login feito logo depois. O logout-all não trava o usuário, ao contrário da [redefinição de senha](password-reset.md), que espera o login em andamento e revoga a sessão aberta por ele ([Transação com o usuário travado](login.md#transação-com-o-usuário-travado)).
- **As sessões não mudam de estado.** As linhas de `identity.sessions` continuam lá, sem token ativo; não há coluna de encerramento nem listagem de sessões.
- **Usuário não é notificado.** Não há aviso por e-mail nem trilha de auditoria persistida.

## Como verificar

Execute a suíte de integração pelo [guia de desenvolvimento](../development.md#testes) (exige Docker).

[`LogoutAllTests`](../../tests/Ouroboros.IntegrationTests/LogoutAllTests.cs), contra o PostgreSQL real:

- com duas sessões do mesmo `Customer`, uma delas já renovada, o logout-all responde `204`, não sobra refresh token ativo, e tanto o token da primeira sessão quanto o renovado da segunda recebem `401` na renovação;
- um segundo logout-all, já sem sessões ativas, responde `204`;
- sem token e com um token que não é JWT, a resposta é `401`;
- com dois usuários, o logout-all de um não afeta o outro, que continua renovando;
- um `Admin` encerra pelo listener administrativo as suas duas sessões, e a outra sessão deixa de renovar;
- um token de `Customer` recebe `401` no listener administrativo, e o refresh token continua ativo.

A espera pelo lock e a ausência de deadlock entre chamadas simultâneas não têm teste próprio; o mecanismo de lock da sessão é exercitado nos [`RefreshTokenReuseTests`](refresh-token-reuse.md#como-verificar). A recusa de tokens expirados, de outra assinatura ou de outra audiência pelo listener é coberta em [Autenticação](../architecture/authentication.md#testes).

A consulta do próprio usuário, que também exige access token, está em [Consultar o próprio usuário](me.md).

## Referências

- [OWASP Session Management Cheat Sheet: session expiration](https://cheatsheetseries.owasp.org/cheatsheets/Session_Management_Cheat_Sheet.html#session-expiration)
- [RFC 9700, seção 4.14: proteção de refresh tokens](https://www.rfc-editor.org/rfc/rfc9700.html#section-4.14)
- [PostgreSQL: Explicit Locking, deadlocks](https://www.postgresql.org/docs/current/explicit-locking.html#LOCKING-DEADLOCKS)

## Issues relacionadas

- [#18](https://github.com/adnerscarpelini/ouroboros-server/issues/18): encerrar todas as sessões.
- Depende de [#15](https://github.com/adnerscarpelini/ouroboros-server/issues/15) (renovação com rotação do refresh token) e segue a regra de lock da [#16](https://github.com/adnerscarpelini/ouroboros-server/issues/16) (reuso), aplicada a várias sessões.
- [#23](https://github.com/adnerscarpelini/ouroboros-server/issues/23): a redefinição de senha usa o mesmo lock das sessões, depois do lock do usuário; a ordem dos locks do serviço está em [Domínio de identidade](../architecture/identity.md#ordem-dos-locks).

[Voltar à documentação](../README.md)
