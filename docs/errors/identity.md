# Erros do módulo Identity

Falhas de negócio do módulo Identity, no formato do [catálogo de erros](README.md).

## invalid-credentials

- **Status HTTP**: `401 Unauthorized`
- **Quando ocorre**: no login (`POST /api/v1/identity/sessions` e `POST /api/v1/admin/identity/sessions`), quando o e-mail não existe, a senha está errada, o usuário está inativo, o usuário é de outro papel (um `Admin` na API externa, um `Customer` no backoffice) ou a conta está bloqueada por falhas seguidas de senha, mesmo que a senha informada esteja correta ([Bloquear a conta](../identity/account-lockout.md)). A resposta é a mesma em todos os casos, para não revelar se a conta existe nem se está bloqueada.
- **Como reagir**: avise o usuário que o e-mail ou a senha estão incorretos, sem sugerir qual dos dois. Não repita a tentativa automaticamente: cada senha errada conta para o bloqueio da conta, que, com os valores iniciais, ocorre na quinta falha seguida e dura 15 minutos.

## invalid-refresh-token

- **Status HTTP**: `401 Unauthorized`
- **Quando ocorre**: na renovação (`POST /api/v1/identity/sessions/refresh` e `POST /api/v1/admin/identity/sessions/refresh`), quando o refresh token está vazio ou ausente, não existe, já foi revogado (inclusive por uma renovação simultânea com o mesmo token, que venceu) ou expirou, ou quando o dono do token está inativo ou é de outro papel (uma sessão de `Admin` na API externa, de `Customer` no backoffice). A resposta é a mesma em todos os casos ([Renovar o access token](../identity/refresh-session.md#token-inválido-401)). Quando o token já estava revogado, o servidor trata a apresentação como reuso e revoga também todos os refresh tokens da mesma sessão, inclusive o mais recente; as outras sessões do usuário não são afetadas ([Revogar a sessão ao detectar reuso](../identity/refresh-token-reuse.md)).
- **Como reagir**: descarte o access token e o refresh token guardados e peça um novo login; a sessão pode ter sido encerrada, e nenhum token dela volta a renovar. Não repita a renovação com o mesmo token. Não há janela de tolerância: para não perder a sessão por concorrência, faça uma renovação por vez (serialize as chamadas entre abas, threads ou processos do mesmo cliente) e use sempre o refresh token devolvido pela última. Duas renovações simultâneas com o mesmo token encerram a sessão, mesmo que uma delas responda `200`.

[Voltar ao catálogo de erros](README.md)
