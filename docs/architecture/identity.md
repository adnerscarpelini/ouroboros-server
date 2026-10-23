# Domínio de identidade próprio e hash de senha

## Contexto

O módulo Identity precisa de usuários com senha, papéis e, mais adiante, login com JWT e refresh token. O ASP.NET Core Identity completo oferece tudo isso pronto, mas com custos para o Ouroboros:

- os endpoints prontos (`MapIdentityApi`) emitem tokens proprietários, que não são JWT, e a Microsoft os descreve como voltados a cenários simples;
- o `IdentityDbContext` e o `IdentityUser` impõem as tabelas `AspNet*` e acoplam o domínio ao framework, em conflito com o [schema por módulo](persistence.md) e com as [convenções das entidades](entity-conventions.md).

Por outro lado, escrever o hash de senha à mão é um risco desnecessário: algoritmo, sal, formato e comparação em tempo constante já estão resolvidos e revisados no framework.

## Decisão

- **Domínio próprio.** O módulo tem a própria entidade `User`, segue as convenções de identificadores, datas e nomes, e implementa os próprios fluxos de JWT ([#9](https://github.com/adnerscarpelini/ouroboros-server/issues/9), veja [Tokens](#tokens)) e de refresh token (com o login, na [#12](https://github.com/adnerscarpelini/ouroboros-server/issues/12)).
- **Do ASP.NET Core Identity, só o `PasswordHasher<T>`**, atrás de uma interface do módulo. Não se usam `IdentityDbContext`, `IdentityUser`, `UserManager` nem `MapIdentityApi`.
- **Iterações no nível da OWASP.** O hasher usa o formato V3 (PBKDF2 com HMAC-SHA512) com `IterationCount = 210.000`, a recomendação da OWASP para PBKDF2-HMAC-SHA512; o padrão do framework é 100.000.
- **Rehash na verificação.** Quando a verificação indica que o hash usa parâmetros mais fracos que os atuais, a senha é aceita e o hash é refeito com os parâmetros atuais.

## Modelo do usuário

[`Domain/Users/User`](../../src/Modules/Ouroboros.Modules.Identity/Domain/Users/User.cs) é `internal sealed` e herda `Entity` (`Id`, `ExternalId`, `CreatedAt`, `UpdatedAt`). É criado por `User.Create(email, passwordHash, role)`:

| Propriedade | Regra |
| --- | --- |
| `Email` | Obrigatório. Normalizado no domínio (sem espaços nas pontas e em minúsculas, com `ToLowerInvariant`); depois da normalização, tem no máximo 254 caracteres (`User.EmailMaxLength`, limite prático da RFC 5321). Vazio, em branco ou longo demais lança `ArgumentException`. |
| `PasswordHash` | Obrigatório; o domínio guarda só o hash, nunca a senha. Muda por `ChangePasswordHash`. |
| `Role` | [`UserRole`](../../src/Modules/Ouroboros.Modules.Identity/Domain/Users/UserRole.cs): `Customer` ou `Admin`, exatamente um por usuário. |
| `IsActive` | Nasce `true`. Ainda não há operação que o altere. |

A unicidade do e-mail sem distinção de maiúsculas vem da normalização: `Cliente@Example.COM` e `cliente@example.com` viram o mesmo valor, e o índice único do banco recai sobre ele. `User.NormalizeEmail` é público dentro do módulo para que buscas por e-mail usem a mesma regra.

## Hash e rehash

| Tipo | Camada | Papel |
| --- | --- | --- |
| [`IPasswordHasher`](../../src/Modules/Ouroboros.Modules.Identity/Application/Security/IPasswordHasher.cs) | `Application/Security` | Porta do módulo: `Hash(password)` e `Verify(passwordHash, providedPassword)`, que retorna `PasswordVerificationOutcome` (`Failed`, `Succeeded`, `SucceededRehashNeeded`). Os tipos do ASP.NET Core Identity não aparecem nela. |
| [`UserPasswordVerifier`](../../src/Modules/Ouroboros.Modules.Identity/Application/Security/UserPasswordVerifier.cs) | `Application/Security` | Verifica a senha de um `User`; em `SucceededRehashNeeded`, gera um hash novo e o atribui com `ChangePasswordHash`. Retorna `true` nos dois casos de sucesso. Não grava: quem chama salva o usuário (o login da #12 fará isso). Em falha, o hash não muda. |
| [`AspNetCorePasswordHasher`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Security/AspNetCorePasswordHasher.cs) | `Infrastructure/Security` | Implementa a porta com `PasswordHasher<User>`, `CompatibilityMode = IdentityV3` e `IterationCount = 210.000`. Traduz `PasswordVerificationResult` para `PasswordVerificationOutcome`. |

O formato V3 grava no próprio hash o algoritmo, o número de iterações e o sal. Por isso o hasher verifica hashes antigos com os parâmetros com que foram gerados e sinaliza o rehash quando as iterações gravadas são menores que as configuradas. Aumentar `IterationCount` no futuro atualiza os hashes aos poucos, a cada login bem-sucedido, sem migração de dados.

`AddIdentityModule` registra `IPasswordHasher` → `AspNetCorePasswordHasher` e `UserPasswordVerifier` como singletons; os dois não guardam estado por requisição.

O `PasswordHasher<T>` vem do shared framework `Microsoft.AspNetCore.App`, que o módulo já referencia (também usado pela autenticação JwtBearer). O pacote `Microsoft.Extensions.Identity.Core`, citado na issue, contém a mesma classe; referenciá-lo seria redundante e gera o aviso NU1510, então ele não foi adicionado.

**Custo:** com 210.000 iterações, cada hash ou verificação consome da ordem de 100 a 500 ms de CPU, conforme a máquina. É intencional (é o que encarece ataques de força bruta) e deve ser considerado em endpoints de login e cadastro.

## Tokens

Desde a [#9](https://github.com/adnerscarpelini/ouroboros-server/issues/9), o Identity emite e valida os access tokens JWT (HS256), conforme [Autenticação JWT e autorização por políticas](authentication.md):

- `AddIdentityModule` registra as opções `Authentication:Jwt` validadas na subida (`JwtOptions` e `JwtOptionsValidator`), o esquema JwtBearer com a audiência do host e as políticas por capacidade do módulo (nomes em [`Contracts/IdentityPolicies`](../../src/Modules/Ouroboros.Modules.Identity/Contracts/IdentityPolicies.cs)).
- [`AccessTokenIssuer`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Security/AccessTokenIssuer.cs), em `Infrastructure/Security`, emite o access token com `sub` (`ExternalId` do usuário), `role` (`Customer` ou `Admin`, o `Role` do `User`), `sid` (`ExternalId` da sessão) e `jti`, válido por 15 minutos.
- O refresh token, a sessão e o login ficam com a [#12](https://github.com/adnerscarpelini/ouroboros-server/issues/12); hoje nenhum endpoint emite tokens.

## E-mail

Desde a [#10](https://github.com/adnerscarpelini/ouroboros-server/issues/10), o Identity envia e-mails pela porta `IEmailSender`, em `Application/Notifications`, com os adaptadores em `Infrastructure/Email`: em `Development`, o `PickupDirectoryEmailSender` grava cada mensagem como `.eml` em `.local/emails`; sem provedor configurado, o `UnconfiguredEmailSender` falha ao enviar. `AddIdentityModule` registra as opções `Identity:Email`, validadas na subida. Os casos de uso enviam só depois de gravar a transação, e o token nunca volta na resposta nem vai para o log. Detalhes em [Envio de e-mail por porta com adaptador local](email.md).

## Persistência

A tabela `identity.users` é mapeada por [`UserConfiguration`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Persistence/Configurations/UserConfiguration.cs) e criada pela migration `AddUsers`:

| Coluna | Tipo | Observação |
| --- | --- | --- |
| `id` | `bigint generated always as identity` | Chave primária `pk_users`; nunca sai do módulo. |
| `external_id` | `uuid` | UUIDv7, índice único `ix_users_external_id`. |
| `email` | `varchar(254)` | Valor normalizado, índice único `ix_users_email`. |
| `password_hash` | `text` | Hash no formato V3, em Base64. |
| `role` | `varchar(20)` | Nome do papel (`Customer`, `Admin`), não o número do enum. |
| `is_active` | `boolean` | |
| `created_at`, `updated_at` | `timestamptz` | Preenchidas pelo interceptor. |

Todas as colunas são `not null`. O `IdentityDbContext` expõe `DbSet<User> Users` e aplica as configurações do assembly com `ApplyConfigurationsFromAssembly` antes do `SnakeCaseNaming` (veja [convenções das entidades](entity-conventions.md#como-criar-uma-entidade-nova)).

## Alternativas descartadas

- **ASP.NET Core Identity completo** (`IdentityDbContext`, `IdentityUser`, `UserManager`, `MapIdentityApi`): menos código inicial, mas tokens que não são JWT, tabelas `AspNet*` fora das convenções do projeto e domínio acoplado ao framework.
- **Hash de senha próprio** (PBKDF2 direto, ou outra biblioteca): mais superfície para erro em sal, formato, comparação e evolução de parâmetros, sem ganho para o projeto.
- **Manter as 100.000 iterações padrão:** abaixo da recomendação atual da OWASP para PBKDF2-HMAC-SHA512.
- **Unicidade no banco sem normalizar no domínio**, com índice em `lower(email)` ou coluna `citext`: deixaria a regra fora do domínio, preservaria a grafia digitada e, no caso do `citext`, exigiria uma extensão do PostgreSQL. A normalização no domínio mantém um índice simples e a mesma regra para gravar e buscar.

## Consequências

- O login (#12) deve usar `UserPasswordVerifier` e salvar o usuário depois da verificação, para persistir um eventual rehash.
- Novos fluxos de credencial (cadastro, troca de senha) geram o hash por `IPasswordHasher.Hash` e nunca recebem o hash de fora do módulo.
- O e-mail é guardado em minúsculas; a grafia original digitada pelo usuário não é preservada.
- Papéis são fixos no enum; mudar para vários papéis por usuário ou papéis dinâmicos exigiria nova decisão e migration.

## Testes

Em [`tests/Ouroboros.IntegrationTests`](../../tests/Ouroboros.IntegrationTests/Ouroboros.IntegrationTests.csproj):

- [`PasswordHashingTests`](../../tests/Ouroboros.IntegrationTests/PasswordHashingTests.cs), sem banco: o número de iterações lido do hash V3 é 210.000; o hash só confere com a senha original; um hash gerado com 100.000 iterações é aceito e refeito com as iterações atuais; uma verificação que falha mantém o hash.
- [`UserPersistenceTests`](../../tests/Ouroboros.IntegrationTests/UserPersistenceTests.cs), contra o PostgreSQL real (exige Docker, veja [Testes de integração](integration-tests.md)): e-mail gravado normalizado, papel gravado como texto e `is_active` verdadeiro; e-mails que diferem só em maiúsculas violam o índice único; e-mail vazio, em branco ou acima do limite é rejeitado.

Na validação da #8, a suíte tinha 58 testes. Os testes dos tokens estão em [Autenticação](authentication.md#testes), e os do e-mail, em [Envio de e-mail](email.md#testes).

## Referências

- [Use Identity to secure a Web API backend for SPAs (Microsoft Learn)](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-api-authorization)
- [Configure ASP.NET Core Identity (Microsoft Learn)](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-configuration)
- [OWASP Password Storage Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html)

## Origem

- [#8](https://github.com/adnerscarpelini/ouroboros-server/issues/8): domínio de identidade próprio com o hasher do ASP.NET Core Identity.
- [#9](https://github.com/adnerscarpelini/ouroboros-server/issues/9): emissão e validação dos access tokens JWT.
- [#10](https://github.com/adnerscarpelini/ouroboros-server/issues/10): envio de e-mails por uma porta com adaptador local.

[Voltar à arquitetura](README.md)
