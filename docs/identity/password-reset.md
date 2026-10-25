# Redefinir a senha por e-mail

## Objetivo

Um cliente que esqueceu a senha recebe por e-mail um link de uso único e, com ele, escolhe uma senha nova, sem que a API revele se um e-mail tem conta.

O fluxo tem duas etapas:

1. **Solicitar a redefinição** ([#22](https://github.com/adnerscarpelini/ouroboros-server/issues/22)): a pessoa informa o e-mail e, se ele pertence a uma conta de cliente ativa, recebe o link ([Solicitar a redefinição](#solicitar-a-redefinição)).
2. **Concluir a redefinição** ([#23](https://github.com/adnerscarpelini/ouroboros-server/issues/23)): a tela do link envia o token e a senha nova. A senha é trocada, o bloqueio por falhas é removido e todas as sessões do usuário são encerradas ([Concluir a redefinição](#concluir-a-redefinição)).

Até a conclusão, a senha atual continua valendo: a solicitação não altera a conta nem encerra sessões. A conclusão não faz login: o cliente entra em seguida pelo [login](login.md), com a senha nova.

Serviço responsável: Identity. As regras ficam nos casos de uso `IRequestPasswordReset` e `ICompletePasswordReset` do serviço; o controller do listener público só valida a entrada, chama o caso de uso e traduz o resultado em resposta HTTP ([Controllers HTTP nas APIs dos serviços](../architecture/http-controllers.md)).

## Rotas

| Listener | Rota | Autenticação | Etapa |
| --- | --- | --- | --- |
| Público | `POST /api/v1/identity/password-resets` | Anônima (`[AllowAnonymous]`) | Solicitar |
| Público | `POST /api/v1/identity/password-resets/complete` | Anônima (`[AllowAnonymous]`) | Concluir |

As rotas existem só no listener público (o administrativo as trata como rota inexistente). Contas de backoffice não são recuperadas por elas (veja [Contas de backoffice](#contas-de-backoffice)).

## Pré-requisitos

- O banco local com as migrations de Identity aplicadas ([Desenvolvimento](../development.md#banco-de-dados-local)): a tabela dos tokens é `identity.email_tokens`, criada por `AddEmailTokens`, e `OwnIdentityParameters` cria ou copia os parâmetros e histórico para Identity. Para banco legado, siga o [procedimento de transferência](parameters.md#upgrade-de-banco-existente) antes de ativar a versão nova.
- Uma conta de cliente ativa. Em desenvolvimento, crie uma pelo [cadastro](registration.md#fluxo-completo-em-desenvolvimento).
- Para ver o e-mail enviado em desenvolvimento, a API de Identity no ambiente `Development`, que grava cada mensagem como `.eml` em `.local/emails/` ([Envio de e-mail](../architecture/email.md)). Fora de `Development`, sem provedor configurado, o envio falha e a resposta continua `202`.

## Configuração

Dois [parâmetros do sistema](../architecture/system-parameters.md#valores-iniciais-e-novas-chaves), lidos localmente de Identity a cada solicitação pelo seu `ISystemParameters`, sem cache:

| Chave | Valor inicial | Uso |
| --- | --- | --- |
| `Identity:PasswordReset:TokenLifetimeMinutes` | `60` | Validade do link, em minutos; inteiro de 1 a 1440. Vale para os próximos links; os já enviados mantêm a validade com que foram gerados. |
| `Identity:PasswordReset:CompletionUrl` | `http://localhost:3000/senha/redefinir` | Endereço da tela do front-end que redefine a senha. O sistema acrescenta `?token=<token>`, então o valor não pode ter query string nem fragmento. |

O limite de 1 a 1440 minutos é conferido pelo padrão do parâmetro na gravação e também pelo caso de uso, para um valor gravado direto no banco. A URL é conferida só na gravação, pelo padrão `https?://[^\s?#]+` do parâmetro. A conclusão não lê parâmetros: a validade já está gravada no token.

Os limites de requisições por IP ficam em `Identity:RateLimiting:Policies` ([Limitar requisições](auth-rate-limiting.md#configuração)): `identity-password-reset` (5 por minuto por IP) na solicitação e `identity-password-reset-completion` (10 por minuto por IP) na conclusão.

## Fluxo completo em desenvolvimento

O [guia de desenvolvimento](../development.md#redefinir-a-senha-em-desenvolvimento) contém o fluxo executável, com leitura do token no .eml e conclusão pela API.

## Solicitar a redefinição

```http
POST /api/v1/identity/password-resets HTTP/1.1
Host: localhost:5141
Content-Type: application/json

{ "email": "cliente@example.com" }
```

O corpo aceita só `email`.

### Solicitação aceita (202)

```json
{
  "message": "Se houver uma conta com esse e-mail, você receberá um link para redefinir a senha."
}
```

A resposta é **sempre a mesma**, com ou sem conta para o e-mail e mesmo que o envio do e-mail falhe. Quando o e-mail é de uma conta de cliente ativa, chega à caixa de entrada da conta uma mensagem com o assunto `Redefinição de senha do Ouroboros` e um link como:

```text
http://localhost:3000/senha/redefinir?token=<token>
```

A mensagem informa a validade em minutos, que o link só pode ser usado uma vez e que a senha atual continua valendo até a redefinição ser concluída. Em desenvolvimento, abra o `.eml` mais recente de `.local/emails/` ([Fluxo completo em desenvolvimento](#fluxo-completo-em-desenvolvimento)).

### E-mail inválido (400)

Responde `400` com `ValidationProblemDetails` e `traceId`, sem gerar token nem enviar e-mail ([Respostas de erro](../architecture/error-responses.md#validação-400)):

- e-mail ausente, vazio, só com espaços ou acima de 254 caracteres: recusado pela validação automática do `[ApiController]` (`[Required]` e `[MaxLength(254)]` no DTO), com a mensagem padrão do ASP.NET Core no campo `Email`;
- e-mail fora da [regra estrita de e-mail](registration.md#regra-estrita-de-e-mail) das contas: recusado pelo caso de uso, com a mensagem `Informe um e-mail válido.` no campo `Email`.

O `400` revela só que o formato é inválido, nunca se o e-mail tem conta. Um endereço equivalente ao de uma conta, como `"cliente"@example.com`, `cliente@example.com.` ou `Cliente <cliente@example.com>`, é recusado aqui e não recebe o link.

### Limite excedido na solicitação (429)

Acima de 5 solicitações por minuto do mesmo IP, a resposta é `429` com `Retry-After`, antes de qualquer consulta ao banco ([Limitar requisições](auth-rate-limiting.md#resposta-acima-do-limite-429)).

### Erro interno na solicitação (500)

Parâmetro ausente ou com validade fora de 1 a 1440 faz toda solicitação responder `500` ([Respostas de erro](../architecture/error-responses.md#erro-inesperado-500)), qualquer que seja o e-mail.

### Regras da solicitação

1. **Parâmetros primeiro.** O caso de uso lê `TokenLifetimeMinutes` e `CompletionUrl` antes de qualquer decisão. Parâmetro ausente ou inválido lança exceção para qualquer e-mail, então a falha também não distingue conta existente de inexistente.
2. **Normalização e formato.** O e-mail é normalizado como no domínio (sem espaços nas pontas e em minúsculas) e precisa atender à [regra estrita](registration.md#regra-estrita-de-e-mail); senão, `400`.
3. **Só conta de cliente ativa recebe o link.** O caso de uso procura um usuário com o e-mail normalizado, ativo e com o papel `Customer`. E-mail sem conta, conta inativa e conta de backoffice (`Admin`) recebem o mesmo `202`, sem token nem e-mail.
4. **Token novo invalida os pendentes.** Para uma conta encontrada, numa única transação:
   - um `pg_advisory_xact_lock` sobre o id da conta serializa solicitações simultâneas da mesma conta;
   - os tokens de redefinição ainda pendentes da conta (finalidade `PasswordReset`, sem `used_at` nem `revoked_at`) recebem `revoked_at` (e `updated_at`);
   - o token novo é gravado com `user_id` da conta, o e-mail da conta e validade de `TokenLifetimeMinutes` a partir de agora.

   Por isso sobra sempre um único token de redefinição pendente por conta, mesmo com solicitações simultâneas. Tokens de cadastro não são afetados: os dois tipos são independentes. A solicitação não espera o lock do usuário mantido por um login ou por uma conclusão em andamento ([Ordem dos locks](../architecture/identity.md#por-que-for-no-key-update)).
5. **Token de uso único, gravado só como hash.** O token tem 32 bytes aleatórios em Base64Url, gerados pelo `OpaqueTokens`, o mesmo do cadastro e do refresh token; o banco guarda só o SHA-256 ([Persistência](#persistência)).
6. **O link vai para o e-mail da conta.** O destinatário é o e-mail gravado no usuário, nunca o texto recebido na requisição.
7. **Envio depois do commit.** O link (`<CompletionUrl>?token=<token>`, com o token escapado para URL) é enviado pela porta `IEmailSender` só depois de a transação ser confirmada ([Envio de e-mail](../architecture/email.md)).
8. **Falha no envio não muda a resposta.** Se o envio falhar, a falha vai para o log em nível `Error` (`Falha ao enviar o e-mail de redefinição de senha (<tipo da exceção>).`), sem o e-mail, o link, o token nem a mensagem da exceção, e a resposta continua `202`. O token gravado fica pendente; a pessoa pode pedir de novo. Um cancelamento da requisição não é tratado como falha de envio.
9. **O token nunca sai por outro caminho.** Não aparece na resposta nem nos logs, em nenhum nível.
10. **A senha atual continua valendo.** A solicitação não altera o hash da senha, não bloqueia a conta nem encerra sessões.

## Concluir a redefinição

```http
POST /api/v1/identity/password-resets/complete HTTP/1.1
Host: localhost:5141
Content-Type: application/json

{ "token": "<token do link>", "password": "outra frase longa que só eu conheço" }
```

| Campo | Regra no DTO |
| --- | --- |
| `token` | Opcional no DTO, até 256 caracteres. Ausente, nulo ou vazio recebe a resposta de [token inválido](#token-inválido-400). |
| `password` | Opcional no DTO, até 1024 caracteres. Ausente ou nulo é tratado como senha vazia e recebe [senha recusada](#senha-recusada-400). O limite real da senha é o da [política de senha](../architecture/password-policy.md) (128 code points); o `[MaxLength]` só protege o servidor de entradas enormes. |

Acima de 256 ou 1024 caracteres, a resposta é o `400` de validação padrão do `[ApiController]` (`ValidationProblemDetails`, com o campo `Token` ou `Password`), sem chegar ao caso de uso ([Respostas de erro](../architecture/error-responses.md#validação-400)).

### Senha redefinida (204)

Sem corpo. A senha foi trocada, o token foi consumido, o contador de falhas foi zerado, o bloqueio foi removido e todos os refresh tokens ativos do usuário foram revogados. A resposta não traz access token nem refresh token, e nenhuma sessão é criada: o cliente entra em seguida pelo [login](login.md), com a senha nova, mesmo que a conta estivesse bloqueada.

### Token inválido (400)

```json
{
  "type": "https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/identity.md#invalid-password-reset-token",
  "title": "Link de redefinição de senha inválido ou expirado.",
  "status": 400,
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

A mesma resposta, sem `detail`, para token vazio ou ausente, inexistente, expirado, já usado (inclusive por uma conclusão simultânea que venceu), invalidado por uma solicitação mais nova, de outro tipo (por exemplo, de cadastro) ou de um usuário inativo. O token é conferido antes da senha: um token inválido recebe esta resposta mesmo com uma senha fora da política, para não revelar a regra a quem não tem um link válido ([catálogo de erros](../errors/identity.md#invalid-password-reset-token)).

### Senha recusada (400)

```json
{
  "type": "https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/identity.md#password-rejected",
  "title": "A senha não atende à política de senha.",
  "status": 400,
  "detail": "A nova senha não pode ser igual à atual.",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

Só com um token válido. O `detail` traz a regra violada: uma das mensagens da [política de senha](../architecture/password-policy.md) (tamanho, lista de senhas comuns, nome do sistema ou parte local do e-mail da conta) ou `A nova senha não pode ser igual à atual.`. O token não é consumido: a pessoa pode tentar de novo com o mesmo link ([catálogo de erros](../errors/identity.md#password-rejected)).

### Limite excedido na conclusão (429)

Acima de 10 conclusões por minuto do mesmo IP, a resposta é `429` com `Retry-After`, antes de qualquer consulta ao banco ([Limitar requisições](auth-rate-limiting.md#resposta-acima-do-limite-429)).

### Erro interno na conclusão (500)

Uma falha ao gravar responde `500` ([Respostas de erro](../architecture/error-responses.md#erro-inesperado-500)) e desfaz a transação inteira: a senha antiga continua valendo, as sessões continuam ativas e o token continua pendente, podendo ser usado de novo.

### Regras da redefinição

1. **Token e usuário procurados primeiro.** O caso de uso calcula o SHA-256 do token recebido e procura um token com finalidade `PasswordReset`, sem `used_at` nem `revoked_at` e com `expires_at` posterior ao instante atual; depois, o usuário do token (`user_id`), que precisa estar ativo. Sem um dos dois, [token inválido](#token-inválido-400), sem nada gravado. Um token vale até o fim da validade, exclusive. O papel do usuário não é conferido de novo: só contas `Customer` recebem token de redefinição.
2. **Senha validada contra o e-mail da conta.** A senha passa pela mesma `PasswordPolicy` do [cadastro](registration.md#regras-da-conclusão), com o e-mail gravado no usuário como contexto. Recusada, a resposta é [senha recusada](#senha-recusada-400), e o token continua pendente.
3. **Senha igual à atual é recusada.** A senha nova é verificada contra o hash atual; se confere, [senha recusada](#senha-recusada-400) com `A nova senha não pode ser igual à atual.`, e o token continua pendente.
4. **Verificação e hash fora da transação.** A verificação contra a senha atual e o hash da senha nova (`IPasswordHasher`, 210.000 iterações) rodam antes de abrir a transação, para não segurar o lock do usuário durante o trabalho mais caro ([Domínio de identidade](../architecture/identity.md#hash-e-rehash)).
5. **Tudo numa única transação.** Na ordem:
   1. trava a linha do usuário (`LockUserAsync`, `FOR NO KEY UPDATE`) e depois todas as sessões dele, em ordem de id (`LockUserSessionsAsync`), na mesma ordem dos outros fluxos do serviço ([Ordem dos locks](../architecture/identity.md#ordem-dos-locks));
   2. relê, sob o lock, o hash da senha e `is_active`. Conta desativada no intervalo: [token inválido](#token-inválido-400). Se o hash mudou desde a primeira leitura (caso raro), repete a verificação de senha igual à atual contra o hash novo; só nesse caso a verificação roda dentro da transação;
   3. consome o token com um `UPDATE` condicional, que preenche `used_at` (e `updated_at`) só se o token ainda está sem `used_at`, sem `revoked_at` e não expirado. Se nenhuma linha muda (uma conclusão simultânea venceu, ou uma solicitação mais nova revogou o token no intervalo), [token inválido](#token-inválido-400);
   4. grava, com um `UPDATE` explícito em `identity.users`, o `password_hash` novo, `failed_login_count = 0`, `locked_until = NULL` e `updated_at`. O `UPDATE` não depende do rastreamento do EF: grava as três colunas mesmo que um bloqueio tenha sido atingido depois da primeira leitura;
   5. revoga todos os refresh tokens ativos (sem `revoked_at` e não expirados) das sessões do usuário, preenchendo `revoked_at` e `updated_at`;
   6. confirma a transação.

   Nas saídas de token inválido ou senha recusada de dentro da transação, ela é desfeita. Uma falha em qualquer escrita também desfaz tudo ([Erro interno na conclusão](#erro-interno-na-conclusão-500)).
6. **Duas conclusões simultâneas com o mesmo token trocam a senha uma vez.** O `UPDATE` condicional do token deixa só uma vencer; as demais recebem `400`.
7. **Sessões encerradas, inclusive as de logins em andamento.** O login abre a sessão com o mesmo lock do usuário e confere que o hash não mudou desde a verificação ([Transação com o usuário travado](login.md#transação-com-o-usuário-travado)). Um login com a senha antiga que confirma antes da redefinição tem a sessão revogada por ela; um que chega depois recebe `401`. Um rehash feito por esse login não grava o hash antigo por cima do novo.
8. **Access tokens já emitidos valem até expirar.** A redefinição revoga refresh tokens; um access token emitido antes continua aceito até o `exp` (15 minutos), como depois do [logout-all](logout-all.md).
9. **Sem login automático.** Nenhuma sessão nem token de acesso é emitido.
10. **O token e as senhas não aparecem em logs nem na resposta.**

### Contas de backoffice

A recuperação por e-mail do listener público atende somente contas `Customer` ativas. Contas de backoffice não são recuperadas pelo listener público, coerente com o login público, que também aceita só `Customer` ([Fazer login com e-mail e senha](login.md)). A conclusão não confere o papel, porque só a solicitação gera tokens de redefinição. Disponibilidade da recuperação administrativa: [estado de implementação](../implementation-status.md).

## Casos de uso expostos

| Tipo | Local | Papel |
| --- | --- | --- |
| [`IRequestPasswordReset`](../../src/Services/Identity/Ouroboros.Identity/Contracts/PasswordResets/IRequestPasswordReset.cs) | `Contracts/PasswordResets` (`public`) | `ExecuteAsync(RequestPasswordResetRequest(Email))`, que devolve `RequestPasswordResetResult`: `Accepted` (com ou sem envio do link) ou `InvalidEmail` (formato fora da regra estrita). |
| [`RequestPasswordReset`](../../src/Services/Identity/Ouroboros.Identity/Application/PasswordResets/RequestPasswordReset.cs) | `Application/PasswordResets` (`internal`) | Implementação da solicitação, registrada como scoped em `AddIdentityModule`. Declara as chaves dos dois parâmetros. |
| [`ICompletePasswordReset`](../../src/Services/Identity/Ouroboros.Identity/Contracts/PasswordResets/ICompletePasswordReset.cs) | `Contracts/PasswordResets` (`public`) | `ExecuteAsync(CompletePasswordResetRequest(Token, Password))`, que devolve `CompletePasswordResetResult(Outcome, Message)`: `Completed`, `InvalidToken` (sem motivo) ou `PasswordRejected`, com a regra violada em `Message`. |
| [`CompletePasswordReset`](../../src/Services/Identity/Ouroboros.Identity/Application/PasswordResets/CompletePasswordReset.cs) | `Application/PasswordResets` (`internal`) | Implementação da conclusão, registrada como scoped em `AddIdentityModule`. |
| [`EmailToken.ForPasswordReset`](../../src/Services/Identity/Ouroboros.Identity/Domain/EmailTokens/EmailToken.cs) | `Domain/EmailTokens` (`internal`) | Cria o token de redefinição com finalidade `PasswordReset` e `user_id` da conta. |
| [`IPasswordResetStore`](../../src/Services/Identity/Ouroboros.Identity/Application/PasswordResets/IPasswordResetStore.cs) | `Application/PasswordResets` (`internal`) | Porta scoped de consulta, substituição atômica dos pendentes e escritas da conclusão na transação compartilhada de Identity. |
| [`PasswordResetStore`](../../src/Services/Identity/Ouroboros.Identity/Infrastructure/Persistence/PasswordResetStore.cs) | `Infrastructure/Persistence` (`internal`) | Adaptador scoped sobre `IdentityDbContext`; concentra EF, SQL e advisory lock da solicitação. Consumo e troca de senha exigem transação ativa. |
| [`ISessionStore`](../../src/Services/Identity/Ouroboros.Identity/Application/Sessions/ISessionStore.cs) | `Application/Sessions` (`internal`) | Abre a transação da conclusão, trava usuário e sessões, relê o estado vigente e revoga os refresh tokens. Compartilha o contexto com o adaptador de reset ([persistência de sessões](session-persistence.md)). |
| [`PasswordResetsController`](../../src/Services/Identity/Ouroboros.Identity.Api/Controllers/External/PasswordResetsController.cs) | `Controllers/External` da API de Identity | Action `Create`, com `[EnableRateLimiting(IdentityRateLimitPolicies.PasswordReset)]`, e action `Complete` (`POST complete`), com `[EnableRateLimiting(IdentityRateLimitPolicies.PasswordResetCompletion)]`, ambas `[AllowAnonymous]`. Declara no mesmo arquivo o `PasswordResetRequestBody` (só `Email`) e o `CompletePasswordResetBody` (`Token` e `Password`), e reutiliza o `AcceptedResponse(Message)` do cadastro e os `type` de erro de [`SessionModels.cs`](../../src/Services/Identity/Ouroboros.Identity.Api/Controllers/SessionModels.cs). |

## Persistência

Desde a [#76](https://github.com/adnerscarpelini/ouroboros-server/issues/76), os casos de uso de redefinição dependem de portas internas, sem DbContext, EF, SQL ou tipos de Infrastructure. `IPasswordResetStore` é registrado com `PasswordResetStore`; ele e `ISessionStore` são scoped e usam o mesmo `IdentityDbContext` resolvido pela DI.

A solicitação consulta a conta e substitui seus tokens por `ReplacePendingTokenAsync`: advisory lock por conta, revogação dos pendentes e inserção do novo token confirmam na mesma transação. O destinatário continua sendo o e-mail persistido da conta. O envio pela porta local `IEmailSender` ocorre somente após o retorno confirmado da escrita, sem locks durante transporte. A #81 transferiu os parâmetros para o [proprietário Identity](parameters.md); outbox e integração com Notifications pertencem à #85.

Na conclusão, `ISessionStore.BeginTransactionAsync` abre a unidade de trabalho inteira. O caso de uso usa a mesma porta para travar o usuário, depois as sessões em id crescente, e reler hash/estado sob lock. `IPasswordResetStore.ConsumeTokenAsync` e `ChangePasswordAsync` participam dessa transação, sem commit próprio; o adaptador recusa essas escritas sem uma transação ativa. `ISessionStore.RevokeUserTokensAsync` encerra a sequência de escritas antes do commit único. Saída sem commit ou falha desfaz consumo, senha, contador, bloqueio e revogações juntos. A conclusão não chama transporte e pode executar sem Notifications.

Os tokens ficam na tabela `identity.email_tokens`, a mesma do cadastro ([Cadastrar cliente por e-mail](registration.md#persistência)), sem migration nova no Identity. Um token de redefinição tem:

- `purpose` = `PasswordReset`;
- `user_id` preenchido com o id da conta (FK para `identity.users`, com índice), usado para revogar os pendentes da conta e, na conclusão, para encontrar o usuário;
- `email` com o e-mail da conta no momento da solicitação;
- `token_hash` com o SHA-256 do token; o valor do link nunca é gravado;
- `used_at` preenchido pela conclusão, na mesma transação que troca a senha.

A conclusão altera, em `identity.users`, as colunas `password_hash`, `failed_login_count`, `locked_until` e `updated_at`, e, em `identity.refresh_tokens`, `revoked_at` e `updated_at` dos tokens ativos do usuário.

Os dois parâmetros e seu histórico pertencem a Identity desde `OwnIdentityParameters`: a migration copia o legado sem perder revisões ou, na instalação limpa, cria as sementes com revisão 0 ([parâmetros locais](parameters.md)).

## Limitações

- **Diferença de tempo na solicitação.** A resposta é a mesma, mas uma conta encontrada custa mais (transação, gravação e envio) que um e-mail sem conta; medindo o tempo de resposta, ainda é possível inferir se um e-mail tem conta. É uma limitação conhecida, registrada na #22; o limite por IP só reduz a escala da sondagem.
- **Invalidação por terceiros.** Como cada solicitação revoga o link pendente, alguém que conheça o e-mail pode invalidar o link de outra pessoa ao solicitar de novo, limitado pela política de 5 por minuto por IP. A senha atual continua valendo.
- **Access tokens sobrevivem à redefinição.** Os refresh tokens são revogados, mas um access token emitido antes vale até expirar (15 minutos), porque a validação do JWT não consulta a sessão ([Fazer login](login.md#limitações)).
- **Sem recuperação para administradores** (veja [Contas de backoffice](#contas-de-backoffice)).
- **Envio sem outbox.** Se o envio falhar depois do commit, a mensagem se perde, e o token gravado fica pendente sem ter sido entregue ([Envio de e-mail](../architecture/email.md)).
- **Fora de `Development`, nenhum e-mail sai.** Ainda não há adaptador real; todo envio falha (com o mesmo `202`) e vai para o log.
- **Sem aviso de "senha alterada"** por e-mail.
- **Sem front-end.** O link aponta para `http://localhost:3000/senha/redefinir`, que não faz parte deste repositório.

## Como verificar

Execute a suíte de integração pelo [guia de desenvolvimento](../development.md#testes) (exige Docker).

[`PasswordResetRequestTests`](../../tests/Ouroboros.IntegrationTests/PasswordResetRequestTests.cs), contra o PostgreSQL real, com o `IEmailSender` substituído pelo [`CapturingEmailSender`](../../tests/Ouroboros.IntegrationTests/CapturingEmailSender.cs) e um `FakeTimeProvider`:

- conta de cliente ativa, informada com espaços e maiúsculas, recebe `202`, um único e-mail para o endereço da conta com o link `http://localhost:3000/senha/redefinir?token=`, um token cujo SHA-256 está gravado com finalidade `PasswordReset` e `user_id` da conta, e validade de 1 hora;
- e-mail sem conta recebe o mesmo corpo de resposta de uma conta existente, sem e-mail nem token;
- conta inativa e conta `Admin` recebem `202`, sem e-mail (nem token, no caso do `Admin`);
- uma segunda solicitação deixa o primeiro token com `revoked_at`, o segundo pendente e o hash da senha inalterado;
- oito solicitações simultâneas deixam um só token pendente;
- `"cliente"@example.com`, `cliente@example.com.`, `Cliente <cliente@example.com>` e `nao-e-email` recebem `400`, sem e-mail;
- com a política `identity-password-reset` limitada a 1, a segunda solicitação recebe `429`;
- o token não aparece no corpo da resposta nem em nenhum log (nível mínimo `Trace`);
- com falha simulada no envio, a resposta continua `202`, e o log registra a falha.

[`PasswordResetCompletionTests`](../../tests/Ouroboros.IntegrationTests/PasswordResetCompletionTests.cs), contra o PostgreSQL real, com o token lido do `CapturingEmailSender`, um `FakeTimeProvider` e um hasher de poucas iterações:

- um token válido responde `204`, a senha antiga recebe `401` no login, a nova entra, e o token fica com `used_at`;
- com duas sessões abertas, nenhum refresh token fica ativo, e os dois recebem `401` na renovação;
- uma conta bloqueada (com contador 3) entra com a senha nova logo depois, com o contador zerado;
- token já usado, expirado (uma hora depois), invalidado por uma solicitação mais nova, de cadastro, de usuário inativo (sem consumir o token), inexistente, vazio ou nulo recebe o mesmo `400` `invalid-password-reset-token`; com o token expirado, a senha antiga continua valendo;
- senha curta, comum, igual à atual ou nula recebe `400` `password-rejected`, sem consumir o token, que funciona em seguida;
- cinco conclusões simultâneas com o mesmo token e senhas diferentes: só uma recebe `204`, as demais `400`, e só a senha da vencedora entra;
- com falha simulada por trigger na troca da senha ou na revogação de refresh tokens, a resposta é `500` e hash, contador de falhas, bloqueio, token de redefinição e refresh tokens permanecem como antes; removida a falha e liberado o bloqueio de teste, a senha antiga entra, a sessão renova e o mesmo token conclui.

[`PasswordResetRaceTests`](../../tests/Ouroboros.IntegrationTests/PasswordResetRaceTests.cs) força as corridas com um hasher que pausa uma vez numa verificação ou num hash de uma senha escolhida e pode simular que o hash armazenado precisa ser refeito:

- um login com a senha antiga, verificado antes da redefinição e liberado depois dela, recebe `401`, e nenhum refresh token fica ativo;
- o mesmo login com rehash não traz a senha antiga de volta: depois dele, a senha antiga recebe `401` e a nova entra;
- um bloqueio atingido enquanto a redefinição calcula o hash é removido por ela, e a senha nova entra;
- com a linha do usuário travada por `LockUserAsync` numa transação aberta, uma nova solicitação de redefinição responde `202` e envia o e-mail em menos de 10 segundos, sem esperar o lock nem entrar em deadlock.

[`PasswordResetPersistenceTests`](../../tests/Ouroboros.IntegrationTests/PasswordResetPersistenceTests.cs) exercita as portas reais resolvidas pela DI:

- falha por trigger na inserção do token substituto preserva o token pendente anterior e seu destinatário;
- uma conclusão espera o lock real de uma rotação em andamento e, depois do commit dessa rotação, revoga também o refresh token sucessor;
- tentativas de consumir token ou trocar senha fora da transação local compartilhada são recusadas sem consumo.

Os testes de arquitetura verificam a retirada das exceções de persistência da #76 e da dependência de parâmetros de Settings removida pela #81.

Os limites de `identity-password-reset` e `identity-password-reset-completion` são elevados para 100.000 na [`IntegrationApiFactory`](../../tests/Ouroboros.IntegrationTests/IntegrationApiFactory.cs), como os demais ([Testes de integração](../architecture/integration-tests.md)). Não há teste de `429` próprio da conclusão; o mecanismo é o mesmo das outras políticas ([Limitar requisições](auth-rate-limiting.md#como-verificar)).

## Referências

- [OWASP Forgot Password Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Forgot_Password_Cheat_Sheet.html)

## Issues relacionadas

- [#22](https://github.com/adnerscarpelini/ouroboros-server/issues/22): solicitar redefinição de senha.
- [#23](https://github.com/adnerscarpelini/ouroboros-server/issues/23): redefinir a senha com o token.
- [#76](https://github.com/adnerscarpelini/ouroboros-server/issues/76): portas de persistência e unidade de trabalho compartilhada com sessões.
- Depende de [#10](https://github.com/adnerscarpelini/ouroboros-server/issues/10) (envio de e-mail), [#12](https://github.com/adnerscarpelini/ouroboros-server/issues/12) (login), [#13](https://github.com/adnerscarpelini/ouroboros-server/issues/13) (bloqueio da conta) e [#14](https://github.com/adnerscarpelini/ouroboros-server/issues/14) (limite de requisições por IP).

[Voltar à documentação](../README.md)
