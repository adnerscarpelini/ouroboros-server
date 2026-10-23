# Erros do módulo Identity

Falhas de negócio do módulo Identity, no formato do [catálogo de erros](README.md).

## invalid-credentials

- **Status HTTP**: `401 Unauthorized`
- **Quando ocorre**: no login (`POST /api/v1/identity/sessions` e `POST /api/v1/admin/identity/sessions`), quando o e-mail não existe, a senha está errada, o usuário está inativo, o usuário é de outro papel (um `Admin` na API externa, um `Customer` no backoffice) ou a conta está bloqueada por falhas seguidas de senha, mesmo que a senha informada esteja correta ([Bloquear a conta](../identity/account-lockout.md)). A resposta é a mesma em todos os casos, para não revelar se a conta existe nem se está bloqueada.
- **Como reagir**: avise o usuário que o e-mail ou a senha estão incorretos, sem sugerir qual dos dois. Não repita a tentativa automaticamente: cada senha errada conta para o bloqueio da conta, que, com os valores iniciais, ocorre na quinta falha seguida e dura 15 minutos.

## invalid-refresh-token

- **Status HTTP**: `401 Unauthorized`
- **Quando ocorre**: na renovação (`POST /api/v1/identity/sessions/refresh` e `POST /api/v1/admin/identity/sessions/refresh`), quando o refresh token está vazio ou ausente, não existe, já foi revogado (inclusive por uma renovação simultânea com o mesmo token, que venceu) ou expirou, ou quando o dono do token está inativo ou é de outro papel (uma sessão de `Admin` na API externa, de `Customer` no backoffice). A resposta é a mesma em todos os casos ([Renovar o access token](../identity/refresh-session.md#token-inválido-401)). Quando o token já estava revogado, o servidor trata a apresentação como reuso e revoga também todos os refresh tokens da mesma sessão, inclusive o mais recente; as outras sessões do usuário não são afetadas ([Revogar a sessão ao detectar reuso](../identity/refresh-token-reuse.md)). No logout (`POST /api/v1/identity/sessions/logout` e `POST /api/v1/admin/identity/sessions/logout`), só quando o refresh token está vazio, ausente ou não existe; um token já revogado ou expirado recebe `204`, sem ser tratado como reuso ([Encerrar a sessão atual](../identity/logout.md#token-inválido-401)).
- **Como reagir**: na renovação, descarte o access token e o refresh token guardados e peça um novo login; a sessão pode ter sido encerrada, e nenhum token dela volta a renovar. Não repita a renovação com o mesmo token. Não há janela de tolerância: para não perder a sessão por concorrência, faça uma renovação por vez (serialize as chamadas entre abas, threads ou processos do mesmo cliente) e use sempre o refresh token devolvido pela última. Duas renovações simultâneas com o mesmo token encerram a sessão, mesmo que uma delas responda `200`. No logout, trate a resposta como sessão já encerrada no cliente: descarte os tokens guardados; não há o que repetir.

## invalid-registration-token

- **Status HTTP**: `400 Bad Request`
- **Quando ocorre**: na conclusão do cadastro (`POST /api/v1/identity/registrations/complete`), quando o token do link está vazio, não existe, expirou, já foi usado, foi invalidado por uma solicitação mais nova ou é de outro tipo, ou quando o e-mail passou a pertencer a outra conta (inclusive por uma conclusão simultânea com o mesmo token, que venceu). A resposta é a mesma em todos os casos, sem `detail`. O token é conferido antes da senha: um token inválido recebe este erro mesmo com uma senha fora da política ([Concluir o cadastro](../identity/registration.md#token-inválido-400)).
- **Como reagir**: peça ao usuário que solicite o cadastro de novo, para receber um link novo. Não repita a conclusão com o mesmo token. Se o e-mail já tiver conta, nenhum link novo chega; o usuário pode tentar entrar pelo login.

## invalid-password-reset-token

- **Status HTTP**: `400 Bad Request`
- **Quando ocorre**: na redefinição de senha (`POST /api/v1/identity/password-resets/complete`), quando o token do link está vazio, não existe, expirou, já foi usado, foi invalidado por uma solicitação mais nova, é de outro tipo (por exemplo, de cadastro) ou pertence a um usuário inativo, inclusive quando uma conclusão simultânea com o mesmo token venceu. A resposta é a mesma em todos os casos, sem `detail`. O token é conferido antes da senha: um token inválido recebe este erro mesmo com uma senha fora da política. Nada é gravado: a senha, o bloqueio e as sessões continuam como estavam ([Concluir a redefinição](../identity/password-reset.md#token-inválido-400)).
- **Como reagir**: peça ao usuário que solicite a redefinição de novo, para receber um link novo. Não repita a conclusão com o mesmo token. Só contas de cliente ativas recebem o link.

## password-rejected

- **Status HTTP**: `400 Bad Request`
- **Quando ocorre**: na conclusão do cadastro (`POST /api/v1/identity/registrations/complete`) e na redefinição de senha (`POST /api/v1/identity/password-resets/complete`), com um token válido, quando a senha informada (ou ausente, tratada como vazia) não atende à [política de senha](../architecture/password-policy.md): tamanho, lista de senhas comuns, nome do sistema ou parte local do e-mail. Na redefinição, também quando a senha nova é igual à atual (`detail`: `A nova senha não pode ser igual à atual.`). O `detail` traz a regra violada. O token não é consumido e continua válido ([Concluir o cadastro](../identity/registration.md#senha-recusada-400), [Concluir a redefinição](../identity/password-reset.md#senha-recusada-400)).
- **Como reagir**: mostre o `detail` ao usuário e peça outra senha; repita a conclusão com o mesmo token.

[Voltar ao catálogo de erros](README.md)
