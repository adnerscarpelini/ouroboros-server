# Consultar o próprio usuário (me)

## Objetivo

O cliente da API precisa saber quem está autenticado e qual é o seu papel, por exemplo para decidir que telas mostrar. A rota `me` devolve o `id`, o e-mail e o papel do usuário do access token, lidos do banco a cada chamada.

Serviço responsável: Identity. A consulta fica no caso de uso `IGetCurrentUser` do serviço; os controllers da API de Identity (um por público) só leem o usuário do access token, chamam o caso de uso e traduzem o resultado em `200` ou `401` ([Controllers HTTP nas APIs dos serviços](../architecture/http-controllers.md)).

## Rotas

| Listener | Rota | Access token aceito |
| --- | --- | --- |
| Público | `GET /api/v1/identity/me` | De `Customer`, audiência `ouroboros-api` |
| Administrativo | `GET /api/v1/admin/identity/me` | De `Admin`, audiência `ouroboros-backoffice` |

As duas rotas **exigem access token** (`Authorization: Bearer <accessToken>`): não declaram `[AllowAnonymous]` e seguem a política padrão da API: autenticação em ambas e, na rota administrativa, também o papel `Admin`, aplicado por convenção de rota ([Autenticação](../architecture/authentication.md#autorização)). O usuário é o do claim `sub` do token. Não há parâmetros nem corpo.

A rota administrativa permite que o `Admin` consulte os próprios dados.

## Pré-requisitos

- O banco local com as migrations do Identity aplicadas ([Desenvolvimento](../development.md#banco-de-dados-local)). A consulta não cria migration.
- Um access token válido, obtido no [login](login.md) ou na última [renovação](refresh-session.md), da audiência do listener chamado.

## Como usar

Com a API de Identity no ar (listener público em `http://localhost:5141` no perfil padrão) e o access token de um `Customer`:

```http
GET /api/v1/identity/me HTTP/1.1
Host: localhost:5141
Authorization: Bearer <access-token>
```

Exemplo executável no [guia de desenvolvimento](../development.md#consultar-o-próprio-usuário).

No listener administrativo (`http://localhost:5142`), o pedido é o mesmo, na rota `/api/v1/admin/identity/me`, com o access token de um `Admin`. Os valores dos exemplos são fictícios.

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
| `role` | `Customer` no listener público ou `Admin` no administrativo. |

A resposta é uma **lista fechada de campos**, o record `MeResponse` de cada público: nunca traz o id interno (`long`), o hash da senha, tokens nem dados de bloqueio (`FailedLoginCount`, `LockedUntil`). Um teste falha se um campo for adicionado sem atualizar a lista permitida (veja [Como verificar](#como-verificar)).

### Não autenticado (401)

A resposta é `401` em ProblemDetails, com `traceId` e o header `WWW-Authenticate: Bearer`, sem explicar o motivo da recusa ([Respostas de erro](../architecture/error-responses.md#respostas-sem-corpo-401-403-404-405-415)), quando:

- não há header `Authorization`, ou o token é malformado, expirado, de outra assinatura ou de outra audiência (por exemplo, um token de `Customer` no listener administrativo): recusado pela autenticação do listener, antes do controller;
- o token é válido, mas o `sub` não é um UUID;
- o token é válido, mas o usuário do `sub` não existe, deixou de estar ativo ou não tem mais o papel do canal. Por exemplo, um `Admin` rebaixado a `Customer` no banco deixa de ser aceito em `GET /api/v1/admin/identity/me` antes de o token expirar.

Nos casos com token válido, é o controller que responde `401` e acrescenta `WWW-Authenticate: Bearer`, como no `401` da autenticação ([RFC 9110, seção 11.6.1](https://www.rfc-editor.org/rfc/rfc9110.html#section-11.6.1)).

## Regras

- **Dados lidos do banco a cada chamada.** Nada vem do token além do `sub`: o e-mail e o papel são os atuais do banco, não os do momento da emissão.
- **Usuário ativo e com o papel do canal.** A consulta filtra por `ExternalId`, `IsActive` e pelo papel do canal (`LoginChannels.RoleOf`: `Customer` no listener público, `Admin` no administrativo), como fazem o login e a renovação. Sem correspondência, o caso de uso devolve `null` e a API responde `401`.
- **Consulta só dos campos expostos.** Uma consulta sem tracking (`AsNoTracking`) que projeta só `ExternalId`, `Email` e `Role` no record `CurrentUser`; o hash da senha e os dados de bloqueio nem são lidos.
- **Somente leitura.** Nada é gravado: não renova a sessão, não altera o usuário e não escreve log.
- **Sem limite de requisições por IP.** As rotas não têm política de rate limit, ao contrário dos endpoints anônimos de autenticação ([Limitar requisições](auth-rate-limiting.md)); exigem um access token válido.

## Caso de uso exposto

| Tipo | Local | Papel |
| --- | --- | --- |
| [`IGetCurrentUser`](../../src/Services/Identity/Ouroboros.Identity/Contracts/Users/IGetCurrentUser.cs) | `Contracts/Users` (`public`) | `ExecuteAsync(GetCurrentUserRequest(UserId, Channel))`, que devolve `CurrentUser(Id, Email, Role)` ou `null`. `UserId` é o `ExternalId` do usuário, lido do claim `sub`; `Channel` é o `LoginChannel` do público (`CustomerApp` ou `Backoffice`). |
| [`GetCurrentUser`](../../src/Services/Identity/Ouroboros.Identity/Application/Users/GetCurrentUser.cs) | `Application/Users` (`internal`) | Resolve o papel exigido pelo canal com `LoginChannels.RoleOf` e consulta a porta `ICurrentUserReader`, propagando o cancelamento. Registrado como scoped em `AddIdentityModule`. |
| [`ICurrentUserReader`](../../src/Services/Identity/Ouroboros.Identity/Application/Users/ICurrentUserReader.cs) | `Application/Users` (`internal`) | Porta mínima de leitura: `FindActiveAsync(Guid externalId, UserRole role, CancellationToken)` devolve `CurrentUser?`, sem entidades, `IQueryable` ou tipos de persistência. |
| [`CurrentUserReader`](../../src/Services/Identity/Ouroboros.Identity/Infrastructure/Persistence/CurrentUserReader.cs) | `Infrastructure/Persistence` (`internal`) | Adaptador EF Core da porta, registrado como scoped. Filtra pelo identificador público, usuário ativo e papel informado; projeta somente os campos de `CurrentUser`, sem tracking. |
| `MeController` [externo](../../src/Services/Identity/Ouroboros.Identity.Api/Controllers/External/MeController.cs) e [administrativo](../../src/Services/Identity/Ouroboros.Identity.Api/Controllers/Admin/MeController.cs) | `Controllers/External` e `Controllers/Admin` da API de Identity | Action `Get`, sem `[AllowAnonymous]` nem rate limit. Lê o `sub` com `IdentityClaims.Subject`, passa o canal do público e devolve o `MeResponse(Id, Email, Role)` declarado no mesmo arquivo. |

O caso de uso depende da porta interna, sem acessar EF Core ou Infrastructure. A consulta e a projeção ficam no adaptador de Infrastructure; o contrato público e as restrições dos dois públicos permanecem os mesmos. A exceção de `GetCurrentUser` foi retirada da [lista de transição dos testes de arquitetura](../../tests/Ouroboros.ArchitectureTests/Dependencies/KnownViolations.cs), conforme a [matriz de dependências](../architecture/layer-dependencies.md).

## Limitações

- **Sessão encerrada não invalida o access token.** Os access tokens não têm estado e a consulta não verifica a sessão (`sid`): depois de um [logout](logout.md) ou de um [logout-all](logout-all.md), o access token já emitido continua aceito na rota `me` até expirar, no máximo 15 minutos, desde que o usuário siga ativo e com o mesmo papel ([Autenticação](../architecture/authentication.md#emissão-do-access-token)).
- **Só as rotas `me` conferem o banco.** A recusa de usuário inativo ou rebaixado vale para esta consulta; os demais endpoints autenticados, como o logout-all, confiam no token até ele expirar.
- **Sem consulta de outros usuários nem alteração de dados.** A consulta é restrita aos dados do usuário autenticado.

## Como verificar

Execute a suíte de integração pelo [guia de desenvolvimento](../development.md#testes) (exige Docker).

[`MeTests`](../../tests/Ouroboros.IntegrationTests/MeTests.cs), contra o PostgreSQL real, usa o login de cada público nos cenários autenticados existentes e verifica:

- um `Customer` ativo recebe `200` com o seu `id`, e-mail e `role` `Customer`;
- os campos da resposta são exatamente os da lista permitida no teste (`email`, `id`, `role`) e o corpo não contém o access token; adicionar um campo à resposta sem atualizar essa lista reprova o teste;
- um `Customer` desativado no banco depois do login recebe `401` em `application/problem+json`;
- um `Admin` rebaixado a `Customer` no banco depois do login recebe `401` no listener administrativo, com `WWW-Authenticate: Bearer`;
- sem token e com um token que não é JWT, a resposta é `401`;
- um `Admin` consulta os próprios dados pelo listener administrativo, com `role` `Admin` e a mesma lista de campos.

A mesma suíte usa tokens emitidos diretamente por `IAccessTokenIssuer` para verificar:

- usuário inexistente, inativo ou com papel alterado no banco após a emissão, nos dois públicos: seis casos que respondem `401`, com ProblemDetails e `WWW-Authenticate: Bearer`.

Como o emissor escolhe a audiência pelo papel, o token da audiência do outro canal é forjado com a chave de assinatura de teste e verificado nas duas direções: `401` com `WWW-Authenticate: Bearer`, mesmo com usuário ativo e papel adequado ao destino.

A suíte de arquitetura verifica as dependências do fluxo sem a exceção da #78 e a visibilidade interna da porta e do adaptador. A recusa de tokens expirados, de outra assinatura ou de outra audiência pelo listener também é coberta em [Autenticação](../architecture/authentication.md#testes).

## Referências

- [RFC 9110, seção 11.6.1: WWW-Authenticate](https://www.rfc-editor.org/rfc/rfc9110.html#section-11.6.1)
- [OWASP API Security Top 10, API3:2023 Broken Object Property Level Authorization](https://owasp.org/API-Security/editions/2023/en/0xa3-broken-object-property-level-authorization/)

## Issues relacionadas

- [#19](https://github.com/adnerscarpelini/ouroboros-server/issues/19): consultar o próprio usuário.
- [#78](https://github.com/adnerscarpelini/ouroboros-server/issues/78): isolar a consulta e a projeção por porta interna, preservando contratos e restrições.
- Depende de [#12](https://github.com/adnerscarpelini/ouroboros-server/issues/12) (login, que emite o access token).

[Voltar à documentação](../README.md)
