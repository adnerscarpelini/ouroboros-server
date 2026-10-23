# Fazer login com e-mail e senha

## Objetivo

Clientes e administradores entram com e-mail e senha e recebem um access token (JWT) para chamar as rotas protegidas, mais um refresh token opaco. Cada público entra pelo seu host ([Hosts](../architecture/hosts.md)): o cliente pela API externa e o administrador pelo backoffice.

Módulo responsável: Identity. A regra fica no caso de uso `ILogin` do módulo; os controllers dos dois hosts só validam o formato da entrada, chamam o caso de uso com o canal do host e traduzem o resultado em resposta HTTP ([Controllers HTTP nos hosts](../architecture/http-controllers.md)).

## Rotas

| Host | Rota | Papel aceito | Audiência do token |
| --- | --- | --- | --- |
| API externa (`Ouroboros.Api`) | `POST /api/v1/identity/sessions` | `Customer` | `ouroboros-api` |
| Backoffice (`Ouroboros.Backoffice.Api`) | `POST /api/v1/admin/identity/sessions` | `Admin` | `ouroboros-backoffice` |

As duas rotas são anônimas (`[AllowAnonymous]`), as únicas dos hosts até agora, e respondem com `Cache-Control: no-store`, para que nenhum cache guarde os tokens. O access token emitido só é aceito pelo host em que o login foi feito ([Autenticação](../architecture/authentication.md#validação-do-token)).

## Pré-requisitos

- O banco local com as migrations do Identity aplicadas, inclusive a `AddSessions` ([README](../../README.md#banco-de-dados-local)).
- A chave `Authentication:Jwt:SigningKey` configurada ([README](../../README.md#chave-de-assinatura-dos-tokens)).
- Um usuário ativo do papel do host. O `Admin` é criado pelo comando `create-admin` ([Criar o primeiro administrador](first-admin.md)). Ainda não há cadastro de clientes ([#20](https://github.com/adnerscarpelini/ouroboros-server/issues/20) e [#21](https://github.com/adnerscarpelini/ouroboros-server/issues/21)), então o login da API externa só é exercitado hoje com usuários criados pelos testes.

## Como usar

Com o backoffice no ar (`http://localhost:5142` no perfil padrão, veja o [README](../../README.md#executar-os-hosts)) e um `Admin` criado:

```http
POST /api/v1/admin/identity/sessions HTTP/1.1
Host: localhost:5142
Content-Type: application/json

{
  "email": "admin@example.com",
  "password": "<senha>"
}
```

```powershell
# PowerShell
$body = @{ email = 'admin@example.com'; password = '<senha>' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://localhost:5142/api/v1/admin/identity/sessions -ContentType 'application/json' -Body $body
```

Na API externa (`http://localhost:5141`), o pedido é o mesmo, na rota `/api/v1/identity/sessions`, com um `Customer`.

Use o access token no header `Authorization: Bearer <accessToken>` das demais rotas do mesmo host.

### Entrada

| Campo | Regra |
| --- | --- |
| `email` | Obrigatório, até 254 caracteres. O formato não é validado: um e-mail malformado recebe o mesmo `401` de credenciais erradas. Espaços nas pontas e maiúsculas são ignorados, pela mesma normalização do cadastro ([Domínio de identidade](../architecture/identity.md#modelo-do-usuário)). |
| `password` | Obrigatória, até 1024 caracteres. A senha não passa pela [política de senha](../architecture/password-policy.md), que vale só para senhas novas. |

Os limites de tamanho só protegem o servidor de entradas enormes. Campo ausente, vazio ou acima do limite responde `400` com `ValidationProblemDetails` ([Respostas de erro](../architecture/error-responses.md#validação-400)).

### Resposta de sucesso (200)

```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "tokenType": "Bearer",
  "accessTokenExpiresAt": "2026-10-23T12:15:00+00:00",
  "refreshToken": "q3Jm0f2yQyWm8kV6vY0bq1u2b9c4wJx7kP5sT8nZ1aE",
  "refreshTokenExpiresAt": "2026-10-30T12:00:00+00:00"
}
```

| Campo | Conteúdo |
| --- | --- |
| `accessToken` | JWT HS256 com `sub` (`ExternalId` do usuário), `role` (`Customer` ou `Admin`), `sid` (`ExternalId` da sessão aberta) e `jti`, além de `iss`, `aud`, `iat`, `nbf` e `exp` ([Emissão do access token](../architecture/authentication.md#emissão-do-access-token)). |
| `tokenType` | Sempre `Bearer`. |
| `accessTokenExpiresAt` | Instante de expiração do access token: agora + `AccessTokenLifetime` (15 minutos). |
| `refreshToken` | Valor opaco: 32 bytes aleatórios em Base64Url (43 caracteres). |
| `refreshTokenExpiresAt` | Instante de expiração do refresh token: agora + `RefreshTokenLifetime` (7 dias). |

O formato segue a resposta de token da [RFC 6749, seção 5.1](https://www.rfc-editor.org/rfc/rfc6749#section-5.1), com as expirações como instantes em vez de segundos. Os tempos de vida ficam em `Authentication:Jwt` no `appsettings.json` de cada host ([Configuração](../architecture/authentication.md#configuração)). Os valores do exemplo são fictícios.

### Credenciais inválidas (401)

```json
{
  "type": "https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/identity.md#invalid-credentials",
  "title": "E-mail ou senha inválidos.",
  "status": 401,
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

O código [`invalid-credentials`](../errors/identity.md#invalid-credentials) é o mesmo para e-mail inexistente, senha errada, usuário inativo e usuário de outro papel (um `Admin` na API externa, um `Customer` no backoffice). Nenhum token é emitido e nenhuma sessão é aberta.

## Regras

- **O canal define o papel.** Cada controller informa o seu `LoginChannel`: `CustomerApp` na API externa, que aceita só `Customer`, e `Backoffice`, que aceita só `Admin`. É a camada 4 das [camadas de proteção do backoffice](../architecture/hosts.md#camadas-de-proteção-do-backoffice).
- **Mesma resposta para toda falha.** O resultado do caso de uso não diz o motivo da falha, e o controller responde sempre o mesmo `401`, para não revelar se a conta existe.
- **Mesmo custo para e-mail inexistente.** Sem usuário, o `UserPasswordVerifier.VerifyWithoutUser` verifica a senha contra um hash fictício, de uma senha aleatória gerado com os parâmetros atuais do hasher. O hash é calculado na criação do verificador (singleton), e não no primeiro uso, para que o primeiro login com e-mail inexistente não demore mais que os outros. Usuário inativo ou de outro papel também passa pela verificação real da senha antes da recusa.
- **Rehash salvo no login.** Se o hash da senha usa parâmetros mais fracos que os atuais, o verificador o refaz e o login bem-sucedido o grava junto com a sessão ([Hash e rehash](../architecture/identity.md#hash-e-rehash)). Num login recusado, nada é gravado.
- **Uma sessão nova por login.** Cada login bem-sucedido abre uma sessão com o seu primeiro refresh token, sem encerrar as sessões anteriores do usuário; os tokens de logins anteriores continuam válidos.
- **Só o hash do refresh token.** O banco guarda o SHA-256 do refresh token, nunca o valor entregue ao cliente. Um hash rápido basta porque o token tem 256 bits de entropia, ao contrário de uma senha.
- **Sem segredos nos logs.** A senha, o access token e o refresh token não aparecem nos logs, em nenhum nível.

## Caso de uso exposto

| Tipo | Local | Papel |
| --- | --- | --- |
| [`ILogin`](../../src/Modules/Ouroboros.Modules.Identity/Contracts/Sessions/ILogin.cs) | `Contracts/Sessions` (`public`) | `ExecuteAsync(LoginRequest(Email, Password, Channel))`, que retorna `LoginResult`. O mesmo arquivo declara `LoginChannel` (`CustomerApp`, `Backoffice`), `LoginResult` (`Tokens`, `Succeeded` e `InvalidCredentials`, sem motivo da falha) e `SessionTokens` (os dois tokens e as expirações). |
| [`Login`](../../src/Modules/Ouroboros.Modules.Identity/Application/Sessions/Login.cs) | `Application/Sessions` (`internal`) | Implementação, registrada como scoped em `AddIdentityModule`. |
| [`RefreshTokens`](../../src/Modules/Ouroboros.Modules.Identity/Application/Security/RefreshTokens.cs) | `Application/Security` (`internal`) | Gera o refresh token (32 bytes aleatórios em Base64Url) e calcula o seu SHA-256. |
| [`SessionsController` da API externa](../../src/Ouroboros.Api/Controllers/Identity/SessionsController.cs) e [do backoffice](../../src/Ouroboros.Backoffice.Api/Controllers/Identity/SessionsController.cs) | `Controllers/Identity` de cada host | Action `Create`; os DTOs `CreateSessionRequest` e `SessionResponse` e o `type` do erro ficam em `SessionModels.cs`, um por host. |

## Persistência

A migration `AddSessions` cria duas tabelas no schema `identity`, mapeadas por [`SessionConfiguration`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Persistence/Configurations/SessionConfiguration.cs) a partir das entidades [`Session`](../../src/Modules/Ouroboros.Modules.Identity/Domain/Sessions/Session.cs) e [`RefreshToken`](../../src/Modules/Ouroboros.Modules.Identity/Domain/Sessions/RefreshToken.cs), em `Domain/Sessions`. Além das colunas abaixo, as duas têm `id`, `external_id`, `created_at` e `updated_at`, conforme as [convenções das entidades](../architecture/entity-conventions.md); todas as colunas são `not null`.

| Tabela | Coluna | Observação |
| --- | --- | --- |
| `sessions` | `user_id` | FK para `identity.users` (`fk_sessions_users_user_id`, sem exclusão em cascata), com índice `ix_sessions_user_id`. O `external_id` vai no claim `sid`. |
| `refresh_tokens` | `session_id` | FK para `identity.sessions` com exclusão em cascata, índice `ix_refresh_tokens_session_id`. |
| `refresh_tokens` | `token_hash` | `bytea` com os 32 bytes do SHA-256 do token, índice único `ix_refresh_tokens_token_hash`. |
| `refresh_tokens` | `expires_at` | `timestamptz`, instante de expiração do token. |

Uma sessão tem um ou mais refresh tokens: o login cria o primeiro, e a renovação ([#15](https://github.com/adnerscarpelini/ouroboros-server/issues/15)) deverá acrescentar os seguintes.

## Limitações

- **O refresh token ainda não tem uso.** Ele é emitido e gravado, mas não há rota que o troque por um access token novo; a renovação com rotação chega com a [#15](https://github.com/adnerscarpelini/ouroboros-server/issues/15), e a revogação da sessão ao detectar reuso, com a [#16](https://github.com/adnerscarpelini/ouroboros-server/issues/16). Depois de 15 minutos, o cliente precisa fazer login de novo.
- **Não há logout.** As sessões não têm estado de encerramento e não expiram por conta própria; encerrar a sessão atual e todas as sessões fica com a [#17](https://github.com/adnerscarpelini/ouroboros-server/issues/17) e a [#18](https://github.com/adnerscarpelini/ouroboros-server/issues/18). A validação do access token não consulta a sessão: um token emitido vale até expirar.
- **Não há bloqueio de conta nem limite de requisições.** Falhas consecutivas de senha não bloqueiam a conta ([#13](https://github.com/adnerscarpelini/ouroboros-server/issues/13)) e não há limite de requisições por IP ([#14](https://github.com/adnerscarpelini/ouroboros-server/issues/14)). Até lá, o custo do hash (da ordem de 100 a 500 ms de CPU por tentativa, veja [Domínio de identidade](../architecture/identity.md#hash-e-rehash)) é a única barreira contra força bruta, e o endpoint anônimo pode ser usado para consumir CPU do servidor.
- **Refresh token no corpo da resposta.** É aceitável enquanto o único cliente for um aplicativo próprio, sem frontend web; com um frontend web, o cookie `HttpOnly` deverá ser avaliado. Cookies e MFA estão fora do escopo.
- **Tempo de resposta não medido.** Os testes verificam que o e-mail inexistente executa uma verificação de hash, mas não medem a diferença de tempo entre as respostas.

## Como verificar

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests   # exige Docker
```

[`LoginTests`](../../tests/Ouroboros.IntegrationTests/LoginTests.cs), contra o PostgreSQL real, nos dois hosts (o do backoffice sobe pela [`BackofficeIntegrationApiFactory`](../../tests/Ouroboros.IntegrationTests/BackofficeIntegrationApiFactory.cs), com a mesma configuração da `IntegrationApiFactory`):

- login de `Customer` responde `200` com `Cache-Control: no-store`, `tokenType` `Bearer`, access token com `sub`, `role`, `sid` (de uma sessão gravada para o usuário) e audiência `ouroboros-api`, e `accessTokenExpiresAt` igual ao `exp`; o token é aceito pelo mesmo host;
- e-mail com maiúsculas e espaços entra;
- senha errada, e-mail inexistente, usuário inativo, `Admin` na API externa e `Customer` no backoffice recebem o mesmo `401` `invalid-credentials`, sem abrir sessão; e-mail inexistente e senha errada têm a mesma forma de resposta;
- e-mail inexistente executa uma verificação de hash;
- um segundo login abre outra sessão e mantém a anterior, cada uma com o seu refresh token;
- login de `Admin` no backoffice emite token com audiência `ouroboros-backoffice`;
- o refresh token tem 32 bytes, e o banco guarda só o seu SHA-256;
- um hash com parâmetros antigos é refeito e gravado no login;
- e-mail ou senha ausente responde `400`;
- os logs, capturados no nível `Trace` pelo [`ListLoggerProvider`](../../tests/Ouroboros.IntegrationTests/ListLoggerProvider.cs), não contêm a senha nem os tokens, num login bem-sucedido e num recusado.

Na validação da #12, a suíte tinha 216 testes.

## Referências

- [OWASP Authentication Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html)
- [RFC 9700: Best Current Practice for OAuth 2.0 Security](https://www.rfc-editor.org/rfc/rfc9700.html)
- [RFC 6749, seção 5.1: resposta de token](https://www.rfc-editor.org/rfc/rfc6749#section-5.1)

## Origem

- [#12](https://github.com/adnerscarpelini/ouroboros-server/issues/12): fazer login com e-mail e senha.

[Voltar à documentação](../README.md)
