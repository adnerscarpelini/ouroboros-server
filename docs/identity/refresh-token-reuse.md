# Revogar a sessão ao detectar reuso de refresh token

## Objetivo

Com a [rotação do refresh token](refresh-session.md), cada renovação revoga o token recebido. Um token já revogado só pode estar com quem guardou uma cópia dele: o atacante, ou o cliente legítimo depois de o atacante ter renovado primeiro. Como não dá para saber qual dos dois apresentou o token, a sessão inteira é encerrada: todos os refresh tokens dela deixam de valer, e os dois lados precisam fazer login de novo.

Módulo responsável: Identity. A detecção fica no mesmo caso de uso da renovação, [`RefreshSession`](../../src/Modules/Ouroboros.Modules.Identity/Application/Sessions/RefreshSession.cs); não há rota nova nem mudança no contrato HTTP.

## Comportamento

- **Reuso detectado.** Na renovação (`POST /api/v1/identity/sessions/refresh` e `POST /api/v1/admin/identity/sessions/refresh`), quando o refresh token apresentado existe e já está revogado, por uma rotação anterior ou pelo [logout](logout.md), todos os tokens ainda não revogados da mesma sessão (`sid`) são revogados.
- **Mesma resposta.** O cliente recebe o mesmo `401` [`invalid-refresh-token`](../errors/identity.md#invalid-refresh-token) de um token inexistente ([Token inválido](refresh-session.md#token-inválido-401)). A resposta não indica que houve reuso.
- **Só a sessão do token.** As outras sessões do mesmo usuário (outros logins, outros dispositivos) continuam funcionando.
- **Token só expirado não é reuso.** Um token expirado e nunca revogado recebe `401` sem revogar nada. Um token revogado é tratado como reuso mesmo que também esteja expirado.
- **Antes das checagens do dono.** O reuso é verificado logo depois de encontrar o token, antes de conferir se o dono está ativo e se é do papel do host. Um token revogado apresentado no host errado ou por um usuário inativo também encerra a sessão.
- **Sem janela de tolerância.** Não há intervalo em que o token anterior ainda seja aceito depois da rotação. O cliente deve serializar as renovações: uma por vez, sempre com o refresh token devolvido pela última.
- **A sessão não muda de estado.** A linha de `identity.sessions` continua lá, sem tokens ativos; os tokens ficam em `identity.refresh_tokens` com `revoked_at` preenchido. Não há coluna nova nem migration.

## Renovações simultâneas com o mesmo token

Se duas ou mais requisições trazem o mesmo token ativo ao mesmo tempo, só uma vence, como já acontecia na [#15](refresh-session.md#regras). A diferença é o que acontece com as perdedoras:

1. Cada requisição abre a transação de renovação, trava a sessão e tenta o `UPDATE` condicional do token (`revoked_at IS NULL` e não expirado).
2. A vencedora revoga o token, grava o sucessor e confirma.
3. A perdedora, depois que a vencedora confirma, não encontra mais o token ativo: o `UPDATE` afeta 0 linhas. Ela desfaz a sua transação (`RollbackAsync`) e confere se o token foi revogado.
4. Se foi revogado, é reuso: a perdedora revoga a sessão inteira, inclusive o token que a vencedora acabou de gravar. Se o token só expirou nesse meio-tempo, nada é revogado.

Resultado: com renovações simultâneas, a vencedora recebe `200` com um par novo, mas esse refresh token já nasce morto, e a sessão termina sem nenhum token ativo. O access token entregue à vencedora continua válido até expirar.

## Atomicidade e serialização por sessão

- **Transação própria para a revogação.** A revogação da sessão roda numa transação separada, com commit, aberta depois do rollback da tentativa de renovação. O rollback da renovação não desfaz a revogação.
- **Lock da sessão.** A renovação, a revogação e, desde a #17, o [logout](logout.md) começam travando a linha da sessão (`SELECT 1 FROM identity.sessions WHERE id = ... FOR UPDATE`, em `IdentityDbContext.LockSessionAsync`), dentro da transação, antes de mexer nos tokens. Assim, operações sobre a mesma sessão são serializadas; sessões diferentes não se bloqueiam.
- **Por que o lock é necessário.** Em READ COMMITTED, o nível padrão do PostgreSQL, o `UPDATE` da revogação só enxerga linhas confirmadas. Sem o lock, uma revogação que rodasse enquanto uma renovação da mesma sessão ainda não tinha confirmado não veria o token novo inserido por ela, e esse token sobreviveria ativo. Com o lock, a revogação espera o commit da renovação e o `UPDATE` seguinte já vê o token novo. O defeito foi encontrado e corrigido durante a validação da #16.
- **Sem deadlock.** Renovação e revogação pegam os locks na mesma ordem: primeiro a sessão, depois os tokens.

> **Regra para novas escritas em `identity.refresh_tokens`:** todo código que revogar ou inserir refresh tokens de uma sessão deve, na mesma transação, travar a linha da sessão antes, na mesma ordem (sessão, depois tokens). O lock é o método `LockSessionAsync` do [`IdentityDbContext`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Persistence/IdentityDbContext.cs), usado pela renovação, pela revogação por reuso e pelo [logout](logout.md) ([#17](https://github.com/adnerscarpelini/ouroboros-server/issues/17)).

## Log

Quando a revogação afeta pelo menos um token, o caso de uso registra um aviso (`Warning`):

```text
Reuso de refresh token detectado; sessão {SessionId} do usuário {UserId} revogada.
```

- `SessionId` e `UserId` são os ids externos (os mesmos de `sid` e `sub` no access token), nunca os ids internos do banco.
- O refresh token e o access token nunca aparecem no log.
- Se a sessão já não tinha tokens ativos (reapresentações seguintes do mesmo token, ou a perdedora de uma corrida que chega depois de outra perdedora), a revogação não afeta nenhuma linha e o aviso não se repete.

## Como reagir no cliente

O cliente não consegue distinguir o reuso das outras causas do `401` e não precisa: descarte o access token e o refresh token guardados e peça um novo login ([Como reagir a `invalid-refresh-token`](../errors/identity.md#invalid-refresh-token)). Para não encerrar a própria sessão por concorrência, faça uma renovação por vez e nunca reenvie um refresh token depois de receber o par seguinte.

## Limitações

- **O access token já emitido continua válido.** A revogação atinge só os refresh tokens. A validação do access token não consulta a sessão, então um access token emitido antes (inclusive o da vencedora de uma corrida) vale até expirar, no máximo 15 minutos ([Autenticação](../architecture/authentication.md#emissão-do-access-token)).
- **Sem tolerância a renovações concorrentes do cliente legítimo.** Um cliente que dispare duas renovações com o mesmo token (por exemplo, duas abas ou duas threads sem coordenação) perde a sessão. A escolha foi deliberada, para não abrir uma janela em que um token vazado ainda seja aceito.
- **Usuário não é notificado.** Não há aviso por e-mail nem trilha de auditoria persistida; o evento fica só no log da aplicação (fora do escopo da issue).
- **Logout não detecta reuso.** Desde a [#17](https://github.com/adnerscarpelini/ouroboros-server/issues/17), um token encerrado pelo [logout](logout.md) e apresentado depois na renovação é tratado como reuso. O inverso não vale: um token já revogado apresentado ao logout recebe `204` sem revogar a sessão, então um logout com um token já rotacionado não encerra o token atual ([Limitações do logout](logout.md#limitações)).

## Como verificar

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests   # exige Docker
```

[`RefreshTokenReuseTests`](../../tests/Ouroboros.IntegrationTests/RefreshTokenReuseTests.cs), contra o PostgreSQL real, pela API externa:

- um token já rotacionado, apresentado de novo, recebe `401` `invalid-refresh-token`, e o sucessor também deixa de renovar; não sobra token ativo;
- com duas sessões do mesmo usuário, o reuso na primeira não afeta a segunda, que continua renovando com o mesmo `sid`;
- cinco renovações simultâneas com o mesmo token têm um único `200`, as demais recebem `401`, a sessão termina sem token ativo e o refresh token da vencedora também recebe `401`;
- uma renovação em andamento, simulada numa conexão própria com os mesmos passos e locks do caso de uso, grava um token novo sem confirmar; o reuso apresentado ao mesmo tempo fica esperando o lock da sessão (verificado em `pg_stat_activity`) e, depois do commit, revoga também o token novo;
- em dez rodadas, uma cadeia de renovações corre contra a reapresentação do token antigo, e a sessão sempre termina sem token ativo;
- um token apenas expirado recebe `401` sem revogar nenhum token, e a sessão aberta depois continua renovando;
- um token revogado por logout (revogado direto no banco, teste anterior à #17) recebe `401`; o logout real seguido de renovação é coberto pelos [`LogoutTests`](logout.md#como-verificar);
- o reuso gera um único log com o id externo do usuário e o da sessão, e nenhum log contém os refresh tokens.

Os testes da renovação em si continuam em [`RefreshSessionTests`](../../tests/Ouroboros.IntegrationTests/RefreshSessionTests.cs) ([Renovar o access token](refresh-session.md#como-verificar)).

Na validação da #16, a suíte tinha 260 testes; na da #17, 270.

## Referências

- [RFC 9700, seção 4.14: proteção de refresh tokens](https://www.rfc-editor.org/rfc/rfc9700.html#section-4.14)
- [PostgreSQL: Explicit Locking, row-level locks](https://www.postgresql.org/docs/current/explicit-locking.html#LOCKING-ROWS)
- [PostgreSQL: Read Committed Isolation Level](https://www.postgresql.org/docs/current/transaction-iso.html#XACT-READ-COMMITTED)

## Origem

- [#16](https://github.com/adnerscarpelini/ouroboros-server/issues/16): revogar a sessão ao detectar reuso de refresh token.
- Depende de [#15](https://github.com/adnerscarpelini/ouroboros-server/issues/15): renovação com rotação do refresh token.

[Voltar à documentação](../README.md)
