# Domínio de identidade próprio e hash de senha

## Contexto

O módulo Identity precisa de usuários com senha, papéis e login com JWT e refresh token. O ASP.NET Core Identity completo oferece tudo isso pronto, mas com custos para o Ouroboros:

- os endpoints prontos (`MapIdentityApi`) emitem tokens proprietários, que não são JWT, e a Microsoft os descreve como voltados a cenários simples;
- o `IdentityDbContext` e o `IdentityUser` impõem as tabelas `AspNet*` e acoplam o domínio ao framework, em conflito com as [convenções das entidades](entity-conventions.md) e a independência do domínio. O schema é uma organização interna do [banco do proprietário](persistence.md).

Por outro lado, escrever o hash de senha à mão é um risco desnecessário: algoritmo, sal, formato e comparação em tempo constante já estão resolvidos e revisados no framework.

## Decisão

- **Domínio próprio.** Identity possui a entidade `User`, credenciais, sessões e tokens. Os contratos e regras estão nas notas de [login](../identity/login.md), [renovação](../identity/refresh-session.md), [revogação por reuso](../identity/refresh-token-reuse.md), [logout](../identity/logout.md), [logout-all](../identity/logout-all.md), [consulta do próprio usuário](../identity/me.md), [cadastro](../identity/registration.md) e [redefinição de senha](../identity/password-reset.md). A consulta do próprio usuário expõe somente `ExternalId` como `id`, `Email` e `Role`.
- **Do ASP.NET Core Identity, só o `PasswordHasher<T>`**, atrás de uma interface do serviço. Não se usam o `IdentityDbContext` do framework, `IdentityUser`, `UserManager` nem `MapIdentityApi`; o contexto próprio do projeto tem o nome `IdentityDbContext`.
- **Iterações no nível da OWASP.** O hasher usa o formato V3 (PBKDF2 com HMAC-SHA512) com `IterationCount = 210.000`, a recomendação da OWASP para PBKDF2-HMAC-SHA512; o padrão do framework é 100.000.
- **Rehash na verificação.** Quando a verificação indica que o hash usa parâmetros mais fracos que os atuais, a senha é aceita e o hash é refeito com os parâmetros atuais.

## Modelo do usuário

[`Domain/Users/User`](../../src/Modules/Ouroboros.Modules.Identity/Domain/Users/User.cs) é `internal sealed` e herda a base `Entity` de [BuildingBlocks](building-blocks.md) (`Id`, `ExternalId`, `CreatedAt`, `UpdatedAt`). É criado por `User.Create(email, passwordHash, role)`:

