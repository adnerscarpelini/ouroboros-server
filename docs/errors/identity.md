# Erros do módulo Identity

Falhas de negócio do módulo Identity, no formato do [catálogo de erros](README.md).

## invalid-credentials

- **Status HTTP**: `401 Unauthorized`
- **Quando ocorre**: no login (`POST /api/v1/identity/sessions` e `POST /api/v1/admin/identity/sessions`), quando o e-mail não existe, a senha está errada, o usuário está inativo, o usuário é de outro papel (um `Admin` na API externa, um `Customer` no backoffice) ou a conta está bloqueada por falhas seguidas de senha, mesmo que a senha informada esteja correta ([Bloquear a conta](../identity/account-lockout.md)). A resposta é a mesma em todos os casos, para não revelar se a conta existe nem se está bloqueada.
- **Como reagir**: avise o usuário que o e-mail ou a senha estão incorretos, sem sugerir qual dos dois. Não repita a tentativa automaticamente: cada senha errada conta para o bloqueio da conta, que, com os valores iniciais, ocorre na quinta falha seguida e dura 15 minutos.

[Voltar ao catálogo de erros](README.md)
