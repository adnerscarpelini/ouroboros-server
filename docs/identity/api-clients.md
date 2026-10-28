# Integradores e emissão de tokens

Identity cadastra credenciais de sistemas integradores pelo backoffice ([#37](https://github.com/adnerscarpelini/ouroboros-server/issues/37)) e emite seus access tokens no público externo ([#39](https://github.com/adnerscarpelini/ouroboros-server/issues/39)). O integrador é uma entidade própria, não uma conta de usuário. A desativação HTTP e o contrato técnico de validação são a entrega seguinte, #38; ainda não estão implementados nesta fatia.

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

## Emissão por client credentials

`POST /api/v1/identity/oauth/token` é anônimo e existe somente no listener público, acessível também pelo gateway externo. Aceita `application/x-www-form-urlencoded`, com `grant_type=client_credentials`, `client_id` e `client_secret`. Como alternativa, envie o par por HTTP Basic e mantenha somente `grant_type` no formulário. Cada componente de Basic é codificado como formulário antes de montar `client_id:client_secret` e aplicar Base64. Não misture Basic com os campos de credencial no corpo. Fora do ambiente local, use HTTPS para transportar credenciais e tokens.

A resposta `200` contém `access_token`, `token_type` igual a `Bearer` e `expires_in` em segundos, com `Cache-Control: no-store`. Não cria sessão nem emite refresh token. A política `identity-login` aplica o limite configurado de autenticação por IP e público, inicialmente 10 requisições/minuto.

Erros seguem ProblemDetails do projeto com extensão `error`: credencial errada, client inexistente ou inativo retornam o mesmo `401` genérico (`invalid_client`, com desafio Basic); concessão não suportada retorna `400` (`unsupported_grant_type`); `scope` não vazio retorna `400` (`invalid_scope`), pois escopos não estão implementados. Parâmetros de formulário duplicados retornam `400` (`invalid_request`), e autenticação misturada é recusada. JSON não é um formato aceito; a seleção de endpoints pode resultar em recusa de autenticação por fallback, portanto não há contrato de `415` para esse caso.

O caso de uso compara o hash em tempo constante, trava a linha do client e relê hash/estado antes de assinar, usando a porta `IAccessTokenIssuer`. O JWT RS256 usa a audiência pública, `sub=client_id`, `principal_type=client` e `jti`, com validade `Authentication:Jwt:AccessTokenLifetime`; não contém `role` ou `sid` de usuário. Ver [autenticação](../architecture/authentication.md).

## Validação e separação dos principais

`IdentityBearerEvents` valida formatos distintos para usuários e integradores. Um client só é autenticado no esquema público, com RS256 e identificador público válido, sem papel ou sessão. A cada autenticação, Identity consulta o estado atual no próprio banco, sem cache; assinatura válida isoladamente não concede acesso. `Authentication:Jwt:ClientValidationTimeout` é positivo, tem padrão de dois segundos e máximo de cinco. Falha ou timeout nessa consulta fecha o acesso com `401` nesta fatia, sem fallback permissivo.

As políticas padrão e de fallback recusam client em endpoints de usuário. Uma rota destinada a integradores precisa declarar a política explícita `identity:api-clients.access`. A sonda positiva usada no QA existe somente nos testes: nenhum endpoint de negócio para integradores foi criado. O contrato HTTP técnico para consumidores e seu erro de indisponibilidade serão entregues na #38.

## Implantação e verificação

Atualize **Notifications antes de Identity**, pois seu leitor passa a aceitar `api-client-notice` no envelope v1. Aplique explicitamente `AddApiClients` com a credencial migradora de Identity; não use a role de runtime para migrations.

A readiness de Identity verifica conexão e existência das tabelas `identity.parameters`, `identity.email_intents`, `identity.admin_invitations` e `identity.api_clients` por consultas `SELECT 1`. A inclusão das tabelas das novas funcionalidades não transforma a probe em validação genérica de todas as colunas ou versões de schema. Permanecem conexões sem pooling, limites existentes e ausência de reset global de pools.

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class "*ApiClientRegistrationTests"
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class "*ClientCredentialsTests"
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-method "*Api_client*"
```

O QA da #37 aprovou 16 casos de cadastro com PostgreSQL real e três cenários completos de mensageria: novo aviso de integrador, convite e regressão de cadastro/reset. Identity usa TestServer; os cenários de entrega usam RabbitMQ, processo Notifications, banco próprio e arquivos `.eml` reais, com API sob IdentityRuntime e migrations separadas. A arquitetura passou em 339 casos; build sem avisos/erros e formatação foram conferidos. Esses resultados não representam execução da suíte completa de integração.

O QA da #39 aprovou uma seleção de 95 casos: 18 de emissão/separação de principais, mais regressões de login, refresh, reuso, logout, emissor, rotação e consulta do usuário; também passaram 346 casos de arquitetura. HTTP usa TestServer e PostgreSQL é real, incluindo corrida e timeout SQL. Build, formatação e diff foram conferidos. Essa seleção não incluiu injeção de falha do coletor de diagnósticos nem equivale à suíte completa.

[Voltar à documentação](../README.md)
