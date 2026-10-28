# Cadastro de integradores

Identity cadastra credenciais de sistemas integradores pelo backoffice ([#37](https://github.com/adnerscarpelini/ouroboros-server/issues/37)). O integrador é uma entidade própria, não uma conta de usuário. Emissão de tokens e desativação pertencem às entregas #39 e #38 e ainda não estão implementadas nesta fatia.

## Contrato HTTP

As rotas ficam no listener administrativo e são encaminhadas pelo gateway administrativo. Exigem Admin ativo e a política `identity:api-clients.manage`; o estado do administrador é relido no proprietário.

| Método e rota | Entrada | Resultado |
| --- | --- | --- |
| `POST /api/v1/admin/identity/api-clients` | `name`, `description` opcional, `currentPassword` | `201` com `client_id` e `client_secret`, mais `Location` para consulta |
| `GET /api/v1/admin/identity/api-clients/{clientId}` | Identificador público na rota | `200` com resumo, sem secret ou hash; `404` se inexistente |

O nome é aparado, deve ter de 1 a 100 caracteres e não aceita controles. A descrição pode ter até 1000 caracteres, não aceita NUL e, quando omitida, é armazenada como texto vazio. Dados inválidos retornam `400`. Senha atual incorreta ou bloqueada retorna `401`; administrador inativo ou com papel removido retorna `403`. A criação usa `identity-admin-actions`, com 10 requisições/minuto/IP compartilhadas com as demais ações administrativas dessa política.

O resumo contém `client_id`, `name`, `description`, `isActive`, `createdBy` e `createdAt`. O cadastro nasce ativo e guarda o identificador público de quem o criou, com data UTC. Não há listagem nesta entrega.

## Credenciais e transação

`client_id` é o `ExternalId` UUIDv7. O secret contém 32 bytes aleatórios codificados em Base64Url; é devolvido **uma única vez**, na resposta `201`, marcada `Cache-Control: no-store`. A consulta nunca o devolve. Identity armazena somente SHA-256 do valor recebido pelo integrador, não o secret bruto. O hash rápido é adequado à alta entropia gerada; senhas humanas continuam usando o hasher lento existente.

A senha do administrador é verificada antes da transação. Sob lock do usuário, Identity confirma novamente hash, papel e estado atuais. Cadastro e intenções de aviso para todos os administradores ativos são gravados na mesma transação local; falha na proteção ou na persistência desfaz a operação. Não há chamada ao broker ou ao provedor durante essa transação.

Os avisos contêm somente identificador público do integrador, autor e data UTC: não incluem secret, hash, senha, nome ou descrição. O propósito `api-client-notice` passa pela outbox protegida e pelo Notifications independente. A entrega posterior não reaplica cadastro confirmado. Ver [outbox](email-outbox.md).

## Implantação e verificação

Atualize **Notifications antes de Identity**, pois seu leitor passa a aceitar `api-client-notice` no envelope v1. Aplique explicitamente `AddApiClients` com a credencial migradora de Identity; não use a role de runtime para migrations.

A readiness de Identity verifica conexão e existência das tabelas `identity.parameters`, `identity.email_intents`, `identity.admin_invitations` e `identity.api_clients` por consultas `SELECT 1`. A inclusão das tabelas das novas funcionalidades não transforma a probe em validação genérica de todas as colunas ou versões de schema. Permanecem conexões sem pooling, limites existentes e ausência de reset global de pools.

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class "*ApiClientRegistrationTests"
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-method "*Api_client*"
```

O QA da #37 aprovou 16 casos de cadastro com PostgreSQL real e três cenários completos de mensageria: novo aviso de integrador, convite e regressão de cadastro/reset. Identity usa TestServer; os cenários de entrega usam RabbitMQ, processo Notifications, banco próprio e arquivos `.eml` reais, com API sob IdentityRuntime e migrations separadas. A arquitetura passou em 339 casos; build sem avisos/erros e formatação foram conferidos. Esses resultados não representam execução da suíte completa de integração.

[Voltar à documentação](../README.md)
