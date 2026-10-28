# Convites de administrador

Identity é proprietário do convite, do token de uso único e da criação da conta ([#25](https://github.com/adnerscarpelini/ouroboros-server/issues/25)). Notifications recebe comunicações cifradas pela outbox, sem consultar usuários.

## Contrato HTTP

As rotas ficam exclusivamente no listener administrativo, inclusive a conclusão anônima. O gateway administrativo encaminha o mesmo prefixo.

| Método e rota | Entrada | Resultado |
| --- | --- | --- |
| `POST /api/v1/admin/identity/admin-invitations` | `email`, `currentPassword` | `202` com `id`; exige Admin ativo e política `identity:admins.invite` |
| `POST /api/v1/admin/identity/admin-invitations/complete` | `token`, `password` | `204`; anônimo na rede administrativa |

Senha atual incorreta ou bloqueada retorna `401`; papel administrativo removido retorna `403`. E-mail inválido ou fora do domínio exato retorna `400`; e-mail já cadastrado ou com convite aberto retorna `409`. Não há promoção de uma conta existente. A conclusão retorna `400` para token inválido, expirado ou usado, sem revelar o token. Erro na política de senha não consome o convite.

Com um único administrador ativo, o convite recebe token e link imediatamente. Com dois ou mais, fica `PendingApproval`, sem token nem e-mail ao convidado; a seleção e decisão de aprovadores são a entrega da [#36](https://github.com/adnerscarpelini/ouroboros-server/issues/36), ainda pendente nesta fatia. A conta só é criada na conclusão, como Admin ativo, com `InvitedBy` contendo o identificador público de quem convidou.

## Parâmetros e segurança

- `Identity:AdminInvitations:AllowedEmailDomain`: domínio exato, sem subdomínios.
- `Identity:AdminInvitations:TokenLifetimeHours`: novas credenciais valem de 1 a 168 horas; valor inicial 24.
- `Identity:AdminInvitations:CompletionUrl`: endereço da tela interna, sem query, fragmento ou credenciais. O valor inicial `http://localhost:3001/administradores/convite` é configuração de desenvolvimento; esta entrega não implementa essa interface. Configure-a antes do uso.

O link acrescenta `?token=...`. Somente seu hash é guardado no convite; o conteúdo do e-mail permanece protegido na outbox/inbox. Administradores ativos recebem avisos de criação e aceitação sem o token, incluindo o novo administrador após a conclusão.

`identity-admin-actions` limita criação a 10 requisições/minuto/IP. A conclusão usa `identity-registration-completion`, também 10/minuto/IP, com a separação por público do rate limiter. Ver [limites de autenticação](auth-rate-limiting.md).

A reautenticação cara ocorre antes da transação. Sob advisory lock compartilhado com a criação do primeiro administrador e depois lock do usuário, Identity confirma senha, papel e estado atuais. Consumo do convite, nova conta e intenções de aviso pertencem ao mesmo commit; concorrência e falha de persistência não produzem contas ou avisos parciais. Nenhuma chamada ao broker ocorre nessa transação.

## Implantação e verificação

Atualize **Notifications antes de Identity**: o leitor passa a aceitar os propósitos `admin-invitation` e `admin-notice` no envelope v1. A versão antiga recusa esses propósitos. Aplique explicitamente a migration `AddAdminInvitations` com a credencial migradora e mantenha credenciais de runtime separadas.

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class "*AdminInvitationTests"
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-method "*Admin_invitation*"
```

O QA da #25 validou 18 casos HTTP/concorrência com PostgreSQL real e dois cenários completos de mensageria, incluindo convite novo e regressão de cadastro/reset. Identity usa TestServer nesses cenários; RabbitMQ, processo Notifications e arquivos `.eml` são reais. A parada do broker após commit conserva intenções, e sua retomada entrega o link. Ver [outbox](email-outbox.md) e [desenvolvimento](../development.md).

[Voltar à documentação](../README.md)
