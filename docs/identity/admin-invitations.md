# Convites de administrador

Identity é proprietário do convite, da decisão, do token de uso único e da criação da conta ([#25](https://github.com/adnerscarpelini/ouroboros-server/issues/25), [#36](https://github.com/adnerscarpelini/ouroboros-server/issues/36)). Notifications recebe comunicações cifradas pela outbox, sem consultar usuários nem escolher aprovadores.

## Contrato HTTP

As rotas ficam exclusivamente no listener administrativo, inclusive a conclusão anônima. O gateway administrativo encaminha o mesmo prefixo.

| Método e rota | Entrada | Resultado |
| --- | --- | --- |
| `POST /api/v1/admin/identity/admin-invitations` | `email`, `currentPassword` | `202` com `id`; exige Admin ativo e política `identity:admins.invite` |
| `POST /api/v1/admin/identity/admin-invitations/complete` | `token`, `password` | `204`; anônimo na rede administrativa |
| `POST /api/v1/admin/identity/admin-invitations/{id}/approval` | `currentPassword` | `204`; exige outro Admin ativo designado como aprovador |
| `POST /api/v1/admin/identity/admin-invitations/{id}/rejection` | `currentPassword` | `204`; mesmas exigências da aprovação |

Senha atual incorreta ou bloqueada retorna `401`; papel administrativo removido retorna `403`. E-mail inválido ou fora do domínio exato retorna `400`; e-mail já cadastrado ou com convite aberto retorna `409`. Não há promoção de uma conta existente. A conclusão retorna `400` para token inválido, expirado ou usado, sem revelar o token. Erro na política de senha não consome o convite.

Com um único administrador ativo, o convite recebe token e link imediatamente. Com dois ou mais, só é criado se houver outro aprovador ativo designado no domínio permitido. Nesse caso fica `PendingApproval`, sem token nem e-mail ao convidado. Sem outro aprovador, a criação retorna `400` com explicação e não grava convite ou intenções. A conta só é criada na conclusão, como Admin ativo, com `InvitedBy` contendo o identificador público de quem convidou.

## Aprovação por outro administrador

`Identity:AdminInvitations:ApproverEmails` contém e-mails separados por vírgulas. A criação e cada decisão leem essa lista e o domínio permitido nos parâmetros locais atuais, sem cache. Somente administradores ativos, no domínio exato e diferentes do solicitante são elegíveis. Entradas inválidas ou fora do domínio são ignoradas e geram warning com contagem, sem registrar os endereços. Um administrador ativo fora do domínio ainda pode receber os avisos gerais destinados a todos os Admins; isso não o torna aprovador.

Os aprovadores elegíveis recebem aviso de pendência com `CompletionUrl?invitationId={id}`. Essa URL aponta para a interface interna configurada, não para uma aprovação automática: abrir o link não decide nada. A interface deve autenticar o administrador e enviar sua senha atual por `POST` à rota de decisão. Esta entrega não implementa essa interface nem listagem de pendências.

O solicitante não pode aprovar nem recusar o próprio convite, mesmo que conste da lista. Solicitante, administrador não designado, inativo ou demovido recebem `403`; senha incorreta ou bloqueada retorna `401`. Convite inexistente retorna `404`; decisão concorrente ou repetida sobre estado já decidido retorna `409`.

A pendência vale 24 horas desde a criação. Uma tentativa autorizada de decisão após o prazo registra `Expired` e retorna `400`, sem link ao convidado; a expiração não depende de um worker de entrega. Na aprovação, o estado passa a `Approved`, registra `DecidedBy`/`DecidedAt` e gera o link com a validade configurada contada a partir da aprovação. Na recusa, passa a `Rejected`, registra os mesmos dados e não envia link ao convidado. Todos os administradores ativos recebem aviso da decisão, sem o token.

## Parâmetros e segurança

- `Identity:AdminInvitations:AllowedEmailDomain`: domínio exato, sem subdomínios.
- `Identity:AdminInvitations:TokenLifetimeHours`: novas credenciais valem de 1 a 168 horas; valor inicial 24.
- `Identity:AdminInvitations:ApproverEmails`: lista de aprovadores, aplicada junto do domínio permitido e do estado administrativo atual.
- `Identity:AdminInvitations:CompletionUrl`: endereço da tela interna de revisão/conclusão, sem query, fragmento ou credenciais. O valor inicial `http://localhost:3001/administradores/convite` é configuração de desenvolvimento; esta entrega não implementa essa interface. Configure-a antes do uso.

O link acrescenta `?token=...`. Somente seu hash é guardado no convite; o conteúdo do e-mail permanece protegido na outbox/inbox. Administradores ativos recebem avisos de criação e aceitação sem o token, incluindo o novo administrador após a conclusão.

`identity-admin-actions` limita criação, aprovação e recusa a 10 requisições/minuto/IP, compartilhando o contador dessa política. As três operações exigem a capacidade `identity:admins.invite`. A conclusão usa `identity-registration-completion`, também 10/minuto/IP, com a separação por público do rate limiter. Ver [limites de autenticação](auth-rate-limiting.md).

A reautenticação cara ocorre antes da transação. Sob advisory lock compartilhado com a criação do primeiro administrador e depois lock do usuário, Identity confirma senha, papel e estado atuais. A decisão, seus dados de auditoria, token e intenções de aviso pertencem ao mesmo commit; apenas a primeira decisão concorrente vale. Na conclusão, consumo do convite, nova conta e intenções também são atômicos. Falha de persistência não deixa decisão, conta ou aviso parcial. Nenhuma chamada ao broker ocorre nessas transações, e indisponibilidade do transporte não desfaz uma decisão confirmada.

## Implantação e verificação

Atualize **Notifications antes de Identity**: o leitor passa a aceitar os propósitos `admin-invitation` e `admin-notice` no envelope v1. A versão antiga recusa esses propósitos. Aplique explicitamente a migration `AddAdminInvitations` com a credencial migradora e mantenha credenciais de runtime separadas.

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class "*AdminInvitationTests"
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class "*AdminInvitationApprovalTests"
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-method "*Admin_invitation*"
```

O QA da #25 validou 18 casos HTTP/concorrência com PostgreSQL real e dois cenários completos de mensageria, incluindo convite novo e regressão de cadastro/reset. Identity usa TestServer nesses cenários; RabbitMQ, processo Notifications e arquivos `.eml` são reais. A parada do broker após commit conserva intenções, e sua retomada entrega o link. Ver [outbox](email-outbox.md) e [desenvolvimento](../development.md).

O QA da #36 validou 33 casos de aprovação e regressão de convites, com HTTP em TestServer e PostgreSQL real, além de 332 casos de arquitetura. A falha de publicação contra um endpoint fechado preservou aprovação, hash e seis intenções protegidas pendentes, com tentativas e próximo agendamento registrados; nova decisão continuou retornando `409`. A entrega pelo worker não foi repetida nessa fatia: os mesmos propósitos foram verificados na #25. A #36 não acrescenta migration nem altera o modelo persistente.

[Voltar à documentação](../README.md)
