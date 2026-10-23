# Domínio de identidade próprio e hash de senha

## Contexto

O módulo Identity precisa de usuários com senha, papéis e login com JWT e refresh token. O ASP.NET Core Identity completo oferece tudo isso pronto, mas com custos para o Ouroboros:

- os endpoints prontos (`MapIdentityApi`) emitem tokens proprietários, que não são JWT, e a Microsoft os descreve como voltados a cenários simples;
- o `IdentityDbContext` e o `IdentityUser` impõem as tabelas `AspNet*` e acoplam o domínio ao framework, em conflito com o [schema por módulo](persistence.md) e com as [convenções das entidades](entity-conventions.md).

Por outro lado, escrever o hash de senha à mão é um risco desnecessário: algoritmo, sal, formato e comparação em tempo constante já estão resolvidos e revisados no framework.

## Decisão

- **Domínio próprio.** O módulo tem a própria entidade `User`, segue as convenções de identificadores, datas e nomes, e implementa os próprios fluxos de JWT ([#9](https://github.com/adnerscarpelini/ouroboros-server/issues/9), veja [Tokens](#tokens)) e de sessão e refresh token (com o login, na [#12](https://github.com/adnerscarpelini/ouroboros-server/issues/12), veja [Fazer login com e-mail e senha](../identity/login.md), a renovação com rotação, na [#15](https://github.com/adnerscarpelini/ouroboros-server/issues/15), veja [Renovar o access token com rotação do refresh token](../identity/refresh-session.md), e a revogação da sessão ao detectar reuso, na [#16](https://github.com/adnerscarpelini/ouroboros-server/issues/16), veja [Revogar a sessão ao detectar reuso de refresh token](../identity/refresh-token-reuse.md)).
- **Do ASP.NET Core Identity, só o `PasswordHasher<T>`**, atrás de uma interface do módulo. Não se usam `IdentityDbContext`, `IdentityUser`, `UserManager` nem `MapIdentityApi`.
- **Iterações no nível da OWASP.** O hasher usa o formato V3 (PBKDF2 com HMAC-SHA512) com `IterationCount = 210.000`, a recomendação da OWASP para PBKDF2-HMAC-SHA512; o padrão do framework é 100.000.
- **Rehash na verificação.** Quando a verificação indica que o hash usa parâmetros mais fracos que os atuais, a senha é aceita e o hash é refeito com os parâmetros atuais.

## Modelo do usuário

[`Domain/Users/User`](../../src/Modules/Ouroboros.Modules.Identity/Domain/Users/User.cs) é `internal sealed` e herda a base `Entity` de [BuildingBlocks](building-blocks.md) (`Id`, `ExternalId`, `CreatedAt`, `UpdatedAt`). É criado por `User.Create(email, passwordHash, role)`:

| Propriedade | Regra |
| --- | --- |
| `Email` | Obrigatório. Normalizado no domínio (sem espaços nas pontas e em minúsculas, com `ToLowerInvariant`); depois da normalização, tem no máximo 254 caracteres (`User.EmailMaxLength`, limite prático da RFC 5321). Vazio, em branco ou longo demais lança `ArgumentException`. |
| `PasswordHash` | Obrigatório; o domínio guarda só o hash, nunca a senha. Muda por `ChangePasswordHash`. |
| `Role` | [`UserRole`](../../src/Modules/Ouroboros.Modules.Identity/Domain/Users/UserRole.cs): `Customer` ou `Admin`, exatamente um por usuário. |
| `IsActive` | Nasce `true`. Ainda não há operação que o altere. O login recusa usuário inativo com o mesmo `401` de senha errada. |
| `FailedLoginCount` | Nasce 0. Falhas de senha seguidas; o incremento é feito por um `UPDATE` atômico no banco, não pela entidade. Zerado por `ResetFailedLogins` no login bem-sucedido. |
| `LockedUntil` | Nasce nulo. Fim do bloqueio por falhas de senha; `IsLockedAt(now)` indica se ainda vale. Limpo por `ResetFailedLogins`. Detalhes em [Bloquear a conta](../identity/account-lockout.md). |

A unicidade do e-mail sem distinção de maiúsculas vem da normalização: `Cliente@Example.COM` e `cliente@example.com` viram o mesmo valor, e o índice único do banco recai sobre ele. `User.NormalizeEmail` é público dentro do módulo para que buscas por e-mail usem a mesma regra.

## Hash e rehash

| Tipo | Camada | Papel |
| --- | --- | --- |
| [`IPasswordHasher`](../../src/Modules/Ouroboros.Modules.Identity/Application/Security/IPasswordHasher.cs) | `Application/Security` | Porta do módulo: `Hash(password)` e `Verify(passwordHash, providedPassword)`, que retorna `PasswordVerificationOutcome` (`Failed`, `Succeeded`, `SucceededRehashNeeded`). Os tipos do ASP.NET Core Identity não aparecem nela. |
| [`UserPasswordVerifier`](../../src/Modules/Ouroboros.Modules.Identity/Application/Security/UserPasswordVerifier.cs) | `Application/Security` | Verifica a senha de um `User`; em `SucceededRehashNeeded`, gera um hash novo e o atribui com `ChangePasswordHash`. Retorna `true` nos dois casos de sucesso. Não grava: quem chama salva o usuário, como faz o login. Em falha, o hash não muda. Desde a #12, `VerifyWithoutUser(password)` executa uma verificação que sempre falha contra um hash fictício, gerado com os parâmetros atuais na criação do singleton, para que um e-mail inexistente custe o mesmo que uma senha errada. |
| [`AspNetCorePasswordHasher`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Security/AspNetCorePasswordHasher.cs) | `Infrastructure/Security` | Implementa a porta com `PasswordHasher<User>`, `CompatibilityMode = IdentityV3` e `IterationCount = 210.000`. Desde a #11, normaliza a senha em NFKC antes de gerar e de verificar o hash, para que a mesma senha com acento composto ou decomposto confira ([Política de senha](password-policy.md#nfkc-no-hasher)). Traduz `PasswordVerificationResult` para `PasswordVerificationOutcome`. |

O formato V3 grava no próprio hash o algoritmo, o número de iterações e o sal. Por isso o hasher verifica hashes antigos com os parâmetros com que foram gerados e sinaliza o rehash quando as iterações gravadas são menores que as configuradas. Aumentar `IterationCount` no futuro atualiza os hashes aos poucos, a cada login bem-sucedido, sem migração de dados.

`AddIdentityModule` registra `IPasswordHasher` → `AspNetCorePasswordHasher` e `UserPasswordVerifier` como singletons; os dois não guardam estado por requisição.

O `PasswordHasher<T>` vem do shared framework `Microsoft.AspNetCore.App`, que o módulo já referencia (também usado pela autenticação JwtBearer). O pacote `Microsoft.Extensions.Identity.Core`, citado na issue, contém a mesma classe; referenciá-lo seria redundante e gera o aviso NU1510, então ele não foi adicionado.

**Custo:** com 210.000 iterações, cada hash ou verificação consome da ordem de 100 a 500 ms de CPU, conforme a máquina. É intencional (é o que encarece ataques de força bruta) e deve ser considerado em endpoints de login e cadastro. Desde a #14, o [limite de requisições por IP](../identity/auth-rate-limiting.md) recusa o excesso antes de qualquer verificação de senha.

## Política de senha

Desde a [#11](https://github.com/adnerscarpelini/ouroboros-server/issues/11), toda senha nova passa por [`PasswordPolicy`](../../src/Modules/Ouroboros.Modules.Identity/Application/Security/PasswordPolicy.cs), em `Application/Security`, antes do hash: 15 a 128 caracteres contados em code points após NFKC, sem regras de composição, recusada se estiver na lista local de senhas comuns ou se contiver `ouroboros` ou a parte local do e-mail (com 4 ou mais caracteres). Detalhes em [Política de senha](password-policy.md).

## Primeiro administrador

Desde a [#11](https://github.com/adnerscarpelini/ouroboros-server/issues/11), o Identity expõe o caso de uso [`ICreateFirstAdmin`](../../src/Modules/Ouroboros.Modules.Identity/Contracts/Administrators/ICreateFirstAdmin.cs), em `Contracts/Administrators`, implementado por `CreateFirstAdmin` (`internal`, em `Application/Administrators`) e registrado como scoped em `AddIdentityModule`. Ele cria um `User` ativo com o papel `Admin` só enquanto não existir nenhum `Admin`, com o e-mail no domínio do parâmetro `Identity:AdminInvitations:AllowedEmailDomain`, lido do Settings pelo `ISystemParameters`. É usado pelo comando `create-admin` do host de backoffice ([Criar o primeiro administrador](../identity/first-admin.md)). Por ele, o Identity passou a referenciar o projeto do Settings, usando só `Settings.Contracts`.

## Tokens

Desde a [#9](https://github.com/adnerscarpelini/ouroboros-server/issues/9), o Identity emite e valida os access tokens JWT (HS256), conforme [Autenticação JWT e autorização por políticas](authentication.md):

- `AddIdentityModule` registra as opções `Authentication:Jwt` validadas na subida (`JwtOptions` e `JwtOptionsValidator`), o esquema JwtBearer com a audiência do host e as políticas por capacidade do módulo (nomes em [`Contracts/IdentityPolicies`](../../src/Modules/Ouroboros.Modules.Identity/Contracts/IdentityPolicies.cs)).
- [`AccessTokenIssuer`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Security/AccessTokenIssuer.cs), em `Infrastructure/Security`, emite o access token com `sub` (`ExternalId` do usuário), `role` (`Customer` ou `Admin`, o `Role` do `User`), `sid` (`ExternalId` da sessão) e `jti`, válido por 15 minutos.
- Desde a [#12](https://github.com/adnerscarpelini/ouroboros-server/issues/12), o login emite o access token e um refresh token opaco de 32 bytes, gravado só como SHA-256, e abre uma sessão (tabelas `identity.sessions` e `identity.refresh_tokens`); detalhes em [Fazer login com e-mail e senha](../identity/login.md).
- Desde a [#15](https://github.com/adnerscarpelini/ouroboros-server/issues/15), a renovação troca um refresh token ativo por um par novo na mesma sessão: o token recebido é revogado (`revoked_at`) por um `UPDATE` condicional na mesma transação em que o novo é gravado, e cada host só renova sessões do seu papel; detalhes em [Renovar o access token com rotação do refresh token](../identity/refresh-session.md).
- Desde a [#16](https://github.com/adnerscarpelini/ouroboros-server/issues/16), apresentar na renovação um refresh token já revogado revoga todos os tokens da mesma sessão, numa transação própria, e registra um aviso com os ids externos do usuário e da sessão; renovação e revogação travam a linha da sessão antes de mexer nos tokens; detalhes em [Revogar a sessão ao detectar reuso de refresh token](../identity/refresh-token-reuse.md).
- Desde a [#13](https://github.com/adnerscarpelini/ouroboros-server/issues/13), falhas seguidas de senha bloqueiam a conta por um tempo, com limite e duração lidos dos parâmetros do sistema; durante o bloqueio, a senha real não é verificada. Detalhes em [Bloquear a conta após falhas consecutivas de senha](../identity/account-lockout.md).
- Desde a [#14](https://github.com/adnerscarpelini/ouroboros-server/issues/14), `AddIdentityModule` registra o rate limiter nativo do ASP.NET Core, com uma política de janela fixa por IP para cada endpoint anônimo de autenticação (hoje, `identity-login` e, desde a #15, `identity-refresh`), configurada em `Identity:RateLimiting` e validada na subida; os nomes das políticas ficam em [`Contracts/IdentityRateLimitPolicies`](../../src/Modules/Ouroboros.Modules.Identity/Contracts/IdentityRateLimitPolicies.cs). Detalhes em [Limitar requisições de autenticação por IP](../identity/auth-rate-limiting.md).

## E-mail

Desde a [#10](https://github.com/adnerscarpelini/ouroboros-server/issues/10), o Identity envia e-mails pela porta `IEmailSender`, em `Application/Notifications`, com os adaptadores em `Infrastructure/Email`: em `Development`, o `PickupDirectoryEmailSender` grava cada mensagem como `.eml` em `.local/emails`; sem provedor configurado, o `UnconfiguredEmailSender` falha ao enviar. `AddIdentityModule` registra as opções `Identity:Email`, validadas na subida. Os casos de uso enviam só depois de gravar a transação, e o token nunca volta na resposta nem vai para o log. Detalhes em [Envio de e-mail por porta com adaptador local](email.md).

## Persistência

As tabelas `identity.sessions` e `identity.refresh_tokens`, da migration `AddSessions`, estão descritas em [Fazer login com e-mail e senha](../identity/login.md#persistência); a coluna `revoked_at`, da migration `AddRefreshTokenRevocation` (#15), em [Renovar o access token](../identity/refresh-session.md#persistência).

A tabela `identity.users` é mapeada por [`UserConfiguration`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Persistence/Configurations/UserConfiguration.cs), criada pela migration `AddUsers` e ampliada pela `AddAccountLockout` (#13):

| Coluna | Tipo | Observação |
| --- | --- | --- |
| `id` | `bigint generated always as identity` | Chave primária `pk_users`; nunca sai do módulo. |
| `external_id` | `uuid` | UUIDv7, índice único `ix_users_external_id`. |
| `email` | `varchar(254)` | Valor normalizado, índice único `ix_users_email`. |
| `password_hash` | `text` | Hash no formato V3, em Base64. |
| `role` | `varchar(20)` | Nome do papel (`Customer`, `Admin`), não o número do enum. |
| `is_active` | `boolean` | |
| `failed_login_count` | `integer` | Padrão `0`. Desde a `AddAccountLockout`. |
| `locked_until` | `timestamptz`, nulo | Desde a `AddAccountLockout`. |
| `created_at`, `updated_at` | `timestamptz` | Preenchidas pelo interceptor. |

Todas as colunas são `not null`, exceto `locked_until`. O `IdentityDbContext` expõe `DbSet<User> Users`, `DbSet<Session> Sessions` e `DbSet<RefreshToken> RefreshTokens` e aplica as configurações do assembly com `ApplyConfigurationsFromAssembly` antes do `SnakeCaseNaming` (veja [convenções das entidades](entity-conventions.md#como-criar-uma-entidade-nova)).

## Alternativas descartadas

- **ASP.NET Core Identity completo** (`IdentityDbContext`, `IdentityUser`, `UserManager`, `MapIdentityApi`): menos código inicial, mas tokens que não são JWT, tabelas `AspNet*` fora das convenções do projeto e domínio acoplado ao framework.
- **Hash de senha próprio** (PBKDF2 direto, ou outra biblioteca): mais superfície para erro em sal, formato, comparação e evolução de parâmetros, sem ganho para o projeto.
- **Manter as 100.000 iterações padrão:** abaixo da recomendação atual da OWASP para PBKDF2-HMAC-SHA512.
- **Unicidade no banco sem normalizar no domínio**, com índice em `lower(email)` ou coluna `citext`: deixaria a regra fora do domínio, preservaria a grafia digitada e, no caso do `citext`, exigiria uma extensão do PostgreSQL. A normalização no domínio mantém um índice simples e a mesma regra para gravar e buscar.

## Consequências

- O login (#12) usa o `UserPasswordVerifier` e salva o usuário junto com a sessão, persistindo um eventual rehash e, desde a #13, o contador de falhas zerado; outros fluxos que verifiquem senha devem fazer o mesmo.
- A revogação do refresh token na renovação também é um `UPDATE` condicional (`ExecuteUpdateAsync`), e não uma alteração da entidade, para que só uma renovação simultânea com o mesmo token vença ([Renovar o access token](../identity/refresh-session.md#regras)).
- Desde a #16, toda escrita em `identity.refresh_tokens` de uma sessão trava antes a linha da sessão (`SELECT ... FOR UPDATE`), na mesma transação e na ordem sessão, depois tokens. Em READ COMMITTED, sem esse lock, a revogação da sessão não enxergaria o token inserido por uma renovação ainda não confirmada. Novos fluxos, como o logout (#17), devem seguir a mesma regra ([Atomicidade e serialização por sessão](../identity/refresh-token-reuse.md#atomicidade-e-serialização-por-sessão)).
- O contador de falhas é incrementado por SQL direto (`UPDATE ... RETURNING`) na tabela do próprio módulo, e não pela entidade, para que falhas simultâneas não se percam ([Bloquear a conta](../identity/account-lockout.md#contagem-atômica)).
- Novos fluxos de credencial (cadastro, troca de senha) validam a senha com `PasswordPolicy` e geram o hash por `IPasswordHasher.Hash`; nunca recebem o hash de fora do módulo.
- O e-mail é guardado em minúsculas; a grafia original digitada pelo usuário não é preservada.
- Papéis são fixos no enum; mudar para vários papéis por usuário ou papéis dinâmicos exigiria nova decisão e migration.

## Testes

Em [`tests/Ouroboros.IntegrationTests`](../../tests/Ouroboros.IntegrationTests/Ouroboros.IntegrationTests.csproj):

- [`PasswordHashingTests`](../../tests/Ouroboros.IntegrationTests/PasswordHashingTests.cs), sem banco: o número de iterações lido do hash V3 é 210.000; o hash só confere com a senha original; um hash gerado com 100.000 iterações é aceito e refeito com as iterações atuais; uma verificação que falha mantém o hash.
- [`UserPersistenceTests`](../../tests/Ouroboros.IntegrationTests/UserPersistenceTests.cs), contra o PostgreSQL real (exige Docker, veja [Testes de integração](integration-tests.md)): e-mail gravado normalizado, papel gravado como texto e `is_active` verdadeiro; e-mails que diferem só em maiúsculas violam o índice único; e-mail vazio, em branco ou acima do limite é rejeitado.

Na validação da #8, a suíte tinha 58 testes. Os testes dos tokens estão em [Autenticação](authentication.md#testes); os do e-mail, em [Envio de e-mail](email.md#testes); os da política de senha e do NFKC no hasher, em [Política de senha](password-policy.md#testes); os do primeiro administrador, em [Criar o primeiro administrador](../identity/first-admin.md#como-verificar); os do login, em [Fazer login com e-mail e senha](../identity/login.md#como-verificar); os do bloqueio de conta, em [Bloquear a conta](../identity/account-lockout.md#como-verificar); os do limite de requisições, em [Limitar requisições](../identity/auth-rate-limiting.md#como-verificar); os da renovação, em [Renovar o access token](../identity/refresh-session.md#como-verificar); e os da detecção de reuso, em [Revogar a sessão ao detectar reuso](../identity/refresh-token-reuse.md#como-verificar).

## Referências

- [Use Identity to secure a Web API backend for SPAs (Microsoft Learn)](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-api-authorization)
- [Configure ASP.NET Core Identity (Microsoft Learn)](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-configuration)
- [OWASP Password Storage Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html)

## Origem

- [#8](https://github.com/adnerscarpelini/ouroboros-server/issues/8): domínio de identidade próprio com o hasher do ASP.NET Core Identity.
- [#9](https://github.com/adnerscarpelini/ouroboros-server/issues/9): emissão e validação dos access tokens JWT.
- [#10](https://github.com/adnerscarpelini/ouroboros-server/issues/10): envio de e-mails por uma porta com adaptador local.
- [#11](https://github.com/adnerscarpelini/ouroboros-server/issues/11): política de senha, NFKC no hasher e caso de uso do primeiro administrador.
- [#12](https://github.com/adnerscarpelini/ouroboros-server/issues/12): login, sessões, refresh token e verificação com hash fictício.
- [#13](https://github.com/adnerscarpelini/ouroboros-server/issues/13): contador de falhas e bloqueio da conta no `User`.
- [#14](https://github.com/adnerscarpelini/ouroboros-server/issues/14): limite de requisições por IP dos endpoints anônimos de autenticação.
- [#15](https://github.com/adnerscarpelini/ouroboros-server/issues/15): renovação do access token com rotação do refresh token.
- [#16](https://github.com/adnerscarpelini/ouroboros-server/issues/16): revogação da sessão ao detectar reuso de refresh token e lock da sessão nas escritas de refresh tokens.

[Voltar à arquitetura](README.md)
