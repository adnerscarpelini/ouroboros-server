# Cadastrar cliente por e-mail

## Objetivo

Clientes se cadastram sozinhos. Para impedir que alguém crie uma conta com o e-mail de outra pessoa e a ative com uma senha que só ele conhece (pre-account takeover), o cadastro começa provando a posse do e-mail: a pessoa informa só o endereço e recebe um link de uso único; a senha é definida depois, ao concluir o cadastro pelo link.

O fluxo tem duas etapas:

1. **Solicitar o cadastro** ([#20](https://github.com/adnerscarpelini/ouroboros-server/issues/20)): a pessoa informa o e-mail e, se ele não tem conta, recebe o link ([Solicitar o cadastro](#solicitar-o-cadastro)).
2. **Concluir o cadastro** ([#21](https://github.com/adnerscarpelini/ouroboros-server/issues/21)): a tela do link envia o token e a senha escolhida; o sistema consome o token e cria um usuário ativo com o papel `Customer` ([Concluir o cadastro](#concluir-o-cadastro)).

Depois disso, o cliente entra pelo [login da API externa](login.md). A conclusão não faz login automático.

Módulo responsável: Identity. As regras ficam nos casos de uso `IRequestRegistration` e `ICompleteRegistration` do módulo; o controller da API externa só valida a entrada, chama o caso de uso e traduz o resultado em resposta HTTP ([Controllers HTTP nos hosts](../architecture/http-controllers.md)).

## Rotas

| Host | Rota | Autenticação | Etapa |
| --- | --- | --- | --- |
| API externa (`Ouroboros.Api`) | `POST /api/v1/identity/registrations` | Anônima (`[AllowAnonymous]`) | Solicitar |
| API externa (`Ouroboros.Api`) | `POST /api/v1/identity/registrations/complete` | Anônima (`[AllowAnonymous]`) | Concluir |

As rotas existem só na API externa: o backoffice não tem cadastro, porque administradores entram por convite ([Criar o primeiro administrador](first-admin.md)).

## Pré-requisitos

- O banco local com as migrations do Identity e do Settings aplicadas ([README](../../README.md#banco-de-dados-local)): a `AddEmailTokens` (Identity) cria a tabela dos tokens, e a `AddRegistrationParameters` (Settings) inclui os parâmetros abaixo. A conclusão não tem migration própria.
- Para ver o e-mail enviado em desenvolvimento, a API externa no ambiente `Development`, que grava cada mensagem como `.eml` em `.local/emails/` ([Envio de e-mail](../architecture/email.md#ver-os-e-mails-em-desenvolvimento)). Fora de `Development`, sem provedor configurado, o envio falha e a resposta continua `202` (veja [Regras da solicitação](#regras-da-solicitação)).

## Configuração

Dois [parâmetros do sistema](../architecture/system-parameters.md#valores-iniciais-e-novas-chaves), lidos do Settings a cada solicitação:

| Chave | Valor inicial | Uso |
| --- | --- | --- |
| `Identity:Registration:TokenLifetimeHours` | `24` | Validade do link, em horas; inteiro de 1 a 168. Vale para os próximos links; os já enviados mantêm a validade com que foram gerados. |
| `Identity:Registration:CompletionUrl` | `http://localhost:3000/cadastro/concluir` | Endereço da tela do front-end que conclui o cadastro. O sistema acrescenta `?token=<token>`, então o valor não pode ter query string nem fragmento. |

O limite de 1 a 168 horas é conferido pelo padrão do parâmetro na gravação e também pelo caso de uso, para um valor gravado direto no banco. A URL é conferida só na gravação, pelo padrão `https?://[^\s?#]+` do parâmetro. A conclusão não lê parâmetros: a validade já está gravada no token.

Os limites de requisições por IP ficam em `Identity:RateLimiting:Policies` ([Limitar requisições](auth-rate-limiting.md#configuração)): `identity-registration` (5 por minuto por IP) na solicitação e `identity-registration-completion` (10 por minuto por IP) na conclusão.

## Fluxo completo em desenvolvimento

Com a API externa no ar em `Development` (`http://localhost:5141` no perfil padrão). Ainda não há front-end; o token é copiado do link do e-mail.

```powershell
# 1. Solicitar o cadastro
Invoke-RestMethod -Method Post -Uri http://localhost:5141/api/v1/identity/registrations `
  -ContentType 'application/json' -Body '{"email":"cliente@example.com"}'

# 2. Ler o token no .eml mais recente (o link termina em ?token=<token>)
$eml = Get-ChildItem .local/emails/*.eml | Sort-Object LastWriteTime | Select-Object -Last 1
$token = [regex]::Match((Get-Content $eml -Raw), 'token=([A-Za-z0-9_-]+)').Groups[1].Value

# 3. Concluir o cadastro com a senha
$body = @{ token = $token; password = 'uma frase longa que só eu conheço' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://localhost:5141/api/v1/identity/registrations/complete `
  -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))

# 4. Entrar
$login = @{ email = 'cliente@example.com'; password = 'uma frase longa que só eu conheço' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://localhost:5141/api/v1/identity/sessions `
  -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($login))
```

O passo 2 roda na raiz do repositório. O `.eml` sai com `Content-Transfer-Encoding: 8bit`, então o link aparece legível no arquivo. O token é Base64Url e vai sem escape no link. O passo 4 devolve o access token e o refresh token ([Fazer login com e-mail e senha](login.md#como-usar)).

## Solicitar o cadastro

```http
POST /api/v1/identity/registrations HTTP/1.1
Host: localhost:5141
Content-Type: application/json

{ "email": "cliente@example.com" }
```

O corpo aceita só `email`. Outros campos, como `role`, são ignorados: o contrato não tem onde recebê-los, e nenhum papel é escolhido pelo cliente.

### Solicitação aceita (202)

```json
{
  "message": "Se o e-mail puder ser usado, você receberá um link para concluir o cadastro."
}
```

A resposta é **sempre a mesma**, com ou sem conta para o e-mail e mesmo que o envio do e-mail falhe, para não revelar quais e-mails já têm conta. Quando o e-mail está livre, chega à caixa de entrada uma mensagem com o assunto `Conclua o seu cadastro no Ouroboros` e um link como:

```text
http://localhost:3000/cadastro/concluir?token=<token>
```

A mensagem informa a validade em horas e que o link só pode ser usado uma vez. Em desenvolvimento, abra o `.eml` mais recente de `.local/emails/`.

### E-mail inválido (400)

Responde `400` com `ValidationProblemDetails` e `traceId`, sem gerar token nem enviar e-mail ([Respostas de erro](../architecture/error-responses.md#validação-400)):

- e-mail ausente, vazio, só com espaços ou acima de 254 caracteres: recusado pela validação automática do `[ApiController]` (`[Required]` e `[MaxLength(254)]` no DTO), com a mensagem padrão do ASP.NET Core no campo `Email`;
- e-mail fora da [regra estrita](#regra-estrita-de-e-mail): recusado pelo caso de uso, com a mensagem `Informe um e-mail válido.` no campo `Email`.

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "Email": ["Informe um e-mail válido."]
  },
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

O `400` revela só que o formato é inválido, nunca se o e-mail tem conta.

### Limite excedido na solicitação (429)

Acima de 5 solicitações por minuto do mesmo IP, a resposta é `429` com `Retry-After`, antes de qualquer consulta ao banco ([Limitar requisições](auth-rate-limiting.md#resposta-acima-do-limite-429)).

### Regras da solicitação

1. **Parâmetros primeiro.** O caso de uso lê `TokenLifetimeHours` e `CompletionUrl` antes de qualquer decisão. Parâmetro ausente ou com validade fora de 1 a 168 lança exceção, e toda solicitação responde `500`, qualquer que seja o e-mail; assim, a falha também não distingue e-mail livre de ocupado.
2. **Normalização e formato.** O e-mail é normalizado como no domínio (sem espaços nas pontas e em minúsculas) e precisa atender à [regra estrita](#regra-estrita-de-e-mail); senão, `400`.
3. **E-mail já usado: nada acontece.** Se o e-mail normalizado já pertence a um usuário, de qualquer papel, nenhum token é gerado e nenhum e-mail é enviado, e a resposta é o mesmo `202`. O dono do e-mail não é avisado da tentativa.
4. **Token novo invalida os pendentes.** Para um e-mail livre, numa única transação:
   - um `pg_advisory_xact_lock` sobre o e-mail serializa solicitações simultâneas para o mesmo endereço;
   - os tokens de cadastro ainda pendentes desse e-mail (sem `used_at` nem `revoked_at`) recebem `revoked_at`;
   - o token novo é gravado, com validade de `TokenLifetimeHours` a partir de agora.

   Por isso sobra sempre um único token pendente por e-mail, mesmo com solicitações simultâneas, e o link de uma solicitação anterior deixa de valer na conclusão.
5. **Token de uso único, gravado só como hash.** O token tem 32 bytes aleatórios em Base64Url, gerados pelo mesmo `OpaqueTokens` do refresh token; o banco guarda só o SHA-256 (veja [Persistência](#persistência)).
6. **Envio depois do commit.** O link (`<CompletionUrl>?token=<token>`, com o token escapado para URL) é enviado pela porta `IEmailSender` só depois de a transação ser confirmada ([Envio de e-mail](../architecture/email.md#como-usar-nos-casos-de-uso)).
7. **Falha no envio não muda a resposta.** Se o envio falhar, a falha vai para o log em nível `Error` (`Falha ao enviar o e-mail de cadastro (<tipo da exceção>).`), sem o e-mail, o link, o token nem a mensagem da exceção, e a resposta continua `202`. O token gravado fica pendente; para receber um link, a pessoa faz nova solicitação. Um cancelamento da requisição não é tratado como falha de envio.
8. **O token nunca sai por outro caminho.** Não aparece na resposta nem nos logs, em nenhum nível.
9. **Nenhum usuário é criado.** A conta só nasce na [conclusão](#concluir-o-cadastro).

### Regra estrita de e-mail

Desde a #20, os e-mails de conta passam por [`EmailAddresses.IsValid`](../../src/Modules/Ouroboros.Modules.Identity/Domain/Users/EmailAddresses.cs) (`Domain/Users`), a mesma regra do tipo `Email` dos [parâmetros do sistema](../architecture/system-parameters.md#regras-de-validação). Sobre o e-mail já normalizado, exige:

- no máximo 254 caracteres;
- endereço interpretado igual à própria entrada, sem nome de exibição (`Ana <ana@example.com>` é recusado);
- parte local em ASCII, só com letras minúsculas, dígitos e `._%+-`, de 1 a 64 caracteres, sem aspas, espaços nem comentários;
- domínio DNS com pelo menos um ponto e terminação alfabética, sem ponto final (`example.com.`) nem literal de IP (`[127.0.0.1]`); `localhost` é recusado.

A regra vale na solicitação do cadastro, no [`create-admin`](first-admin.md#regras) e em `User.Create`, que lança `ArgumentException` para um e-mail fora dela. A conclusão não recebe e-mail: usa o gravado no token, que já passou pela regra.

O motivo é um defeito encontrado no QA: o `MailAddress` do .NET, usado antes, aceita aspas, espaços, nome de exibição, comentários e ponto final e reescreve o endereço (por exemplo, `vic tim@example.com` vira `tim@example.com`). Com ele, o link poderia ir para outra caixa, e um endereço equivalente ao de uma conta existente (`"vitima"@example.com`, `vitima@example.com.`) escaparia da verificação de e-mail já usado.

Consequência: e-mails com caracteres fora do ASCII (endereços internacionalizados) são recusados.

## Concluir o cadastro

```http
POST /api/v1/identity/registrations/complete HTTP/1.1
Host: localhost:5141
Content-Type: application/json

{ "token": "<token do link>", "password": "uma frase longa que só eu conheço" }
```

| Campo | Regra no DTO |
| --- | --- |
| `token` | Opcional no DTO, até 256 caracteres. Ausente, nulo ou vazio recebe a resposta de [token inválido](#token-inválido-400). |
| `password` | Opcional no DTO, até 1024 caracteres. Ausente ou nulo é tratado como senha vazia e recebe [senha recusada](#senha-recusada-400). O limite real da senha é o da [política de senha](../architecture/password-policy.md) (128 code points); o `[MaxLength]` só protege o servidor de entradas enormes. |

Acima de 256 ou 1024 caracteres, a resposta é o `400` de validação padrão do `[ApiController]` (`ValidationProblemDetails`, com o campo `Token` ou `Password`), sem chegar ao caso de uso ([Respostas de erro](../architecture/error-responses.md#validação-400)).

### Cadastro concluído (204)

Sem corpo. O usuário foi criado, ativo e com o papel `Customer`, e o token foi consumido. A resposta não traz access token nem refresh token, e nenhuma sessão é criada: o cliente entra em seguida pelo [login](login.md).

### Token inválido (400)

```json
{
  "type": "https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/identity.md#invalid-registration-token",
  "title": "Link de cadastro inválido ou expirado.",
  "status": 400,
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

A mesma resposta, sem `detail`, para token vazio ou ausente, inexistente, expirado, já usado, invalidado por uma solicitação mais nova, de outro tipo (por exemplo, de redefinição de senha) ou cujo e-mail passou a pertencer a outra conta, inclusive por uma conclusão simultânea que venceu. O token é conferido antes da senha: um token inválido recebe esta resposta mesmo com uma senha fora da política, para não revelar a regra a quem não tem um link válido ([catálogo de erros](../errors/identity.md#invalid-registration-token)).

### Senha recusada (400)

```json
{
  "type": "https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/identity.md#password-rejected",
  "title": "A senha não atende à política de senha.",
  "status": 400,
  "detail": "A senha deve ter pelo menos 15 caracteres.",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

Só com um token válido. O `detail` traz a regra violada, com as mensagens da [política de senha](../architecture/password-policy.md) (tamanho, lista de senhas comuns, nome do sistema ou parte local do e-mail do token). O token não é consumido: a pessoa pode tentar de novo com o mesmo link ([catálogo de erros](../errors/identity.md#password-rejected)).

### Limite excedido na conclusão (429)

Acima de 10 conclusões por minuto do mesmo IP, a resposta é `429` com `Retry-After`, antes de qualquer consulta ao banco ([Limitar requisições](auth-rate-limiting.md#resposta-acima-do-limite-429)).

### Regras da conclusão

1. **Token procurado pelo hash.** O caso de uso calcula o SHA-256 do token recebido e procura um token com finalidade `Registration`, sem `used_at` nem `revoked_at` e com `expires_at` posterior ao instante atual. Sem ele, [token inválido](#token-inválido-400). Um token vale até o fim da validade, exclusive.
2. **Senha validada contra o e-mail do token.** A senha passa pela mesma `PasswordPolicy` do [primeiro administrador](first-admin.md#regras), com o e-mail gravado no token como contexto. Recusada, a resposta é [senha recusada](#senha-recusada-400), nada é gravado e o token continua pendente.
3. **Hash fora da transação.** O hash da senha (`IPasswordHasher.Hash`, 210.000 iterações) é calculado antes de abrir a transação, para não segurar a transação durante o trabalho mais caro ([Domínio de identidade](../architecture/identity.md#hash-e-rehash)).
4. **Consumo condicional na mesma transação da conta.** Numa transação:
   - um `UPDATE` condicional preenche `used_at` (e `updated_at`) só se o token ainda está sem `used_at`, sem `revoked_at` e não expirado. Se nenhuma linha muda (uma conclusão simultânea venceu, ou uma solicitação mais nova revogou o token no intervalo), [token inválido](#token-inválido-400);
   - o caso de uso confere se o e-mail já tem conta, de qualquer papel. Se tem (uma conta criada depois da solicitação), [token inválido](#token-inválido-400);
   - o `User` é criado com `User.Create(email, hash, Customer)`: ativo, com exatamente um papel, `Customer`.

   Só com tudo gravado a transação é confirmada. Nas saídas de token inválido de dentro da transação, ela é desfeita, então nada fica gravado, nem o consumo do token.
5. **Corrida pelo e-mail.** Se outra criação de usuário com o mesmo e-mail vencer entre a verificação e a gravação, o índice único de `identity.users.email` recusa a inserção. A violação de unicidade (`23505`), reconhecida por `PersistenceErrors.IsUniqueViolation`, também vira [token inválido](#token-inválido-400), sem nada gravado.
6. **Falha ao gravar desfaz tudo.** Qualquer outra falha ao gravar desfaz a transação e responde `500`; o token continua pendente e pode ser usado de novo.
7. **Sem login automático.** Nenhuma sessão nem token de acesso é emitido. O cliente entra pelo login da API externa, que aceita só `Customer` ([Fazer login com e-mail e senha](login.md)).
8. **O token não aparece em logs nem na resposta.** Como na solicitação.

### Vínculo futuro com o Customers

Os dados do cliente (nome, documento etc.) ficam fora deste fluxo e pertencem ao módulo Customers, ainda sem análise própria. O vínculo será feito pelo id externo do usuário, o claim `sub` do access token (o `ExternalId` do `User`), sem referência entre os módulos no banco nem transação conjunta.

## Casos de uso expostos

| Tipo | Local | Papel |
| --- | --- | --- |
| [`IRequestRegistration`](../../src/Modules/Ouroboros.Modules.Identity/Contracts/Registrations/IRequestRegistration.cs) | `Contracts/Registrations` (`public`) | `ExecuteAsync(RequestRegistrationRequest(Email))`, que devolve `RequestRegistrationResult`: `Accepted` (com ou sem envio do link) ou `InvalidEmail` (formato fora da regra estrita). |
| [`RequestRegistration`](../../src/Modules/Ouroboros.Modules.Identity/Application/Registrations/RequestRegistration.cs) | `Application/Registrations` (`internal`) | Implementação da solicitação, registrada como scoped em `AddIdentityModule`. Declara as chaves dos dois parâmetros. |
| [`ICompleteRegistration`](../../src/Modules/Ouroboros.Modules.Identity/Contracts/Registrations/ICompleteRegistration.cs) | `Contracts/Registrations` (`public`) | `ExecuteAsync(CompleteRegistrationRequest(Token, Password))`, que devolve `CompleteRegistrationResult(Outcome, Message)`. `CompleteRegistrationOutcome`: `Completed`, `InvalidToken` ou `PasswordRejected` (com a regra violada em `Message`). |
| [`CompleteRegistration`](../../src/Modules/Ouroboros.Modules.Identity/Application/Registrations/CompleteRegistration.cs) | `Application/Registrations` (`internal`) | Implementação da conclusão, registrada como scoped em `AddIdentityModule`. |
| [`PersistenceErrors`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Persistence/PersistenceErrors.cs) | `Infrastructure/Persistence` (`internal`) | `IsUniqueViolation(DbUpdateException)`: reconhece a violação de unicidade do PostgreSQL (`PostgresErrorCodes.UniqueViolation`), para que `Application` não dependa do Npgsql. |
| [`EmailAddresses`](../../src/Modules/Ouroboros.Modules.Identity/Domain/Users/EmailAddresses.cs) | `Domain/Users` (`internal`) | Regra estrita de e-mail das contas. |
| [`OpaqueTokens`](../../src/Modules/Ouroboros.Modules.Identity/Application/Security/OpaqueTokens.cs) | `Application/Security` (`internal`) | Gera tokens opacos de 32 bytes em Base64Url e calcula o SHA-256; usado pelo cadastro, pela [redefinição de senha](password-reset.md) e pelas sessões. Era o `RefreshTokens`, renomeado na #20. |
| [`RegistrationsController`](../../src/Ouroboros.Api/Controllers/Identity/RegistrationsController.cs) | `Controllers/Identity` da API externa | Action `Create` (solicitação), com `[EnableRateLimiting(IdentityRateLimitPolicies.Registration)]`, e action `Complete` (`[HttpPost("complete")]`), com `[EnableRateLimiting(IdentityRateLimitPolicies.RegistrationCompletion)]`; as duas com `[AllowAnonymous]`. Declara no mesmo arquivo o `RegistrationRequestBody` (só `Email`), o `CompleteRegistrationBody` (`Token` e `Password`) e o `AcceptedResponse(Message)`. As constantes do `type` e do `title` dos dois erros ficam em `IdentityErrors`, no `SessionModels.cs`. |

## Persistência

A migration `AddEmailTokens` cria a tabela `identity.email_tokens`, da entidade [`EmailToken`](../../src/Modules/Ouroboros.Modules.Identity/Domain/EmailTokens/EmailToken.cs) (`Domain/EmailTokens`), mapeada por [`EmailTokenConfiguration`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Persistence/Configurations/EmailTokenConfiguration.cs). É genérica: guarda os tokens de uso único enviados por e-mail, hoje os de cadastro e os da [redefinição de senha](password-reset.md#persistência) ([#22](https://github.com/adnerscarpelini/ouroboros-server/issues/22)). Os dois tipos são independentes: uma solicitação de um tipo só revoga os pendentes do mesmo tipo. Além de `id`, `external_id`, `created_at` e `updated_at` das [convenções das entidades](../architecture/entity-conventions.md):

| Coluna | Tipo | Observação |
| --- | --- | --- |
| `purpose` | `varchar(30)` | Finalidade, pelo nome do enum `EmailTokenPurpose`: `Registration` ou `PasswordReset`. |
| `email` | `varchar(254)` | E-mail normalizado para onde o link foi enviado; na conclusão, vira o e-mail do usuário criado. Índice `ix_email_tokens_email_purpose` com `purpose`, usado para revogar os pendentes do cadastro. Na redefinição, é o e-mail da conta no momento da solicitação. |
| `user_id` | `bigint`, nulo | Dono do token quando ele se refere a uma conta existente; nulo no cadastro, inclusive depois da conclusão; preenchido na redefinição de senha, cujos pendentes são revogados por ele. FK para `identity.users` (`fk_email_tokens_users_user_id`, sem exclusão em cascata), com índice. |
| `token_hash` | `bytea` | SHA-256 do token (32 bytes), índice único `ix_email_tokens_token_hash`, usado na busca da conclusão. O valor do link nunca é gravado. |
| `expires_at` | `timestamptz` | Fim da validade. |
| `used_at` | `timestamptz`, nulo | Momento da conclusão com sucesso, preenchido pelo `UPDATE` condicional. |
| `revoked_at` | `timestamptz`, nulo | Momento em que uma solicitação mais nova, do mesmo tipo, invalidou o token. |

Um token está pendente enquanto `used_at` e `revoked_at` são nulos. Tokens expirados, usados ou revogados não são apagados; não há limpeza periódica. O usuário criado vai para `identity.users`, como os demais ([Domínio de identidade](../architecture/identity.md#modelo-do-usuário)).

## Limitações

- **Diferença de tempo na solicitação.** A resposta é a mesma, mas um e-mail livre custa mais (transação, gravação e envio) que um ocupado; medindo o tempo de resposta, ainda é possível inferir se um e-mail tem conta. É uma limitação conhecida, registrada na #20; o limite por IP só reduz a escala da sondagem.
- **Sem aviso ao dono do e-mail já usado.** Quem tenta se cadastrar com um e-mail que já tem conta não recebe nada, nem o dono da conta é avisado; ficou fora do escopo.
- **Sem reenvio.** Uma nova solicitação cumpre esse papel e invalida o link anterior. Como cada solicitação revoga o link pendente, alguém que conheça o e-mail pode invalidar o link de outra pessoa ao solicitar de novo, limitado pela política de 5 por minuto por IP.
- **E-mail que ganhou conta depois da solicitação.** A conclusão responde token inválido e, como a transação é desfeita, o token continua pendente até expirar, mas nunca mais conclui. Uma nova solicitação para esse e-mail não gera token, porque ele já tem conta.
- **Envio sem outbox.** Se o envio falhar depois do commit, a mensagem se perde, e o token gravado fica pendente sem ter sido entregue ([Envio de e-mail](../architecture/email.md#limitações)).
- **Fora de `Development`, nenhum e-mail sai.** Ainda não há adaptador real; sem provedor, todo envio falha (com o mesmo `202`) e vai para o log, e ninguém consegue concluir um cadastro nesses ambientes.
- **Log `fail` do EF Core na corrida pelo e-mail.** A violação de unicidade tratada como token inválido ainda gera um log de nível `Error` (`fail`) do EF Core ao salvar, sem dado sensível. Na operação, esse log isolado na conclusão do cadastro é uma corrida esperada, não uma falha do sistema.
- **Sem front-end.** O link aponta para `http://localhost:3000/cadastro/concluir`, que não faz parte deste repositório; em desenvolvimento, o token é copiado do `.eml` ([Fluxo completo em desenvolvimento](#fluxo-completo-em-desenvolvimento)).
- **Dados de cliente** (módulo Customers) estão fora do escopo.

## Como verificar

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests   # exige Docker
```

Os testes rodam contra o PostgreSQL real e substituem o `IEmailSender` pelo [`CapturingEmailSender`](../../tests/Ouroboros.IntegrationTests/CapturingEmailSender.cs), que guarda as mensagens (e pode simular falha) para ler o token do link, porque o adaptador `.eml` só existe em `Development`.

[`RegistrationRequestTests`](../../tests/Ouroboros.IntegrationTests/RegistrationRequestTests.cs), da solicitação:

- e-mail livre, com espaços e maiúsculas, recebe `202`, um único e-mail para o endereço normalizado com o link `http://localhost:3000/cadastro/concluir?token=`, um token de 32 bytes cujo SHA-256 está gravado com finalidade `Registration`, validade de 24 horas e nenhum usuário criado;
- e-mail de um usuário existente recebe o mesmo corpo de resposta de um e-mail livre, sem e-mail enviado nem token gravado;
- uma segunda solicitação gera outro token, e o primeiro fica com `revoked_at`;
- oito solicitações simultâneas para o mesmo e-mail gravam oito tokens, com um só pendente;
- `"vitima"@example.com` e `vitima@example.com.`, equivalentes ao e-mail de um usuário existente, recebem `400`, sem e-mail nem token;
- `role` no corpo é ignorado: `202`, nenhum usuário e um token;
- recebem `400`, sem e-mail nem token: `nao-e-email`, vazio, nulo, parte local entre aspas, ponto final no domínio, espaço na parte local, nome de exibição, `>` sobrando, `localhost`, literal de IP e comentário;
- com a política `identity-registration` limitada a 1, a segunda solicitação recebe `429`;
- o token não aparece no corpo da resposta nem em nenhum log (nível mínimo `Trace`);
- com falha simulada no envio, a resposta continua `202`, e o log registra a falha.

[`RegistrationCompletionTests`](../../tests/Ouroboros.IntegrationTests/RegistrationCompletionTests.cs), da conclusão, com o token obtido por uma solicitação real e um `FakeTimeProvider`:

- token válido e senha na política: `204` sem corpo, usuário `Customer` ativo, `used_at` preenchido, nenhuma sessão criada, e o login da API externa com a senha responde `200`;
- recebem token inválido (`type` e `title` do `invalid-registration-token`): token já usado (sem criar outro usuário), expirado (24 horas depois), invalidado por uma solicitação mais nova (e o token novo conclui), de outro tipo (`PasswordReset`), inexistente, vazio e nulo;
- senha curta, comum, com a parte local do e-mail e nula: `400` com `type` do `password-rejected` e `detail` preenchido, `used_at` nulo, e o mesmo token conclui em seguida;
- cinco conclusões simultâneas com o mesmo token: um único `204`, os demais `400`, e um só usuário;
- e-mail que passou a ter conta depois da solicitação: token inválido, e a conta existente fica intacta.

A política `identity-registration-completion` não tem teste próprio de `429`; usa o mesmo mecanismo das demais. Os limites de `identity-registration` e `identity-registration-completion` são elevados para 100.000 na [`IntegrationApiFactory`](../../tests/Ouroboros.IntegrationTests/IntegrationApiFactory.cs), como os demais ([Testes de integração](../architecture/integration-tests.md)).

Na validação da #20, a suíte tinha 305 testes; na da #21, 319.

## Referências

- [OWASP Authentication Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html)
- [OWASP Forgot Password Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Forgot_Password_Cheat_Sheet.html)

## Origem

- [#20](https://github.com/adnerscarpelini/ouroboros-server/issues/20): solicitar cadastro de cliente por e-mail.
- [#21](https://github.com/adnerscarpelini/ouroboros-server/issues/21): concluir o cadastro de cliente definindo a senha.
- Depende de [#10](https://github.com/adnerscarpelini/ouroboros-server/issues/10) (envio de e-mail), [#11](https://github.com/adnerscarpelini/ouroboros-server/issues/11) (política de senha) e [#14](https://github.com/adnerscarpelini/ouroboros-server/issues/14) (limite de requisições por IP).

[Voltar à documentação](../README.md)
