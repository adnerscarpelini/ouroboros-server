# Cadastrar cliente por e-mail

## Objetivo

Clientes se cadastram sozinhos. Para impedir que alguém crie uma conta com o e-mail de outra pessoa e a ative com uma senha que só ele conhece (pre-account takeover), o cadastro começa provando a posse do e-mail: a pessoa informa só o endereço e recebe um link de uso único; a senha é definida depois, ao concluir o cadastro pelo link.

Esta nota descreve a primeira etapa, a **solicitação do cadastro** ([#20](https://github.com/adnerscarpelini/ouroboros-server/issues/20)). A conclusão, que valida o token do link, define a senha e cria o usuário, é a [#21](https://github.com/adnerscarpelini/ouroboros-server/issues/21) e ainda não existe: hoje nenhum usuário é criado por este fluxo.

Módulo responsável: Identity. A regra fica no caso de uso `IRequestRegistration` do módulo; o controller da API externa só valida a entrada, chama o caso de uso e traduz o resultado em `202` ou `400` ([Controllers HTTP nos hosts](../architecture/http-controllers.md)).

## Rota

| Host | Rota | Autenticação |
| --- | --- | --- |
| API externa (`Ouroboros.Api`) | `POST /api/v1/identity/registrations` | Anônima (`[AllowAnonymous]`) |

A rota existe só na API externa: o backoffice não tem cadastro, porque administradores entram por convite ([Criar o primeiro administrador](first-admin.md)).

## Pré-requisitos

- O banco local com as migrations do Identity e do Settings aplicadas ([README](../../README.md#banco-de-dados-local)): a `AddEmailTokens` (Identity) cria a tabela dos tokens, e a `AddRegistrationParameters` (Settings) inclui os parâmetros abaixo.
- Para ver o e-mail enviado em desenvolvimento, a API externa no ambiente `Development`, que grava cada mensagem como `.eml` em `.local/emails/` ([Envio de e-mail](../architecture/email.md#ver-os-e-mails-em-desenvolvimento)). Fora de `Development`, sem provedor configurado, o envio falha e a resposta continua `202` (veja [Regras](#regras)).

## Configuração

Dois [parâmetros do sistema](../architecture/system-parameters.md#valores-iniciais-e-novas-chaves), lidos do Settings a cada solicitação:

| Chave | Valor inicial | Uso |
| --- | --- | --- |
| `Identity:Registration:TokenLifetimeHours` | `24` | Validade do link, em horas; inteiro de 1 a 168. Vale para os próximos links; os já enviados mantêm a validade com que foram gerados. |
| `Identity:Registration:CompletionUrl` | `http://localhost:3000/cadastro/concluir` | Endereço da tela do front-end que conclui o cadastro. O sistema acrescenta `?token=<token>`, então o valor não pode ter query string nem fragmento. |

O limite de 1 a 168 horas é conferido pelo padrão do parâmetro na gravação e também pelo caso de uso, para um valor gravado direto no banco. A URL é conferida só na gravação, pelo padrão `https?://[^\s?#]+` do parâmetro.

O limite de requisições por IP fica na política `identity-registration` (5 por minuto por IP), em `Identity:RateLimiting:Policies` ([Limitar requisições](auth-rate-limiting.md#configuração)).

## Como usar

Com a API externa no ar (`http://localhost:5141` no perfil padrão):

```http
POST /api/v1/identity/registrations HTTP/1.1
Host: localhost:5141
Content-Type: application/json

{ "email": "cliente@example.com" }
```

```powershell
# PowerShell
Invoke-RestMethod -Method Post -Uri http://localhost:5141/api/v1/identity/registrations `
  -ContentType 'application/json' -Body '{"email":"cliente@example.com"}'
```

O corpo aceita só `email`. Outros campos, como `role`, são ignorados: o contrato não tem onde recebê-los, e nenhum papel é atribuído nesta etapa.

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

### Limite excedido (429)

Acima de 5 solicitações por minuto do mesmo IP, a resposta é `429` com `Retry-After`, antes de qualquer consulta ao banco ([Limitar requisições](auth-rate-limiting.md#resposta-acima-do-limite-429)).

## Regras

1. **Parâmetros primeiro.** O caso de uso lê `TokenLifetimeHours` e `CompletionUrl` antes de qualquer decisão. Parâmetro ausente ou com validade fora de 1 a 168 lança exceção, e toda solicitação responde `500`, qualquer que seja o e-mail; assim, a falha também não distingue e-mail livre de ocupado.
2. **Normalização e formato.** O e-mail é normalizado como no domínio (sem espaços nas pontas e em minúsculas) e precisa atender à [regra estrita](#regra-estrita-de-e-mail); senão, `400`.
3. **E-mail já usado: nada acontece.** Se o e-mail normalizado já pertence a um usuário, de qualquer papel, nenhum token é gerado e nenhum e-mail é enviado, e a resposta é o mesmo `202`. O dono do e-mail não é avisado da tentativa.
4. **Token novo invalida os pendentes.** Para um e-mail livre, numa única transação:
   - um `pg_advisory_xact_lock` sobre o e-mail serializa solicitações simultâneas para o mesmo endereço;
   - os tokens de cadastro ainda pendentes desse e-mail (sem `used_at` nem `revoked_at`) recebem `revoked_at`;
   - o token novo é gravado, com validade de `TokenLifetimeHours` a partir de agora.

   Por isso sobra sempre um único token pendente por e-mail, mesmo com solicitações simultâneas, e o link de uma solicitação anterior deixa de valer.
5. **Token de uso único, gravado só como hash.** O token tem 32 bytes aleatórios em Base64Url, gerados pelo mesmo `OpaqueTokens` do refresh token; o banco guarda só o SHA-256 (veja [Persistência](#persistência)).
6. **Envio depois do commit.** O link (`<CompletionUrl>?token=<token>`, com o token escapado para URL) é enviado pela porta `IEmailSender` só depois de a transação ser confirmada ([Envio de e-mail](../architecture/email.md#como-usar-nos-casos-de-uso)).
7. **Falha no envio não muda a resposta.** Se o envio falhar, a falha vai para o log em nível `Error` (`Falha ao enviar o e-mail de cadastro (<tipo da exceção>).`), sem o e-mail, o link, o token nem a mensagem da exceção, e a resposta continua `202`. O token gravado fica pendente; para receber um link, a pessoa faz nova solicitação. Um cancelamento da requisição não é tratado como falha de envio.
8. **O token nunca sai por outro caminho.** Não aparece na resposta nem nos logs, em nenhum nível.
9. **Nenhum usuário é criado.** A conta só nasce na conclusão do cadastro ([#21](https://github.com/adnerscarpelini/ouroboros-server/issues/21)).

### Regra estrita de e-mail

Desde esta issue, os e-mails de conta passam por [`EmailAddresses.IsValid`](../../src/Modules/Ouroboros.Modules.Identity/Domain/Users/EmailAddresses.cs) (`Domain/Users`), a mesma regra do tipo `Email` dos [parâmetros do sistema](../architecture/system-parameters.md#regras-de-validação). Sobre o e-mail já normalizado, exige:

- no máximo 254 caracteres;
- endereço interpretado igual à própria entrada, sem nome de exibição (`Ana <ana@example.com>` é recusado);
- parte local em ASCII, só com letras minúsculas, dígitos e `._%+-`, de 1 a 64 caracteres, sem aspas, espaços nem comentários;
- domínio DNS com pelo menos um ponto e terminação alfabética, sem ponto final (`example.com.`) nem literal de IP (`[127.0.0.1]`); `localhost` é recusado.

A regra vale na solicitação do cadastro, no [`create-admin`](first-admin.md#regras) e em `User.Create`, que lança `ArgumentException` para um e-mail fora dela.

O motivo é um defeito encontrado no QA: o `MailAddress` do .NET, usado antes, aceita aspas, espaços, nome de exibição, comentários e ponto final e reescreve o endereço (por exemplo, `vic tim@example.com` vira `tim@example.com`). Com ele, o link poderia ir para outra caixa, e um endereço equivalente ao de uma conta existente (`"vitima"@example.com`, `vitima@example.com.`) escaparia da verificação de e-mail já usado.

Consequência: e-mails com caracteres fora do ASCII (endereços internacionalizados) são recusados.

## Caso de uso exposto

| Tipo | Local | Papel |
| --- | --- | --- |
| [`IRequestRegistration`](../../src/Modules/Ouroboros.Modules.Identity/Contracts/Registrations/IRequestRegistration.cs) | `Contracts/Registrations` (`public`) | `ExecuteAsync(RequestRegistrationRequest(Email))`, que devolve `RequestRegistrationResult`: `Accepted` (com ou sem envio do link) ou `InvalidEmail` (formato fora da regra estrita). |
| [`RequestRegistration`](../../src/Modules/Ouroboros.Modules.Identity/Application/Registrations/RequestRegistration.cs) | `Application/Registrations` (`internal`) | Implementação, registrada como scoped em `AddIdentityModule`. Declara as chaves dos dois parâmetros. |
| [`EmailAddresses`](../../src/Modules/Ouroboros.Modules.Identity/Domain/Users/EmailAddresses.cs) | `Domain/Users` (`internal`) | Regra estrita de e-mail das contas. |
| [`OpaqueTokens`](../../src/Modules/Ouroboros.Modules.Identity/Application/Security/OpaqueTokens.cs) | `Application/Security` (`internal`) | Gera tokens opacos de 32 bytes em Base64Url e calcula o SHA-256; usado pelo cadastro e pelas sessões. Era o `RefreshTokens`, renomeado nesta issue. |
| [`RegistrationsController`](../../src/Ouroboros.Api/Controllers/Identity/RegistrationsController.cs) | `Controllers/Identity` da API externa | Action `Create`, com `[AllowAnonymous]` e `[EnableRateLimiting(IdentityRateLimitPolicies.Registration)]`. Declara no mesmo arquivo o `RegistrationRequestBody` (só `Email`) e o `AcceptedResponse(Message)`. |

## Persistência

A migration `AddEmailTokens` cria a tabela `identity.email_tokens`, da entidade [`EmailToken`](../../src/Modules/Ouroboros.Modules.Identity/Domain/EmailTokens/EmailToken.cs) (`Domain/EmailTokens`), mapeada por [`EmailTokenConfiguration`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Persistence/Configurations/EmailTokenConfiguration.cs). É genérica: guarda os tokens de uso único enviados por e-mail, hoje os de cadastro, e servirá à conclusão do cadastro e à redefinição de senha ([#21](https://github.com/adnerscarpelini/ouroboros-server/issues/21) a [#23](https://github.com/adnerscarpelini/ouroboros-server/issues/23)). Além de `id`, `external_id`, `created_at` e `updated_at` das [convenções das entidades](../architecture/entity-conventions.md):

| Coluna | Tipo | Observação |
| --- | --- | --- |
| `purpose` | `varchar(30)` | Finalidade, pelo nome do enum `EmailTokenPurpose`: `Registration` ou `PasswordReset` (este ainda sem uso). |
| `email` | `varchar(254)` | E-mail normalizado para onde o link foi enviado. Índice `ix_email_tokens_email_purpose` com `purpose`, usado para revogar os pendentes. |
| `user_id` | `bigint`, nulo | Dono do token quando ele se refere a uma conta existente; nulo no cadastro. FK para `identity.users` (`fk_email_tokens_users_user_id`, sem exclusão em cascata), com índice. |
| `token_hash` | `bytea` | SHA-256 do token (32 bytes), índice único `ix_email_tokens_token_hash`. O valor do link nunca é gravado. |
| `expires_at` | `timestamptz` | Fim da validade. |
| `used_at` | `timestamptz`, nulo | Momento do uso com sucesso; ainda não é preenchido (a conclusão é a #21). |
| `revoked_at` | `timestamptz`, nulo | Momento em que uma solicitação mais nova invalidou o token. |

Um token está pendente enquanto `used_at` e `revoked_at` são nulos. Tokens expirados ou revogados não são apagados; não há limpeza periódica.

## Limitações

- **Diferença de tempo.** A resposta é a mesma, mas um e-mail livre custa mais (transação, gravação e envio) que um ocupado; medindo o tempo de resposta, ainda é possível inferir se um e-mail tem conta. É uma limitação conhecida, registrada na issue; o limite por IP só reduz a escala da sondagem.
- **Sem aviso ao dono do e-mail já usado.** Quem tenta se cadastrar com um e-mail que já tem conta não recebe nada, nem o dono da conta é avisado; ficou fora do escopo.
- **Sem reenvio.** Uma nova solicitação cumpre esse papel e invalida o link anterior. Como cada solicitação revoga o link pendente, alguém que conheça o e-mail pode invalidar o link de outra pessoa ao solicitar de novo, limitado pela política de 5 por minuto por IP.
- **Envio sem outbox.** Se o envio falhar depois do commit, a mensagem se perde, e o token gravado fica pendente sem ter sido entregue ([Envio de e-mail](../architecture/email.md#limitações)).
- **Fora de `Development`, nenhum e-mail sai.** Ainda não há adaptador real; sem provedor, todo envio falha (com o mesmo `202`) e vai para o log.
- **Conclusão ainda não implementada.** O link aponta para a tela do front-end, mas o endpoint que aceita o token e cria a conta chega com a #21. Por enquanto, o efeito de "link anterior não aceito" é verificável só pelo `revoked_at` no banco.
- **Dados de cliente** (módulo Customers) estão fora do escopo.

## Como verificar

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests   # exige Docker
```

[`RegistrationRequestTests`](../../tests/Ouroboros.IntegrationTests/RegistrationRequestTests.cs), contra o PostgreSQL real. Os testes substituem o `IEmailSender` pelo [`CapturingEmailSender`](../../tests/Ouroboros.IntegrationTests/CapturingEmailSender.cs), que guarda as mensagens (e pode simular falha) para ler o token do link, porque o adaptador `.eml` só existe em `Development`:

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

O limite da política `identity-registration` é elevado para 100.000 na [`IntegrationApiFactory`](../../tests/Ouroboros.IntegrationTests/IntegrationApiFactory.cs), como os demais ([Testes de integração](../architecture/integration-tests.md)).

Na validação da #20, a suíte tinha 305 testes.

## Referências

- [OWASP Authentication Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html)
- [OWASP Forgot Password Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Forgot_Password_Cheat_Sheet.html)

## Origem

- [#20](https://github.com/adnerscarpelini/ouroboros-server/issues/20): solicitar cadastro de cliente por e-mail.
- Depende de [#10](https://github.com/adnerscarpelini/ouroboros-server/issues/10) (envio de e-mail) e [#14](https://github.com/adnerscarpelini/ouroboros-server/issues/14) (limite de requisições por IP).

[Voltar à documentação](../README.md)
