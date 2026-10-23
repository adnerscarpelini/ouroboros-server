# Consultar o próprio usuário (me)

## Objetivo

O cliente da API precisa saber quem está autenticado e qual é o seu papel, por exemplo para decidir que telas mostrar. A rota `me` devolve o `id`, o e-mail e o papel do usuário do access token, lidos do banco a cada chamada.

Módulo responsável: Identity. A consulta fica no caso de uso `IGetCurrentUser` do módulo; os controllers dos dois hosts só leem o usuário do access token, chamam o caso de uso e traduzem o resultado em `200` ou `401` ([Controllers HTTP nos hosts](../architecture/http-controllers.md)).

## Rotas

| Host | Rota | Access token aceito |
| --- | --- | --- |
| API externa (`Ouroboros.Api`) | `GET /api/v1/identity/me` | De `Customer`, audiência `ouroboros-api` |
| Backoffice (`Ouroboros.Backoffice.Api`) | `GET /api/v1/admin/identity/me` | De `Admin`, audiência `ouroboros-backoffice` |

As duas rotas **exigem access token** (`Authorization: Bearer <accessToken>`): não declaram `[AllowAnonymous]` e seguem a política padrão de cada host ([Autenticação](../architecture/authentication.md#política-padrão-dos-hosts)). O usuário é o do claim `sub` do token. Não há parâmetros nem corpo.

A rota do backoffice vai além da issue, que pedia só a da API externa, para que o `Admin` também consulte os próprios dados.

## Pré-requisitos

- O banco local com as migrations do Identity aplicadas ([README](../../README.md#banco-de-dados-local)). A consulta não cria migration.
- Um access token válido, obtido no [login](login.md) ou na última [renovação](refresh-session.md), da audiência do host chamado.

## Como usar

Com a API externa no ar (`http://localhost:5141` no perfil padrão) e o access token de um `Customer`:

```http
GET /api/v1/identity/me HTTP/1.1
Host: localhost:5141
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.exemplo.assinatura
```

```powershell
# PowerShell
$headers = @{ Authorization = 'Bearer <access token atual>' }
Invoke-RestMethod -Uri http://localhost:5141/api/v1/identity/me -Headers $headers
```

No backoffice (`http://localhost:5142`), o pedido é o mesmo, na rota `/api/v1/admin/identity/me`, com o access token de um `Admin`. Os valores dos exemplos são fictícios.

### Usuário autenticado (200)

```json
{
  "id": "0199a3b2-7c4e-7d10-9f3a-5b6c7d8e9f01",
  "email": "cliente@example.com",
  "role": "Customer"
}
```

| Campo | Conteúdo |
| --- | --- |
| `id` | `ExternalId` do usuário (UUIDv7), o mesmo do claim `sub`. |
| `email` | E-mail normalizado do usuário ([Modelo do usuário](../architecture/identity.md#modelo-do-usuário)). |
| `role` | `Customer` na API externa ou `Admin` no backoffice. |

A resposta é uma **lista fechada de campos**, o record `MeResponse` de cada host: nunca traz o id interno (`long`), o hash da senha, tokens nem dados de bloqueio (`FailedLoginCount`, `LockedUntil`). Um teste falha se um campo for adicionado sem atualizar a lista permitida (veja [Como verificar](#como-verificar)).

### Não autenticado (401)

A resposta é `401` em ProblemDetails, com `traceId` e o header `WWW-Authenticate: Bearer`, sem explicar o motivo da recusa ([Respostas de erro](../architecture/error-responses.md#respostas-sem-corpo-401-403-404-405-415)), quando:

- não há header `Authorization`, ou o token é malformado, expirado, de outra assinatura ou de outra audiência (por exemplo, um token de `Customer` no backoffice): recusado pela autenticação do host, antes do controller;
- o token é válido, mas o `sub` não é um UUID;
- o token é válido, mas o usuário do `sub` não existe, deixou de estar ativo ou não tem mais o papel do canal (host). Por exemplo, um `Admin` rebaixado a `Customer` no banco deixa de ser aceito em `GET /api/v1/admin/identity/me` antes de o token expirar.

Nos casos com token válido, é o controller que responde `401` e acrescenta `WWW-Authenticate: Bearer`, como no `401` da autenticação ([RFC 9110, seção 11.6.1](https://www.rfc-editor.org/rfc/rfc9110.html#section-11.6.1)).

## Regras

- **Dados lidos do banco a cada chamada.** Nada vem do token além do `sub`: o e-mail e o papel são os atuais do banco, não os do momento da emissão.
- **Usuário ativo e com o papel do canal.** A consulta filtra por `ExternalId`, `IsActive` e pelo papel do canal (`LoginChannels.RoleOf`: `Customer` na API externa, `Admin` no backoffice), como fazem o login e a renovação. Sem correspondência, o caso de uso devolve `null` e o host responde `401`.
- **Consulta só dos campos expostos.** Uma consulta sem tracking (`AsNoTracking`) que projeta só `ExternalId`, `Email` e `Role` no record `CurrentUser`; o hash da senha e os dados de bloqueio nem são lidos.
- **Somente leitura.** Nada é gravado: não renova a sessão, não altera o usuário e não escreve log.
- **Sem limite de requisições por IP.** As rotas não têm política de rate limit, ao contrário dos endpoints anônimos de autenticação ([Limitar requisições](auth-rate-limiting.md)); exigem um access token válido.

## Caso de uso exposto

| Tipo | Local | Papel |
| --- | --- | --- |
| [`IGetCurrentUser`](../../src/Modules/Ouroboros.Modules.Identity/Contracts/Users/IGetCurrentUser.cs) | `Contracts/Users` (`public`) | `ExecuteAsync(GetCurrentUserRequest(UserId, Channel))`, que devolve `CurrentUser(Id, Email, Role)` ou `null`. `UserId` é o `ExternalId` do usuário, lido do claim `sub`; `Channel` é o `LoginChannel` do host (`CustomerApp` ou `Backoffice`). |
| [`GetCurrentUser`](../../src/Modules/Ouroboros.Modules.Identity/Application/Users/GetCurrentUser.cs) | `Application/Users` (`internal`) | Implementação, registrada como scoped em `AddIdentityModule`. |
| [`MeController` da API externa](../../src/Ouroboros.Api/Controllers/Identity/MeController.cs) e [do backoffice](../../src/Ouroboros.Backoffice.Api/Controllers/Identity/MeController.cs) | `Controllers/Identity` de cada host | Action `Get`, sem `[AllowAnonymous]` nem rate limit. Lê o `sub` com `IdentityClaims.Subject`, passa o canal do host e devolve o `MeResponse(Id, Email, Role)` declarado no mesmo arquivo. |

## Limitações

- **Sessão encerrada não invalida o access token.** Os access tokens não têm estado e a consulta não verifica a sessão (`sid`): depois de um [logout](logout.md) ou de um [logout-all](logout-all.md), o access token já emitido continua aceito na rota `me` até expirar, no máximo 15 minutos, desde que o usuário siga ativo e com o mesmo papel ([Autenticação](../architecture/authentication.md#emissão-do-access-token)).
- **Só as rotas `me` conferem o banco.** A recusa de usuário inativo ou rebaixado vale para esta consulta; os demais endpoints autenticados, como o logout-all, confiam no token até ele expirar.
- **Sem consulta de outros usuários nem alteração de dados.** Ficaram fora do escopo da issue.

## Como verificar

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests   # exige Docker
```

[`MeTests`](../../tests/Ouroboros.IntegrationTests/MeTests.cs), contra o PostgreSQL real, com tokens emitidos pelo login de cada host:

- um `Customer` ativo recebe `200` com o seu `id`, e-mail e `role` `Customer`;
- os campos da resposta são exatamente os da lista permitida no teste (`email`, `id`, `role`) e o corpo não contém o access token; adicionar um campo à resposta sem atualizar essa lista reprova o teste;
- um `Customer` desativado no banco depois do login recebe `401` em `application/problem+json`;
- um `Admin` rebaixado a `Customer` no banco depois do login recebe `401` no backoffice, com `WWW-Authenticate: Bearer`;
- sem token e com um token que não é JWT, a resposta é `401`;
- um `Admin` consulta os próprios dados pelo backoffice, com `role` `Admin` e a mesma lista de campos.

A recusa de tokens expirados, de outra assinatura ou de outra audiência pelo host é coberta em [Autenticação](../architecture/authentication.md#testes).

Na validação da #19, a suíte tinha 284 testes.

## Referências

- [RFC 9110, seção 11.6.1: WWW-Authenticate](https://www.rfc-editor.org/rfc/rfc9110.html#section-11.6.1)
- [OWASP API Security Top 10, API3:2023 Broken Object Property Level Authorization](https://owasp.org/API-Security/editions/2023/en/0xa3-broken-object-property-level-authorization/)

## Origem

- [#19](https://github.com/adnerscarpelini/ouroboros-server/issues/19): consultar o próprio usuário.
- Depende de [#12](https://github.com/adnerscarpelini/ouroboros-server/issues/12) (login, que emite o access token).

[Voltar à documentação](../README.md)
