# Integradores, tokens e desativação

Identity cadastra credenciais de sistemas integradores pelo backoffice ([#37](https://github.com/adnerscarpelini/ouroboros-server/issues/37)), emite seus access tokens no público externo ([#39](https://github.com/adnerscarpelini/ouroboros-server/issues/39)) e controla desativação e validação autoritativa ([#38](https://github.com/adnerscarpelini/ouroboros-server/issues/38)). O integrador é uma entidade própria, não uma conta de usuário.

## Contrato HTTP

As rotas ficam no listener administrativo e são encaminhadas pelo gateway administrativo. Exigem Admin ativo e a política `identity:api-clients.manage`; o estado do administrador é relido no proprietário.

| Método e rota | Entrada | Resultado |
| --- | --- | --- |
| `POST /api/v1/admin/identity/api-clients` | `name`, `description` opcional, `currentPassword` | `201` com `client_id` e `client_secret`, mais `Location` para consulta |
| `GET /api/v1/admin/identity/api-clients/{clientId}` | Identificador público na rota | `200` com resumo, sem secret ou hash; `404` se inexistente |
| `POST /api/v1/admin/identity/api-clients/{clientId}/deactivation` | `currentPassword` | `204` idempotente; exige reautenticação administrativa |

O nome é aparado, deve ter de 1 a 100 caracteres e não aceita controles. A descrição pode ter até 1000 caracteres, não aceita NUL e, quando omitida, é armazenada como texto vazio. Dados inválidos retornam `400`. Senha atual incorreta ou bloqueada retorna `401`; administrador inativo ou com papel removido retorna `403`. A criação usa `identity-admin-actions`, com 10 requisições/minuto/IP compartilhadas com as demais ações administrativas dessa política.

O resumo contém `client_id`, `name`, `description`, `isActive`, `createdBy`, `createdAt`, `deactivatedBy` e `deactivatedAt`. O cadastro nasce ativo e guarda o identificador público de quem o criou, com data UTC; os campos de desativação começam nulos. Não há listagem nesta entrega.

## Credenciais e transação

`client_id` é o `ExternalId` UUIDv7. O secret contém 32 bytes aleatórios codificados em Base64Url; é devolvido **uma única vez**, na resposta `201`, marcada `Cache-Control: no-store`. A consulta nunca o devolve. Identity armazena somente SHA-256 do valor recebido pelo integrador, não o secret bruto. O hash rápido é adequado à alta entropia gerada; senhas humanas continuam usando o hasher lento existente.

A senha do administrador é verificada antes da transação. Sob lock do usuário, Identity confirma novamente hash, papel e estado atuais. Cadastro e intenções de aviso para todos os administradores ativos são gravados na mesma transação local; falha na proteção ou na persistência desfaz a operação. Não há chamada ao broker ou ao provedor durante essa transação.

Os avisos contêm somente identificador público do integrador, autor e data UTC: não incluem secret, hash, senha, nome ou descrição. O propósito `api-client-notice` passa pela outbox protegida e pelo Notifications independente. A entrega posterior não reaplica cadastro confirmado. Ver [outbox](email-outbox.md).

## Emissão por client credentials

`POST /api/v1/identity/oauth/token` é anônimo e existe somente no listener público, acessível também pelo gateway externo. Aceita `application/x-www-form-urlencoded`, com `grant_type=client_credentials`, `client_id` e `client_secret`. Como alternativa, envie o par por HTTP Basic e mantenha somente `grant_type` no formulário. Cada componente de Basic é codificado como formulário antes de montar `client_id:client_secret` e aplicar Base64. Não misture Basic com os campos de credencial no corpo. Fora do ambiente local, use HTTPS para transportar credenciais e tokens.

A resposta `200` contém `access_token`, `token_type` igual a `Bearer` e `expires_in` em segundos, com `Cache-Control: no-store`. Não cria sessão nem emite refresh token. A política `identity-login` aplica o limite configurado de autenticação por IP e público, inicialmente 10 requisições/minuto.

Erros seguem ProblemDetails do projeto com extensão `error`: credencial errada, client inexistente ou inativo retornam o mesmo `401` genérico (`invalid_client`, com desafio Basic); concessão não suportada retorna `400` (`unsupported_grant_type`); `scope` não vazio retorna `400` (`invalid_scope`), pois escopos não estão implementados. Parâmetros de formulário duplicados retornam `400` (`invalid_request`), e autenticação misturada é recusada. JSON não é um formato aceito; a seleção de endpoints pode resultar em recusa de autenticação por fallback, portanto não há contrato de `415` para esse caso.

O caso de uso compara o hash em tempo constante, trava a linha do client e relê hash/estado antes de assinar, usando a porta `IAccessTokenIssuer`. O JWT RS256 usa a audiência pública, `sub=client_id`, `principal_type=client` e `jti`, com validade `Authentication:Jwt:AccessTokenLifetime`; não contém `role` ou `sid` de usuário. Ver [autenticação](../architecture/authentication.md).

## Validação e separação dos principais

`IdentityBearerEvents` valida formatos distintos para usuários e integradores. Um client só é autenticado no esquema público, com RS256 e identificador público válido, sem papel ou sessão. A cada autenticação, Identity consulta o estado atual no próprio banco, sem cache; assinatura válida isoladamente não concede acesso. `Authentication:Jwt:ClientValidationTimeout` é positivo, tem padrão de dois segundos e máximo de cinco. Integrador inativo ou token inválido recebe `401`; falha ou timeout na consulta autoritativa fecha o acesso com `503` sanitizado, `traceId` e `Cache-Control: no-store`, sem fallback permissivo. O ProblemDetails também é preservado quando o escritor padrão recusa um `Accept` como `text/plain`.

As políticas padrão e de fallback recusam client em endpoints de usuário. Uma rota destinada a integradores precisa declarar a política explícita `identity:api-clients.access`. Nenhum endpoint de negócio para integradores foi criado; a rota abaixo é exclusivamente técnica.

### Contrato técnico para consumidores

`POST /api/v1/identity/oauth/validation` recebe o token original em `Authorization: Bearer <token>`, sem corpo. Existe somente no listener público, também pelo gateway externo, e exige `identity:api-clients.access`. A autenticação verifica assinatura, audiência, principal e estado atual; a action não muda estado e retorna `204` somente quando tudo é válido. Token inválido ou de integrador inativo retorna `401`; principal autenticado sem a política apropriada recebe `403`; indisponibilidade da consulta recebe `503`. A validação bem-sucedida e os desafios de autenticação usam `no-store`; nenhum resultado deve ser usado como cache de autorização.

A política obrigatória `identity-client-validation` permite inicialmente 120 requisições/minuto/IP, com recusa `429` e `Retry-After`. Futuros serviços consumidores devem chamar esse contrato por HTTP com timeout limitado, fora de suas transações e locks locais, e aceitar **somente `204`**. `401`, `403`, `429`, `503`, timeout, falha de rede ou qualquer outro resultado negam acesso. Não reutilizar uma aceitação em cache nem tratar falha de consulta como autorização. Esses consumidores ainda não existem no checkout; a #38 não cria outro serviço.

## Desativação

A operação administrativa exige a senha atual e a mesma capacidade de gerenciamento, usando `identity-admin-actions` (10/minuto/IP compartilhadas). Senha incorreta retorna `401`, administrador inativo ou demovido retorna `403`, e identificador inexistente retorna `404`.

Identity trava primeiro o usuário e depois o client, confirma reautenticação e grava `IsActive=false`, `DeactivatedBy`, `DeactivatedAt` e intenções de aviso na mesma transação local. Administradores ativos recebem aviso sem credenciais pelo propósito `api-client-notice`. Nova chamada válida sobre client já desativado retorna `204` sem sobrescrever a autoria/data originais ou repetir avisos; decisões concorrentes preservam uma única alteração auditada. Falha ao persistir a intenção desfaz a desativação; falha posterior de publicação mantém o estado desativado e as intenções recuperáveis, sem reativar nem reaplicar o comando. Não há reativação nesta entrega.

Depois do commit, as credenciais não emitem novos tokens e a próxima validação de um JWT já emitido recusa o integrador. Isso não cancela uma requisição cuja validação terminou antes do commit; a garantia é sobre a próxima validação autoritativa, sem janela de cache ou propagação por evento.

## Implantação e verificação

Atualize **Notifications antes de Identity**, pois os fluxos do lote usam os propósitos `admin-invitation`, `admin-notice` e `api-client-notice` no envelope v1. Aplique explicitamente as migrations `AddAdminInvitations`, `AddApiClients` e `AddApiClientDeactivationAudit` com a credencial migradora de Identity; não use a role de runtime para migrations. A migration de auditoria é `20261028091043_AddApiClientDeactivationAudit`.

A readiness de Identity verifica conexão e existência das tabelas `identity.parameters`, `identity.email_intents`, `identity.admin_invitations` e `identity.api_clients` por consultas `SELECT 1`; a consulta de clients referencia `deactivated_at` para exigir a coluna dessa migration. Isso não transforma a probe em validação genérica de todas as colunas ou versões de schema. Permanecem conexões sem pooling, limites existentes e ausência de reset global de pools.

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class "*ApiClientRegistrationTests"
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class "*ClientCredentialsTests"
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class "*ApiClientDeactivationTests"
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-method "*Api_client*"
```

O QA da #37 aprovou 16 casos de cadastro com PostgreSQL real e três cenários completos de mensageria: novo aviso de integrador, convite e regressão de cadastro/reset. Identity usa TestServer; os cenários de entrega usam RabbitMQ, processo Notifications, banco próprio e arquivos `.eml` reais, com API sob IdentityRuntime e migrations separadas. A arquitetura passou em 339 casos; build sem avisos/erros e formatação foram conferidos. Esses resultados não representam execução da suíte completa de integração.

O QA da #39 aprovou uma seleção de 95 casos: 18 de emissão/separação de principais, mais regressões de login, refresh, reuso, logout, emissor, rotação e consulta do usuário; também passaram 346 casos de arquitetura. HTTP usa TestServer e PostgreSQL é real, incluindo corrida e timeout SQL. Build, formatação e diff foram conferidos. Essa seleção não incluiu injeção de falha do coletor de diagnósticos nem equivale à suíte completa.

A validação final do lote aprovou **1030/1030 testes**, sendo **677 de integração e 353 de arquitetura**, sem falhas ou ignorados. Os 13 cenários de desativação cobrem auditoria, token anterior e nova emissão recusados, autorização, idempotência concorrente, rollback de intenção, falha de publicação, timeout SQL com `503` e recuperação, negociação `Accept`, logs e rate limit. HTTP de Identity usa TestServer; PostgreSQL, RabbitMQ, processo Notifications e `.eml` são reais nos cenários pertinentes. Build sem avisos/erros, formatação e diff foram conferidos. Essa evidência não implica outro serviço consumidor implementado nem entrega exatamente uma vez no provedor de e-mail.

[Voltar à documentação](../README.md)
