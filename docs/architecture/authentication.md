# Autenticação JWT e autorização por políticas

## Contexto

Clientes e administradores usam o Ouroboros por dois hosts, a API externa e o backoffice ([Hosts](hosts.md)). Cada host precisa validar o access token e aplicar autorização por papel sem que cada endpoint repita regras, e um endpoint esquecido sem anotação não pode ficar exposto. No monólito, o mesmo processo emite e valida os tokens.

## Decisão

- **JWT assinado com HS256** (`Microsoft.AspNetCore.Authentication.JwtBearer`). A chave simétrica tem no mínimo 256 bits e fica fora do Git.
- **Uma audiência por host:** `ouroboros-api` na API externa e `ouroboros-backoffice` no backoffice. Um token só é aceito no host da sua audiência.
- **Nega por padrão:** a política de fallback de cada host exige usuário autenticado; no backoffice, exige também o papel `Admin`. Um endpoint só é anônimo se declarar `[AllowAnonymous]`.
- **Papéis fixos** `Customer` e `Admin`, um por usuário (o enum `UserRole` do [domínio de identidade](identity.md#modelo-do-usuário)), no claim `role`.
- **Políticas nomeadas por capacidade** (por exemplo `identity:admins.invite`), com o mapeamento de papel para política num único lugar do módulo dono.
- **Access token de 15 minutos e refresh token de 7 dias**, configuráveis e validados na subida.

## Configuração

Seção `Authentication:Jwt`, lida por [`JwtOptions`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Security/JwtOptions.cs):

| Chave | Onde fica | Valor atual |
| --- | --- | --- |
| `Issuer` | `appsettings.json` de cada host | `ouroboros` nos dois. |
| `Audience` | `appsettings.json` de cada host | `ouroboros-api` em [`Ouroboros.Api`](../../src/Ouroboros.Api/appsettings.json); `ouroboros-backoffice` em [`Ouroboros.Backoffice.Api`](../../src/Ouroboros.Backoffice.Api/appsettings.json). |
| `AccessTokenLifetime` | `appsettings.json` de cada host | `00:15:00` (15 minutos). |
| `RefreshTokenLifetime` | `appsettings.json` de cada host | `7.00:00:00` (7 dias); define a expiração do refresh token emitido no login ([#12](https://github.com/adnerscarpelini/ouroboros-server/issues/12)) e em cada renovação, contada a partir dela ([#15](https://github.com/adnerscarpelini/ouroboros-server/issues/15)). |
| `SigningKey` | Fora do Git: user-secrets em desenvolvimento, variável `Authentication__Jwt__SigningKey` nos demais ambientes | Chave aleatória de pelo menos 32 bytes. |

Os dois hosts compartilham o `UserSecretsId`, então leem a mesma `SigningKey` em desenvolvimento. A chave é usada como texto: seus bytes em UTF-8 formam a chave HMAC, então uma string Base64 de 64 caracteres equivale a 64 bytes. Como gerar e gravar a chave: [README](../../README.md#chave-de-assinatura-dos-tokens).

O `JwtOptionsValidator` roda na subida (`ValidateOnStart`) e impede o host de subir, com `OptionsValidationException` e mensagem que cita a chave, quando:

- `SigningKey` está ausente, vazia ou só com espaços (a mensagem aponta o README);
- `SigningKey` tem menos de 32 bytes em UTF-8 (256 bits);
- `Issuer` ou `Audience` não foram configurados;
- `AccessTokenLifetime` ou `RefreshTokenLifetime` não são positivos.

## Validação do token

O Identity registra o esquema JwtBearer em `AddIdentityModule` ([`DependencyInjection.cs`](../../src/Modules/Ouroboros.Modules.Identity/DependencyInjection.cs)), com a audiência do host em que roda:

| Regra | Configuração |
| --- | --- |
| Algoritmo | `ValidAlgorithms = [HS256]`; `alg=none`, HS384 e qualquer outro são recusados. |
| Assinatura | `RequireSignedTokens` e `ValidateIssuerSigningKey` com a `SigningKey`. |
| Emissor e audiência | `ValidateIssuer` e `ValidateAudience`, com os valores do host. |
| Validade | `ValidateLifetime` e `RequireExpirationTime` (token sem `exp` é recusado), com `ClockSkew` de 30 segundos para `exp` e `nbf`. |
| Claims | `MapInboundClaims = false`: os claims mantêm os nomes curtos do JWT; `NameClaimType = sub` e `RoleClaimType = role`. |
| Resposta | `IncludeErrorDetails = false`: o `401` não explica o motivo da recusa (o `WWW-Authenticate` não traz `error_description`). |

Token ausente ou inválido responde `401`; token válido sem o papel ou a política exigida responde `403`. As duas respostas saem em ProblemDetails com `traceId` ([Respostas de erro](error-responses.md#respostas-sem-corpo-401-403-404-405-415)). Nenhum token nem a chave aparecem nos logs, inclusive no nível `Trace` (verificado no QA da #9).

## Emissão do access token

[`AccessTokenIssuer`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Security/AccessTokenIssuer.cs) (`internal`, singleton) emite o access token com `JsonWebTokenHandler`, assinado em HS256, com `iss`, `aud` (a audiência do host), `iat`, `nbf`, `exp` (agora + `AccessTokenLifetime`) e os claims:

| Claim | Conteúdo |
| --- | --- |
| `sub` | `ExternalId` do usuário. |
| `role` | `Customer` ou `Admin`. |
| `sid` | `ExternalId` da sessão. |
| `jti` | Identificador único do token (UUIDv7). |

`Issue(user, sessionId)` retorna o token e o instante de expiração. Os nomes dos claims ficam em [`IdentityClaims`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Security/IdentityClaims.cs). Desde a [#12](https://github.com/adnerscarpelini/ouroboros-server/issues/12), o emissor é chamado pelo login, que abre a sessão cujo `ExternalId` vai no `sid` e entrega também um refresh token opaco ([Fazer login com e-mail e senha](../identity/login.md)). Desde a [#15](https://github.com/adnerscarpelini/ouroboros-server/issues/15), o emissor também é chamado pela renovação, que troca o refresh token por um par novo na mesma sessão (mesmo `sid`), com o papel lido do banco e a audiência do host em que a renovação foi feita ([Renovar o access token com rotação do refresh token](../identity/refresh-session.md)). A validação do access token não consulta a sessão: um token emitido vale até expirar, mesmo depois de uma renovação ou do [logout](../identity/logout.md) (#17).

## Autorização

### Política padrão dos hosts

`AddOuroborosHost` ([`OuroborosHostExtensions`](../../src/Ouroboros.Hosting/OuroborosHostExtensions.cs)) define a `FallbackPolicy`, aplicada a todo endpoint sem anotação de autorização:

| Host | Política de fallback |
| --- | --- |
| API externa | Usuário autenticado. |
| Backoffice | Usuário autenticado com o papel `Admin` (`OuroborosHostExtensions.AdminRole`). |

`UseOuroborosHost` inclui `UseAuthentication()` e `UseAuthorization()` no pipeline, antes de `MapControllers()`.

- Um endpoint só é anônimo com `[AllowAnonymous]`. Hoje os únicos anônimos são os dois logins da #12 (`POST /api/v1/identity/sessions` e `POST /api/v1/admin/identity/sessions`, veja [Fazer login com e-mail e senha](../identity/login.md)) as duas renovações da #15 (`POST /api/v1/identity/sessions/refresh` e `POST /api/v1/admin/identity/sessions/refresh`, veja [Renovar o access token com rotação do refresh token](../identity/refresh-session.md)) e os dois logouts da #17 (`POST /api/v1/identity/sessions/logout` e `POST /api/v1/admin/identity/sessions/logout`, veja [Encerrar a sessão atual](../identity/logout.md)).
- A política padrão também vale para requisições que não correspondem a nenhuma rota. Sem token, até uma rota inexistente responde `401`, então a resposta não revela quais rotas existem; com token válido, a mesma rota responde `404`.
- No backoffice, o fallback exige `Admin` em todo endpoint, sem depender de base de controller nem de anotação: um token de `Customer` recebe `403` em qualquer endpoint `api/v1/admin/<module>`, mesmo que o controller não declare nenhuma política. Até a #47, a `AdminControllerBase` de cada módulo também exigia `Admin`; ela foi removida junto com os controllers dos módulos ([Controllers HTTP nos hosts](http-controllers.md#autorização)).

### Políticas por capacidade

As actions exigem uma capacidade, não um papel: `[Authorize(Policy = IdentityPolicies.AdminsInvite)]`. Os nomes das políticas são constantes `public` em `Contracts` do módulo dono, para que os controllers dos hosts as referenciem; no Identity, [`Contracts/IdentityPolicies`](../../src/Modules/Ouroboros.Modules.Identity/Contracts/IdentityPolicies.cs). O módulo dono mapeia cada capacidade para os papéis que a concedem num único lugar, registrado em `Add<Module>Module` com `services.AddAuthorization(...)` ([`DependencyInjection.cs`](../../src/Modules/Ouroboros.Modules.Identity/DependencyInjection.cs) no Identity):

| Política | Papéis |
| --- | --- |
| `identity:admins.invite` | `Admin` |

Nenhum endpoint usa essa política ainda. Uma política própria soma-se ao papel exigido pelo fallback: no backoffice, o usuário precisa atender a todas.

## Alternativas descartadas

- **RS256 com JWKS:** útil quando outros serviços validam o token só com a chave pública. No monólito, o mesmo processo emite e valida o token, então não há esse consumidor, e o par de chaves só acrescentaria gestão de chaves.

## Consequências

- Os hosts não sobem sem uma `SigningKey` válida, inclusive em desenvolvimento e nos testes, que fornecem chaves fictícias de teste.
- Todo endpoint novo exige token, a menos que declare `[AllowAnonymous]`; no backoffice, exige também `Admin`.
- Trocar a `SigningKey` invalida todos os access tokens emitidos.
- A validação depende do Identity: o esquema JwtBearer é registrado por `AddIdentityModule`, que os dois hosts chamam.
- O `401` não diz por que o token foi recusado; o diagnóstico fica nos logs do servidor.

## Testes

Em [`tests/Ouroboros.ArchitectureTests`](../../tests/Ouroboros.ArchitectureTests/Ouroboros.ArchitectureTests.csproj), sem banco:

- [`TestTokens`](../../tests/Ouroboros.ArchitectureTests/TestTokens.cs) gera tokens válidos e propositalmente inválidos, independentemente do emissor do Identity; as factories dos hosts usam a mesma chave fictícia.
- [`AuthorizationTests`](../../tests/Ouroboros.ArchitectureTests/AuthorizationTests.cs):
  - endpoint sem anotação, sem token: `401` em ProblemDetails; token válido: `200`;
  - `alg=none`, outra chave, expirado, outro emissor, outra audiência, HS384, sem `exp`, `nbf` no futuro e expirado além de 30 segundos: `401`; expirado dentro da tolerância: aceito;
  - o `401` não traz `error_description`;
  - token sem o papel exigido pela política: `403`; `Customer` em endpoints do backoffice, com e sem política própria (o controller de teste não tem base nem anotação de papel): `403`; `Admin` nos mesmos endpoints: autorizado;
  - token da API externa no backoffice e vice-versa: `401`;
  - `SigningKey` ausente, vazia, só com espaços ou com menos de 256 bits impede a subida, com mensagem que cita a chave;

Em [`tests/Ouroboros.IntegrationTests`](../../tests/Ouroboros.IntegrationTests/Ouroboros.IntegrationTests.csproj), [`AccessTokenIssuerTests`](../../tests/Ouroboros.IntegrationTests/AccessTokenIssuerTests.cs): o token emitido pelo `AccessTokenIssuer` é aceito pela API externa com `sub`, `role` e `sid`, usa HS256, a audiência do host e validade de 15 minutos. Os testes do login, que emitem tokens pelos dois hosts e verificam a audiência de cada um, estão em [Fazer login com e-mail e senha](../identity/login.md#como-verificar); os da renovação, em [Renovar o access token](../identity/refresh-session.md#como-verificar); os do logout, em [Encerrar a sessão atual](../identity/logout.md#como-verificar).

Na validação da #9, a suíte tinha 91 testes; na da #47, 93; na da #12, 216; na da #15, 252; na da #16, 260; na da #17, 270.

```powershell
dotnet test --project tests/Ouroboros.ArchitectureTests
```

## Referências

- [Policy-based authorization in ASP.NET Core (Microsoft Learn)](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/policies)
- [NIST Role Based Access Control](https://csrc.nist.gov/projects/role-based-access-control)
- [RFC 9700: Best Current Practice for OAuth 2.0 Security](https://www.rfc-editor.org/rfc/rfc9700.html)

## Origem

- [#9](https://github.com/adnerscarpelini/ouroboros-server/issues/9): autenticação JWT e autorização por políticas.
- [#32](https://github.com/adnerscarpelini/ouroboros-server/issues/32): hosts separados, com uma audiência por host.
- [#47](https://github.com/adnerscarpelini/ouroboros-server/issues/47): remoção da `AdminControllerBase` e nomes das políticas em `Contracts`.
- [#12](https://github.com/adnerscarpelini/ouroboros-server/issues/12): login, sessão e refresh token, com os primeiros endpoints anônimos.
- [#15](https://github.com/adnerscarpelini/ouroboros-server/issues/15): renovação do access token com rotação do refresh token, anônima nos dois hosts.
- [#17](https://github.com/adnerscarpelini/ouroboros-server/issues/17): logout da sessão atual, anônimo nos dois hosts.

[Voltar à arquitetura](README.md)
