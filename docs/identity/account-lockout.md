# Bloquear a conta após falhas consecutivas de senha

## Objetivo

Impedir que um atacante teste senhas indefinidamente contra a mesma conta, mesmo trocando de IP. Complementa o [limite de requisições por IP](auth-rate-limiting.md), que contém tentativas espalhadas por várias contas a partir do mesmo endereço. Depois de um número de falhas de senha seguidas, a conta fica bloqueada por um tempo, e o [login](login.md) a recusa mesmo com a senha correta.

Módulo responsável: Identity. A regra fica no caso de uso `Login` e na classe [`AccountLockout`](../../src/Modules/Ouroboros.Modules.Identity/Application/Sessions/AccountLockout.cs), em `Application/Sessions`; não há rota própria. O bloqueio vale para as duas rotas de login, a da API externa e a do backoffice, porque o contador fica no usuário.

## Comportamento

Com os valores iniciais dos parâmetros (5 falhas, 15 minutos):

- **Cinco falhas seguidas bloqueiam a conta por 15 minutos.** A quinta falha grava `locked_until` = agora + 15 minutos e zera o contador na mesma instrução.
- **Conta bloqueada recebe o mesmo `401`.** Durante o bloqueio, o login responde o mesmo [`invalid-credentials`](../errors/identity.md#invalid-credentials) de uma senha errada, também com a senha correta; nenhuma sessão é aberta. A resposta não indica que a conta está bloqueada.
- **A senha real não é verificada durante o bloqueio.** A verificação roda contra o hash fictício do `UserPasswordVerifier.VerifyWithoutUser`, a mesma usada para e-mail inexistente, para que o custo da resposta seja o mesmo e a senha real não possa ser testada. Tentativas durante o bloqueio não somam falhas (veja [Limitações](#limitações)).
- **O bloqueio expira sozinho.** A partir do instante gravado em `locked_until`, a senha correta volta a funcionar; o login bem-sucedido limpa `locked_until`.
- **A redefinição de senha remove o bloqueio.** Concluir a [redefinição de senha](password-reset.md#concluir-a-redefinição) zera o contador e limpa `locked_until` na mesma transação que troca a senha: quem provou controlar o e-mail entra com a senha nova na hora, sem esperar o fim do bloqueio. Um bloqueio atingido enquanto a redefinição calcula o hash da senha nova também é removido por ela.
- **Login bem-sucedido zera o contador.** Quatro falhas seguidas de um acerto voltam o contador a zero; a próxima falha conta como a primeira.

### O que conta como falha

| Situação | Efeito no contador |
| --- | --- |
| Senha errada | Soma uma falha, inclusive para usuário inativo e para usuário de outro papel (um `Admin` com senha errada na API externa soma falha na própria conta). |
| Senha correta, usuário ativo do papel do host | Login bem-sucedido: zera o contador e limpa um bloqueio já vencido. |
| Senha correta, usuário inativo ou de outro papel | `401`, sem somar nem zerar. |
| Conta bloqueada (qualquer senha) | `401`, sem somar; a senha real não é verificada. |
| E-mail inexistente | `401`; não há contador. |

### Ordem das verificações no login

1. Lê os dois parâmetros de bloqueio, em todo login, com ou sem usuário.
2. Busca o usuário pelo e-mail normalizado.
3. Sem usuário ou com a conta bloqueada: verifica a senha contra o hash fictício e responde `401`.
4. Senha errada: registra a falha e responde `401`.
5. Usuário inativo ou de outro papel: responde `401`.
6. Abre uma transação, trava a linha do usuário e relê o hash da senha, `is_active` e `locked_until`. Se o hash mudou desde a verificação (por exemplo, numa [redefinição de senha](password-reset.md)), a conta foi desativada ou ficou bloqueada no intervalo, responde `401`, sem somar falha e sem abrir sessão ([Transação com o usuário travado](login.md#transação-com-o-usuário-travado)).
7. Zera o contador, abre a sessão e emite os tokens ([Fazer login com e-mail e senha](login.md#regras)).

### Contagem atômica

`AccountLockout.RegisterFailureAsync` delega a gravação à porta `ISessionStore`; o adaptador `SessionStore`, em Infrastructure, registra a falha com um único `UPDATE identity.users ... RETURNING`: soma 1 ao contador e, se o resultado atingir o limite, grava `locked_until` e zera o contador na mesma instrução, além de atualizar `updated_at`. Falhas simultâneas para a mesma conta não se perdem, porque o incremento é feito pelo banco sobre o valor atual da linha, e não lido e regravado pela aplicação. O `UPDATE` é SQL direto sobre a tabela do próprio módulo, sem tocar outro schema.

### Log do bloqueio

Quando uma falha bloqueia a conta, o `AccountLockout` registra um log `Warning` com o `ExternalId` do usuário e o fim do bloqueio. O log nunca contém o e-mail nem a senha.

## Configuração

O limite e a duração são [parâmetros locais de Identity](parameters.md), lidos pelo seu `ISystemParameters`, sem cache:

| Chave | Valor inicial | Tipo e faixa | Efeito de alterar |
| --- | --- | --- | --- |
| `Identity:Lockout:MaxFailedAttempts` | `5` | `Integer`, de 1 a 99 | Vale a partir da próxima falha de senha; não libera contas já bloqueadas. |
| `Identity:Lockout:DurationMinutes` | `15` | `Integer`, de 1 a 1440 | Vale para os próximos bloqueios; não muda o fim dos bloqueios em andamento. |

As faixas vêm do `validation_pattern` de cada parâmetro, aplicado na gravação ([Regras de validação](../architecture/system-parameters.md#regras-de-validação)). Ainda não há endpoint para alterá-los ([#34](https://github.com/adnerscarpelini/ouroboros-server/issues/34)).

Os parâmetros são lidos no início de todo login, antes de buscar o usuário. Assim, se um deles estiver ausente ou com valor que não seja inteiro positivo (gravado fora da aplicação), todo login falha da mesma forma, com `500` e o ProblemDetails genérico de erro inesperado ([Respostas de erro](../architecture/error-responses.md)), para qualquer e-mail: o sistema não libera tentativas ilimitadas, e nem a resposta nem o custo das leituras indicam se a conta existe.

## Pré-requisitos

As migrations de Identity aplicadas ([Desenvolvimento](../development.md#banco-de-dados-local)):

- `AddAccountLockout`, do Identity: as colunas do contador e do bloqueio em `identity.users`.
- `OwnIdentityParameters`: os dois parâmetros e seu histórico no proprietário, copiados do legado ou criados com revisão 0 numa instalação limpa. Banco legado exige o [procedimento com backup e validação](parameters.md#upgrade-de-banco-existente).

Sem os parâmetros locais disponíveis, todo login responde `500`, sem fallback para Settings.

## Persistência

Colunas acrescentadas a `identity.users` pela migration `AddAccountLockout`, mapeadas por [`UserConfiguration`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Persistence/Configurations/UserConfiguration.cs) a partir de [`User`](../../src/Modules/Ouroboros.Modules.Identity/Domain/Users/User.cs):

| Coluna | Tipo | Conteúdo |
| --- | --- | --- |
| `failed_login_count` | `integer not null`, padrão `0` | Falhas de senha seguidas desde o último login bem-sucedido ou o último bloqueio. Propriedade `User.FailedLoginCount`. |
| `locked_until` | `timestamptz`, nulo | Fim do bloqueio; nulo quando a conta nunca foi bloqueada ou já foi liberada por um login ou por uma [redefinição de senha](password-reset.md). Um valor no passado não bloqueia. Propriedade `User.LockedUntil`. |

No domínio, `User.IsLockedAt(now)` indica se `LockedUntil` é posterior ao instante informado, e `User.ResetFailedLogins()` zera o contador e limpa `LockedUntil`; o login bem-sucedido grava essa mudança no mesmo `SaveChanges` da sessão. A redefinição de senha não passa pela entidade: grava `failed_login_count = 0` e `locked_until = NULL` num `UPDATE` explícito, junto com o hash novo. O incremento não passa pela entidade: o `AccountLockout` solicita o registro à porta, e o `SessionStore` executa o `UPDATE` atômico. Os limites da unidade de trabalho estão em [Persistência do ciclo de sessões](session-persistence.md).

## Limitações

- **Falhas concorrentes ao bloqueio.** Uma requisição que leu o usuário antes de ele ser bloqueado e erra a senha depois ainda soma a falha, mesmo com a conta já bloqueada. Só acontece com tentativas simultâneas; o efeito é o contador começar acima de zero quando o bloqueio vence. Esse comportamento é uma limitação conhecida.
- **Bloqueio provocado por terceiros.** Quem conhece o e-mail pode bloquear a conta por 15 minutos errando a senha, em qualquer um dos dois hosts. É o custo conhecido do bloqueio por conta; bloqueio progressivo, CAPTCHA e notificação ao dono da conta ficaram fora do escopo.
- **O bloqueio protege uma conta, não o servidor.** Tentativas contra várias contas são contidas pelo limite de requisições por IP, da [#14](https://github.com/adnerscarpelini/ouroboros-server/issues/14) ([Limitar requisições de autenticação por IP](auth-rate-limiting.md)). Uma requisição recusada com `429` não chega ao login e não soma falha no contador. Um atacante com muitos IPs ainda pode distribuir tentativas contra várias contas.
- **Não há desbloqueio manual.** O bloqueio termina pelo tempo ou, para clientes, pela [redefinição de senha](password-reset.md). Como não há recuperação de senha para administradores, uma conta `Admin` bloqueada só é liberada pelo tempo.

## Como verificar

Execute a suíte de integração pelo [guia de desenvolvimento](../development.md#testes) (exige Docker).

[`AccountLockoutTests`](../../tests/Ouroboros.IntegrationTests/AccountLockoutTests.cs), contra o PostgreSQL real, na API externa, com relógio falso (`FakeTimeProvider`) e hasher de poucas iterações:

- cinco falhas bloqueiam a conta: a sexta tentativa, com a senha correta, recebe `401` com o mesmo `type` de uma senha errada, o contador fica zerado, `locked_until` fica 15 minutos à frente e nenhuma sessão é aberta;
- com a conta bloqueada, a única verificação feita é contra um hash diferente do hash real do usuário;
- um segundo antes dos 15 minutos o login ainda é recusado; aos 15 minutos funciona e limpa `locked_until`;
- quatro falhas seguidas de um acerto zeram o contador, e a falha seguinte conta 1;
- 30 falhas simultâneas (com o limite em 99) resultam em contador 30;
- com os parâmetros alterados para 2 falhas e 1 minuto, duas falhas bloqueiam por 1 minuto;
- sem o parâmetro `Identity:Lockout:MaxFailedAttempts`, o login responde `500` para um e-mail existente e para um inexistente, sem abrir sessão;
- o log do bloqueio contém o `ExternalId` e nenhum log contém o e-mail ou as senhas.

A remoção do bloqueio pela redefinição é verificada pelos [`PasswordResetCompletionTests`](../../tests/Ouroboros.IntegrationTests/PasswordResetCompletionTests.cs) e pelos [`PasswordResetRaceTests`](../../tests/Ouroboros.IntegrationTests/PasswordResetRaceTests.cs) ([Redefinir a senha](password-reset.md#como-verificar)).

O [`SystemParametersTests`](../../tests/Ouroboros.IntegrationTests/SystemParametersTests.cs) confere que todo parâmetro semeado tem histórico de inclusão e respeita as próprias regras de tipo e padrão ([Parâmetros do sistema](../architecture/system-parameters.md#testes)).

## Referências

- [OWASP Authentication Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html)
- [Configure ASP.NET Core Identity: lockout (Microsoft Learn)](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-configuration)
- [NIST SP 800-63B-4](https://pages.nist.gov/800-63-4/sp800-63b.html)

## Issues relacionadas

- [#13](https://github.com/adnerscarpelini/ouroboros-server/issues/13): bloquear a conta após falhas consecutivas de senha.
- [#23](https://github.com/adnerscarpelini/ouroboros-server/issues/23): a redefinição de senha remove o bloqueio; o login recusa a abertura de sessão se a conta foi bloqueada depois da verificação da senha.

[Voltar à documentação](../README.md)