| Propriedade | Regra |
| --- | --- |
| `Email` | Obrigatório. Normalizado no domínio (sem espaços nas pontas e em minúsculas, com `ToLowerInvariant`); depois da normalização, tem no máximo 254 caracteres (`User.EmailMaxLength`, limite prático da RFC 5321) e precisa atender à regra estrita de [`EmailAddresses.IsValid`](../../src/Modules/Ouroboros.Modules.Identity/Domain/Users/EmailAddresses.cs) (veja abaixo). Vazio ou em branco lança `ArgumentException` na normalização; longo demais ou fora da regra estrita lança `ArgumentException` com `O e-mail não é um endereço válido.` |
| `PasswordHash` | Obrigatório; o domínio guarda só o hash, nunca a senha. Muda por `ChangePasswordHash` (rehash no login) ou pelo `UPDATE` explícito da [redefinição de senha](../identity/password-reset.md#regras-da-redefinição), que não passa pela entidade. |
| `Role` | [`UserRole`](../../src/Modules/Ouroboros.Modules.Identity/Domain/Users/UserRole.cs): `Customer` ou `Admin`, exatamente um por usuário. |
| `IsActive` | Nasce `true`. O login recusa usuário inativo com o mesmo `401` de senha errada, e a [rota `me`](../identity/me.md) responde `401` a um usuário inativo mesmo com access token válido. |
| `FailedLoginCount` | Nasce 0. Falhas de senha seguidas; o incremento é feito por um `UPDATE` atômico no banco, não pela entidade. Zerado por `ResetFailedLogins` no login bem-sucedido e pela redefinição de senha. |
| `LockedUntil` | Nasce nulo. Fim do bloqueio por falhas de senha; `IsLockedAt(now)` indica se ainda vale. Limpo por `ResetFailedLogins` e pela redefinição de senha. Detalhes em [Bloquear a conta](../identity/account-lockout.md). |

A unicidade do e-mail sem distinção de maiúsculas vem da normalização: `Cliente@Example.COM` e `cliente@example.com` viram o mesmo valor, e o índice único do banco recai sobre ele. `User.NormalizeEmail` é público dentro do serviço para que buscas por e-mail usem a mesma regra.

`EmailAddresses.IsValid`, em `Domain/Users`, é a regra estrita de e-mail das contas, a mesma do tipo `Email` dos [parâmetros do sistema](system-parameters.md#regras-de-validação): endereço igual à entrada normalizada, sem nome de exibição, parte local ASCII com `[a-z0-9._%+-]` (1 a 64) e domínio DNS sem ponto final nem literal de IP. Vale em `User.Create`, no `create-admin`, na solicitação de cadastro e na solicitação de redefinição de senha; A conclusão do cadastro (#21) usa o e-mail já validado e gravado no token. `MailAddress` isoladamente aceita formatos que a regra do serviço recusa: aspas, espaços, comentários, nomes de exibição e domínio com ponto final. A normalização não deve redirecionar o link para outra caixa nem permitir contornar a unicidade. E-mails com caracteres fora do ASCII são recusados. Detalhes em [Regra estrita de e-mail](../identity/registration.md#regra-estrita-de-e-mail).

## Hash e rehash

| Tipo | Camada | Papel |
| --- | --- | --- |
| [`IPasswordHasher`](../../src/Modules/Ouroboros.Modules.Identity/Application/Security/IPasswordHasher.cs) | `Application/Security` | Porta do serviço: `Hash(password)` e `Verify(passwordHash, providedPassword)`, que retorna `PasswordVerificationOutcome` (`Failed`, `Succeeded`, `SucceededRehashNeeded`). Os tipos do ASP.NET Core Identity não aparecem nela. |
| [`UserPasswordVerifier`](../../src/Modules/Ouroboros.Modules.Identity/Application/Security/UserPasswordVerifier.cs) | `Application/Security` | Verifica a senha de um `User`; em `SucceededRehashNeeded`, gera um hash novo e o atribui com `ChangePasswordHash`. Retorna `true` nos dois casos de sucesso. Não grava: quem chama salva o usuário, como faz o login. Em falha, o hash não muda. `VerifyWithoutUser(password)` executa uma verificação que sempre falha contra um hash fictício, gerado com os parâmetros atuais na criação do singleton, para que um e-mail inexistente custe o mesmo que uma senha errada. |
| [`AspNetCorePasswordHasher`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Security/AspNetCorePasswordHasher.cs) | `Infrastructure/Security` | Implementa a porta com `PasswordHasher<User>`, `CompatibilityMode = IdentityV3` e `IterationCount = 210.000`. Normaliza a senha em NFKC antes de gerar e de verificar o hash, para que a mesma senha com acento composto ou decomposto confira ([Política de senha](password-policy.md#nfkc-no-hasher)). Traduz `PasswordVerificationResult` para `PasswordVerificationOutcome`. |

O formato V3 grava no próprio hash o algoritmo, o número de iterações e o sal. Por isso o hasher verifica hashes antigos com os parâmetros com que foram gerados e sinaliza o rehash quando as iterações gravadas são menores que as configuradas. Aumentar `IterationCount` no futuro atualiza os hashes aos poucos, a cada login bem-sucedido, sem migração de dados.

`AddIdentityModule` registra `IPasswordHasher` → `AspNetCorePasswordHasher` e `UserPasswordVerifier` como singletons; os dois não guardam estado por requisição.

O `PasswordHasher<T>` vem do shared framework `Microsoft.AspNetCore.App`. Não é necessário adicionar `Microsoft.Extensions.Identity.Core` para usar a mesma classe.

**Custo:** o PBKDF2 consome CPU para encarecer ataques de força bruta. O custo depende do hardware e deve ser medido para dimensionar endpoints de login e cadastro. Na conclusão do cadastro (#21) e na redefinição de senha (#23), o hash é calculado antes de abrir a transação, para não segurá-la durante esse trabalho; na redefinição, também a verificação de que a senha nova não é igual à atual. O login verifica a senha antes de abrir a transação que trava o usuário ([Ordem dos locks](#ordem-dos-locks)). O [limite de requisições por IP](../identity/auth-rate-limiting.md) recusa o excesso antes de qualquer verificação de senha.

## Política de senha

Toda senha nova passa por [`PasswordPolicy`](../../src/Modules/Ouroboros.Modules.Identity/Application/Security/PasswordPolicy.cs), em `Application/Security`, antes do hash: 15 a 128 caracteres contados em code points após NFKC, sem regras de composição, recusada se estiver na lista local de senhas comuns ou se contiver `ouroboros` ou a parte local do e-mail (com 4 ou mais caracteres). Detalhes em [Política de senha](password-policy.md). Usam a política o [`create-admin`](../identity/first-admin.md), a [conclusão do cadastro de cliente](../identity/registration.md#regras-da-conclusão), com o e-mail do token como contexto, e a [redefinição de senha](../identity/password-reset.md#regras-da-redefinição), com o e-mail da conta como contexto.

## Primeiro administrador

Identity executa create-admin para criar o primeiro Admin, com serialização concorrente e validação do domínio permitido em seus parâmetros locais. O comando não é endpoint HTTP; senha permanece fora de argumentos e logs. Ver [primeiro administrador](../identity/first-admin.md).

## Tokens

Identity possui sessões e tokens. Access tokens incluem sub (ExternalId do usuário), role, sid (ExternalId da sessão), jti e validade. Emissão e validação seguem [autenticação entre serviços](authentication.md).

Refresh tokens são opacos de 32 bytes, armazenados por SHA-256, rotacionados atomicamente e revogados em reuso/logout. Renovação respeita o público da sessão. Logout não invalida access tokens já emitidos até expiração. Bloqueio de conta, rehash e reset preservam a coordenação local de usuário, sessões e tokens.

## E-mail

Identity mantém geração, hash, finalidade, validade e consumo dos tokens de cadastro e recuperação. Destinatários e regras pertencem a Identity. A intenção de comunicação é persistida com o estado local e entregue por Notifications, conforme [entrega de e-mail](email.md).

Concluir cadastro consome token e cria usuário de forma atômica, com unicidade de e-mail e sem login automático. Redefinir senha consome token, troca hash, limpa bloqueio e revoga sessões na mesma transação. Falhas de entrega não revertem nem repetem essas operações. Customers se vincula pelo ExternalId, sem FK ou transação entre serviços.

## Persistência

As tabelas `identity.sessions` e `identity.refresh_tokens`, da migration `AddSessions`, estão descritas em [Fazer login com e-mail e senha](../identity/login.md#persistência); a coluna `revoked_at`, da migration `AddRefreshTokenRevocation` (#15), em [Renovar o access token](../identity/refresh-session.md#persistência). A tabela `identity.email_tokens`, da migration `AddEmailTokens` (#20), que guarda só o hash dos tokens de uso único enviados por e-mail (cadastro e redefinição de senha, com `user_id` da conta), está em [Cadastrar cliente por e-mail](../identity/registration.md#persistência). A conclusão do cadastro preenche o `used_at` do token na mesma transação que grava o usuário. Os tokens de redefinição estão em [Redefinir a senha por e-mail](../identity/password-reset.md#persistência); a redefinição preenche o `used_at` do token na mesma transação que troca a senha.

A tabela `identity.users` é mapeada por [`UserConfiguration`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Persistence/Configurations/UserConfiguration.cs), criada pela migration `AddUsers` e ampliada pela `AddAccountLockout` (#13):

| Coluna | Tipo | Observação |
| --- | --- | --- |
| `id` | `bigint generated always as identity` | Chave primária `pk_users`; nunca sai do serviço. |
| `external_id` | `uuid` | UUIDv7, índice único `ix_users_external_id`. |
| `email` | `varchar(254)` | Valor normalizado, índice único `ix_users_email`. |
| `password_hash` | `text` | Hash no formato V3, em Base64. |
| `role` | `varchar(20)` | Nome do papel (`Customer`, `Admin`), não o número do enum. |
| `is_active` | `boolean` | |
| `failed_login_count` | `integer` | Padrão `0`. Contador de falhas de senha.  |
| `locked_until` | `timestamptz`, nulo | Fim do bloqueio.  |
| `created_at`, `updated_at` | `timestamptz` | Preenchidas pelo interceptor. |

Todas as colunas são `not null`, exceto `locked_until`. O `IdentityDbContext` expõe `DbSet<User> Users`, `DbSet<Session> Sessions`, `DbSet<RefreshToken> RefreshTokens` e `DbSet<EmailToken> EmailTokens` e aplica as configurações do assembly com `ApplyConfigurationsFromAssembly` antes do `SnakeCaseNaming` (veja [convenções das entidades](entity-conventions.md#como-criar-uma-entidade-nova)).

## Ordem dos locks

O serviço trava linhas numa ordem única, sempre dentro de uma transação:

| Fluxo | Locks, em ordem |
| --- | --- |
| [Login](../identity/login.md#transação-com-o-usuário-travado) | O usuário (`LockUserAsync`). |
| [Redefinição de senha](../identity/password-reset.md#regras-da-redefinição) | O usuário (`LockUserAsync`), depois todas as sessões dele em ordem de id (`LockUserSessionsAsync`). |
| [Logout-all](../identity/logout-all.md) | Todas as sessões do usuário em ordem de id (`LockUserSessionsAsync`). |
| [Renovação](../identity/refresh-session.md), [revogação por reuso](../identity/refresh-token-reuse.md) e [logout](../identity/logout.md) | Só a sessão do token (`LockSessionAsync`). |

Quem trava usuário e sessões trava o usuário primeiro; os fluxos que só travam sessões nunca pedem o lock do usuário. Com a mesma ordem em todos os fluxos, duas transações não esperam uma pela outra em ordens opostas.

### Por que travar o usuário

A redefinição de senha precisa ser atômica em relação à abertura de sessão no login. O ponto de serialização impede duas corridas:

- um login com a senha antiga, verificado antes de a redefinição terminar, poderia abrir a sessão depois de a redefinição revogar todos os refresh tokens, mantendo uma sessão viva com a senha trocada;
- um login que refazia o hash da senha antiga poderia gravar esse hash por cima do novo, trazendo a senha antiga de volta.

As duas operações travam a linha do usuário. O login abre a sessão com o usuário travado e confere, sob o lock, que o hash armazenado é o mesmo que verificou e que a conta segue ativa e não bloqueada; senão, responde `401`. A redefinição trava o usuário antes das sessões, relê o hash e `is_active` sob o lock e só então grava. A verificação e o hash da senha, caros, ficam fora das transações.

### Por que `FOR NO KEY UPDATE`

`IdentityDbContext.LockUserAsync` executa `SELECT 1 FROM identity.users WHERE id = ... FOR NO KEY UPDATE`. Esse modo conflita consigo mesmo (serializa login e redefinição do mesmo usuário) e com os `UPDATE` comuns da linha, mas não com o `FOR KEY SHARE` que o PostgreSQL pede na linha referenciada ao inserir uma linha com FK para o usuário (uma sessão, um token de e-mail).

`FOR UPDATE` conflita também com `FOR KEY SHARE` e permitiria deadlock entre uma redefinição em andamento e uma nova solicitação de redefinição da mesma conta: a solicitação revoga o token pendente e, ao inserir o novo token (com FK para o usuário), esperaria o lock do usuário mantido pela redefinição, que, por sua vez, esperaria a linha do token para consumi-lo. Com `FOR NO KEY UPDATE`, a inserção não espera o lock do usuário.

### Alternativas consideradas

- **`FOR UPDATE` no usuário:** descartado pelo deadlock descrito acima.
- **Advisory lock por usuário** (`pg_advisory_xact_lock`), como na solicitação de redefinição: também serializaria sem conflitar com as FKs, mas o lock não fica na linha, e cada fluxo novo teria de repetir a mesma chave; o lock de linha já é o padrão do serviço para sessões.
- **Só conferir o hash ao gravar a sessão**, sem lock: em READ COMMITTED, uma sessão inserida por um login ainda não confirmado não é vista pela revogação da redefinição, e escaparia dela.

### Consequências

- O login segura o lock do usuário só durante a abertura da sessão; logins simultâneos do mesmo usuário se serializam nesse trecho.
- Uma redefinição espera um login em andamento confirmar e revoga a sessão aberta por ele; um login que espera uma redefinição encontra outro hash e responde `401`.
- O logout-all não trava o usuário: um login simultâneo a ele ainda pode deixar uma sessão ativa ([Encerrar todas as sessões](../identity/logout-all.md#limitações)).
- Novos fluxos que alterem a senha ou o estado de acesso do usuário devem travá-lo com `LockUserAsync` e, se também mexerem em sessões, travá-las depois.

Os testes das corridas estão em [`PasswordResetRaceTests`](../../tests/Ouroboros.IntegrationTests/PasswordResetRaceTests.cs) ([Redefinir a senha](../identity/password-reset.md#como-verificar)).

## Alternativas descartadas

- **ASP.NET Core Identity completo** (`IdentityDbContext`, `IdentityUser`, `UserManager`, `MapIdentityApi`): menos código inicial, mas tokens que não são JWT, tabelas `AspNet*` fora das convenções do projeto e domínio acoplado ao framework.
- **Hash de senha próprio** (PBKDF2 direto, ou outra biblioteca): mais superfície para erro em sal, formato, comparação e evolução de parâmetros, sem ganho para o projeto.
- **Manter as 100.000 iterações padrão:** abaixo da recomendação atual da OWASP para PBKDF2-HMAC-SHA512.
- **Unicidade no banco sem normalizar no domínio**, com índice em `lower(email)` ou coluna `citext`: deixaria a regra fora do domínio, preservaria a grafia digitada e, no caso do `citext`, exigiria uma extensão do PostgreSQL. A normalização no domínio mantém um índice simples e a mesma regra para gravar e buscar.

## Consequências

- O login (#12) usa o `UserPasswordVerifier` e salva o usuário junto com a sessão, persistindo um eventual rehash e o contador de falhas zerado; numa transação com o usuário travado e só se o hash armazenado ainda for o verificado. Outros fluxos que verifiquem senha devem fazer o mesmo.
- A revogação do refresh token na renovação também é um `UPDATE` condicional (`ExecuteUpdateAsync`), e não uma alteração da entidade, para que só uma renovação simultânea com o mesmo token vença ([Renovar o access token](../identity/refresh-session.md#regras)).
- Toda escrita em `identity.refresh_tokens` de uma sessão trava antes a linha da sessão (`SELECT ... FOR UPDATE`), na mesma transação e na ordem sessão, depois tokens. Em READ COMMITTED, sem esse lock, a revogação da sessão não enxergaria o token inserido por uma renovação ainda não confirmada. O lock é o método `IdentityDbContext.LockSessionAsync`, usado pela renovação, pela revogação por reuso e pelo logout; novos fluxos devem usá-lo. O logout-all trava todas as sessões do usuário de uma vez, sempre em ordem de id (`IdentityDbContext.LockUserSessionsAsync`), para não haver deadlock entre chamadas simultâneas ([Atomicidade e serialização por sessão](../identity/refresh-token-reuse.md#atomicidade-e-serialização-por-sessão)). Login e redefinição de senha também travam a linha do usuário, antes de qualquer sessão ([Ordem dos locks](#ordem-dos-locks)).
- O contador de falhas é incrementado por SQL direto (`UPDATE ... RETURNING`) na tabela do próprio serviço, e não pela entidade, para que falhas simultâneas não se percam ([Bloquear a conta](../identity/account-lockout.md#contagem-atômica)).
- Fluxos de credencial (conclusão do cadastro, redefinição e troca de senha) validam a senha com `PasswordPolicy` e geram o hash por `IPasswordHasher.Hash`; nunca recebem o hash de fora do serviço.
- O e-mail é guardado em minúsculas; a grafia original digitada pelo usuário não é preservada.
- Papéis são fixos no enum; mudar para vários papéis por usuário ou papéis dinâmicos exigiria nova decisão e migration.

## Testes

Em [`tests/Ouroboros.IntegrationTests`](../../tests/Ouroboros.IntegrationTests/Ouroboros.IntegrationTests.csproj):

- [`PasswordHashingTests`](../../tests/Ouroboros.IntegrationTests/PasswordHashingTests.cs), sem banco: o número de iterações lido do hash V3 é 210.000; o hash só confere com a senha original; um hash gerado com 100.000 iterações é aceito e refeito com as iterações atuais; uma verificação que falha mantém o hash.
- [`UserPersistenceTests`](../../tests/Ouroboros.IntegrationTests/UserPersistenceTests.cs), contra o PostgreSQL real (exige Docker, veja [Testes de integração](integration-tests.md)): e-mail gravado normalizado, papel gravado como texto e `is_active` verdadeiro; e-mails que diferem só em maiúsculas violam o índice único; e-mail vazio, em branco ou acima do limite é rejeitado. Os casos da regra estrita de e-mail (#20) são exercitados pela solicitação de cadastro ([Cadastrar cliente por e-mail](../identity/registration.md#como-verificar)).

Os testes dos tokens estão em [Autenticação](authentication.md#testes); os do e-mail, em [Envio de e-mail](email.md#testes); os da política de senha e do NFKC no hasher, em [Política de senha](password-policy.md#testes); os do primeiro administrador, em [Criar o primeiro administrador](../identity/first-admin.md#como-verificar); os do login, em [Fazer login com e-mail e senha](../identity/login.md#como-verificar); os do bloqueio de conta, em [Bloquear a conta](../identity/account-lockout.md#como-verificar); os do limite de requisições, em [Limitar requisições](../identity/auth-rate-limiting.md#como-verificar); os da renovação, em [Renovar o access token](../identity/refresh-session.md#como-verificar); os da detecção de reuso, em [Revogar a sessão ao detectar reuso](../identity/refresh-token-reuse.md#como-verificar); os do logout, em [Encerrar a sessão atual](../identity/logout.md#como-verificar); os do logout-all, em [Encerrar todas as sessões](../identity/logout-all.md#como-verificar); os da consulta do próprio usuário, em [Consultar o próprio usuário](../identity/me.md#como-verificar); os da solicitação e da conclusão do cadastro, em [Cadastrar cliente por e-mail](../identity/registration.md#como-verificar); e os da solicitação e da conclusão da redefinição de senha, em [Redefinir a senha por e-mail](../identity/password-reset.md#como-verificar).

## Referências

- [Use Identity to secure a Web API backend for SPAs (Microsoft Learn)](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-api-authorization)
- [Configure ASP.NET Core Identity (Microsoft Learn)](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-configuration)
- [OWASP Password Storage Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html)

## Issues relacionadas

- [#8](https://github.com/adnerscarpelini/ouroboros-server/issues/8): domínio de identidade próprio com o hasher do ASP.NET Core Identity.
- [#9](https://github.com/adnerscarpelini/ouroboros-server/issues/9): emissão e validação dos access tokens JWT.
- [#10](https://github.com/adnerscarpelini/ouroboros-server/issues/10): envio de e-mails por uma porta com adaptador local.
- [#11](https://github.com/adnerscarpelini/ouroboros-server/issues/11): política de senha, NFKC no hasher e caso de uso do primeiro administrador.
- [#12](https://github.com/adnerscarpelini/ouroboros-server/issues/12): login, sessões, refresh token e verificação com hash fictício.
- [#13](https://github.com/adnerscarpelini/ouroboros-server/issues/13): contador de falhas e bloqueio da conta no `User`.
- [#14](https://github.com/adnerscarpelini/ouroboros-server/issues/14): limite de requisições por IP dos endpoints anônimos de autenticação.
- [#15](https://github.com/adnerscarpelini/ouroboros-server/issues/15): renovação do access token com rotação do refresh token.
- [#16](https://github.com/adnerscarpelini/ouroboros-server/issues/16): revogação da sessão ao detectar reuso de refresh token e lock da sessão nas escritas de refresh tokens.
- [#17](https://github.com/adnerscarpelini/ouroboros-server/issues/17): logout da sessão atual e lock da sessão no `IdentityDbContext`.
- [#18](https://github.com/adnerscarpelini/ouroboros-server/issues/18): logout de todas as sessões, com lock de todas as sessões do usuário em ordem de id.
- [#19](https://github.com/adnerscarpelini/ouroboros-server/issues/19): consulta do próprio usuário, com lista fechada de campos.
- [#20](https://github.com/adnerscarpelini/ouroboros-server/issues/20): solicitação de cadastro de cliente, tabela `identity.email_tokens`, regra estrita de e-mail e `OpaqueTokens`.
- [#21](https://github.com/adnerscarpelini/ouroboros-server/issues/21): conclusão do cadastro de cliente, com consumo condicional do token e criação do `Customer` na mesma transação.
- [#22](https://github.com/adnerscarpelini/ouroboros-server/issues/22): solicitação de redefinição de senha, só para conta `Customer` ativa, com tokens de redefinição por conta em `identity.email_tokens`.
- [#23](https://github.com/adnerscarpelini/ouroboros-server/issues/23): conclusão da redefinição de senha, lock do usuário com `FOR NO KEY UPDATE` no login e na redefinição e ordem dos locks do serviço.

[Voltar à arquitetura](README.md)
