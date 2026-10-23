# Redefinir a senha por e-mail

## Objetivo

Um cliente que esqueceu a senha recebe por e-mail um link de uso único para escolher uma senha nova, sem que a API revele se um e-mail tem conta.

O fluxo tem duas etapas:

1. **Solicitar a redefinição** ([#22](https://github.com/adnerscarpelini/ouroboros-server/issues/22)): a pessoa informa o e-mail e, se ele pertence a uma conta de cliente ativa, recebe o link ([Solicitar a redefinição](#solicitar-a-redefinição)).
2. **Concluir a redefinição** ([#23](https://github.com/adnerscarpelini/ouroboros-server/issues/23)): a tela do link envia o token e a senha nova. Ainda não implementada; esta nota será completada com ela.

Até a conclusão, a senha atual continua valendo: a solicitação não altera a conta nem encerra sessões.

Módulo responsável: Identity. A regra fica no caso de uso `IRequestPasswordReset` do módulo; o controller da API externa só valida a entrada, chama o caso de uso e traduz o resultado em resposta HTTP ([Controllers HTTP nos hosts](../architecture/http-controllers.md)).

## Rotas

| Host | Rota | Autenticação | Etapa |
| --- | --- | --- | --- |
| API externa (`Ouroboros.Api`) | `POST /api/v1/identity/password-resets` | Anônima (`[AllowAnonymous]`) | Solicitar |

A rota existe só na API externa. Contas de backoffice não são recuperadas por ela (veja [Regras da solicitação](#regras-da-solicitação)).

## Pré-requisitos

- O banco local com as migrations do Identity e do Settings aplicadas ([README](../../README.md#banco-de-dados-local)): a tabela dos tokens é a `identity.email_tokens`, criada pela `AddEmailTokens` do [cadastro](registration.md#persistência), e a `AddPasswordResetParameters` (Settings) inclui os parâmetros abaixo. A solicitação não tem migration própria no Identity.
- Uma conta de cliente ativa. Em desenvolvimento, crie uma pelo [cadastro](registration.md#fluxo-completo-em-desenvolvimento).
- Para ver o e-mail enviado em desenvolvimento, a API externa no ambiente `Development`, que grava cada mensagem como `.eml` em `.local/emails/` ([Envio de e-mail](../architecture/email.md#ver-os-e-mails-em-desenvolvimento)). Fora de `Development`, sem provedor configurado, o envio falha e a resposta continua `202`.

## Configuração

Dois [parâmetros do sistema](../architecture/system-parameters.md#valores-iniciais-e-novas-chaves), lidos do Settings a cada solicitação:

| Chave | Valor inicial | Uso |
| --- | --- | --- |
| `Identity:PasswordReset:TokenLifetimeMinutes` | `60` | Validade do link, em minutos; inteiro de 1 a 1440. Vale para os próximos links; os já enviados mantêm a validade com que foram gerados. |
| `Identity:PasswordReset:CompletionUrl` | `http://localhost:3000/senha/redefinir` | Endereço da tela do front-end que redefine a senha. O sistema acrescenta `?token=<token>`, então o valor não pode ter query string nem fragmento. |

O limite de 1 a 1440 minutos é conferido pelo padrão do parâmetro na gravação e também pelo caso de uso, para um valor gravado direto no banco. A URL é conferida só na gravação, pelo padrão `https?://[^\s?#]+` do parâmetro.

O limite de requisições por IP fica em `Identity:RateLimiting:Policies` ([Limitar requisições](auth-rate-limiting.md#configuração)): `identity-password-reset`, 5 por minuto por IP.

## Solicitar a redefinição

```http
POST /api/v1/identity/password-resets HTTP/1.1
Host: localhost:5141
Content-Type: application/json

{ "email": "cliente@example.com" }
```

O corpo aceita só `email`.

Em PowerShell, com a API externa no ar em `Development` (`http://localhost:5141` no perfil padrão):

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:5141/api/v1/identity/password-resets `
  -ContentType 'application/json' -Body '{"email":"cliente@example.com"}'
```

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

A mensagem informa a validade em minutos, que o link só pode ser usado uma vez e que a senha atual continua valendo até a redefinição ser concluída. Em desenvolvimento, abra o `.eml` mais recente de `.local/emails/`; o token pode ser lido como no [fluxo do cadastro](registration.md#fluxo-completo-em-desenvolvimento).

### E-mail inválido (400)

Responde `400` com `ValidationProblemDetails` e `traceId`, sem gerar token nem enviar e-mail ([Respostas de erro](../architecture/error-responses.md#validação-400)):

- e-mail ausente, vazio, só com espaços ou acima de 254 caracteres: recusado pela validação automática do `[ApiController]` (`[Required]` e `[MaxLength(254)]` no DTO), com a mensagem padrão do ASP.NET Core no campo `Email`;
- e-mail fora da [regra estrita de e-mail](registration.md#regra-estrita-de-e-mail) das contas: recusado pelo caso de uso, com a mensagem `Informe um e-mail válido.` no campo `Email`.

O `400` revela só que o formato é inválido, nunca se o e-mail tem conta. Um endereço equivalente ao de uma conta, como `"cliente"@example.com`, `cliente@example.com.` ou `Cliente <cliente@example.com>`, é recusado aqui e não recebe o link.

### Limite excedido (429)

Acima de 5 solicitações por minuto do mesmo IP, a resposta é `429` com `Retry-After`, antes de qualquer consulta ao banco ([Limitar requisições](auth-rate-limiting.md#resposta-acima-do-limite-429)).

### Erro interno (500)

Parâmetro ausente ou com validade fora de 1 a 1440 faz toda solicitação responder `500` ([Respostas de erro](../architecture/error-responses.md#erro-inesperado-500)), qualquer que seja o e-mail.

### Regras da solicitação

1. **Parâmetros primeiro.** O caso de uso lê `TokenLifetimeMinutes` e `CompletionUrl` antes de qualquer decisão. Parâmetro ausente ou inválido lança exceção para qualquer e-mail, então a falha também não distingue conta existente de inexistente.
2. **Normalização e formato.** O e-mail é normalizado como no domínio (sem espaços nas pontas e em minúsculas) e precisa atender à [regra estrita](registration.md#regra-estrita-de-e-mail); senão, `400`.
3. **Só conta de cliente ativa recebe o link.** O caso de uso procura um usuário com o e-mail normalizado, ativo e com o papel `Customer`. E-mail sem conta, conta inativa e conta de backoffice (`Admin`) recebem o mesmo `202`, sem token nem e-mail.
4. **Token novo invalida os pendentes.** Para uma conta encontrada, numa única transação:
   - um `pg_advisory_xact_lock` sobre o id da conta serializa solicitações simultâneas da mesma conta;
   - os tokens de redefinição ainda pendentes da conta (finalidade `PasswordReset`, sem `used_at` nem `revoked_at`) recebem `revoked_at` (e `updated_at`);
   - o token novo é gravado com `user_id` da conta, o e-mail da conta e validade de `TokenLifetimeMinutes` a partir de agora.

   Por isso sobra sempre um único token de redefinição pendente por conta, mesmo com solicitações simultâneas. Tokens de cadastro não são afetados: os dois tipos são independentes.
5. **Token de uso único, gravado só como hash.** O token tem 32 bytes aleatórios em Base64Url, gerados pelo `OpaqueTokens`, o mesmo do cadastro e do refresh token; o banco guarda só o SHA-256 ([Persistência](#persistência)).
6. **O link vai para o e-mail da conta.** O destinatário é o e-mail gravado no usuário, nunca o texto recebido na requisição.
7. **Envio depois do commit.** O link (`<CompletionUrl>?token=<token>`, com o token escapado para URL) é enviado pela porta `IEmailSender` só depois de a transação ser confirmada ([Envio de e-mail](../architecture/email.md#como-usar-nos-casos-de-uso)).
8. **Falha no envio não muda a resposta.** Se o envio falhar, a falha vai para o log em nível `Error` (`Falha ao enviar o e-mail de redefinição de senha (<tipo da exceção>).`), sem o e-mail, o link, o token nem a mensagem da exceção, e a resposta continua `202`. O token gravado fica pendente; a pessoa pode pedir de novo. Um cancelamento da requisição não é tratado como falha de envio.
9. **O token nunca sai por outro caminho.** Não aparece na resposta nem nos logs, em nenhum nível.
10. **A senha atual continua valendo.** A solicitação não altera o hash da senha, não bloqueia a conta nem encerra sessões.

### Contas de backoffice

A issue pede o link para qualquer conta existente e ativa. A implementação restringe ao papel `Customer`: contas de backoffice não são recuperadas pela API pública, coerente com o login da API externa, que também aceita só `Customer` ([Fazer login com e-mail e senha](login.md)). É uma decisão de segurança além da issue e **precisa de confirmação do responsável pelo produto**. Hoje não há recuperação de senha para administradores.

## Casos de uso expostos

| Tipo | Local | Papel |
| --- | --- | --- |
| [`IRequestPasswordReset`](../../src/Modules/Ouroboros.Modules.Identity/Contracts/PasswordResets/IRequestPasswordReset.cs) | `Contracts/PasswordResets` (`public`) | `ExecuteAsync(RequestPasswordResetRequest(Email))`, que devolve `RequestPasswordResetResult`: `Accepted` (com ou sem envio do link) ou `InvalidEmail` (formato fora da regra estrita). |
| [`RequestPasswordReset`](../../src/Modules/Ouroboros.Modules.Identity/Application/PasswordResets/RequestPasswordReset.cs) | `Application/PasswordResets` (`internal`) | Implementação da solicitação, registrada como scoped em `AddIdentityModule`. Declara as chaves dos dois parâmetros. |
| [`EmailToken.ForPasswordReset`](../../src/Modules/Ouroboros.Modules.Identity/Domain/EmailTokens/EmailToken.cs) | `Domain/EmailTokens` (`internal`) | Cria o token de redefinição com finalidade `PasswordReset` e `user_id` da conta. |
| [`PasswordResetsController`](../../src/Ouroboros.Api/Controllers/Identity/PasswordResetsController.cs) | `Controllers/Identity` da API externa | Action `Create`, com `[AllowAnonymous]` e `[EnableRateLimiting(IdentityRateLimitPolicies.PasswordReset)]`. Declara no mesmo arquivo o `PasswordResetRequestBody` (só `Email`) e reutiliza o `AcceptedResponse(Message)` do cadastro. |

## Persistência

Os tokens ficam na tabela `identity.email_tokens`, a mesma do cadastro ([Cadastrar cliente por e-mail](registration.md#persistência)), sem migration nova no Identity. Um token de redefinição tem:

- `purpose` = `PasswordReset`;
- `user_id` preenchido com o id da conta (FK para `identity.users`, com índice), usado para revogar os pendentes da conta;
- `email` com o e-mail da conta no momento da solicitação;
- `token_hash` com o SHA-256 do token; o valor do link nunca é gravado.

A migration `AddPasswordResetParameters` do Settings inclui os dois parâmetros, com a revisão 0 no histórico ([Parâmetros do sistema](../architecture/system-parameters.md#valores-iniciais-e-novas-chaves)).

## Limitações

- **Diferença de tempo na solicitação.** A resposta é a mesma, mas uma conta encontrada custa mais (transação, gravação e envio) que um e-mail sem conta; medindo o tempo de resposta, ainda é possível inferir se um e-mail tem conta. É uma limitação conhecida, registrada na #22; o limite por IP só reduz a escala da sondagem.
- **Invalidação por terceiros.** Como cada solicitação revoga o link pendente, alguém que conheça o e-mail pode invalidar o link de outra pessoa ao solicitar de novo, limitado pela política de 5 por minuto por IP. A senha atual continua valendo.
- **Sem conclusão ainda.** O token é gerado, mas nenhuma rota o consome até a [#23](https://github.com/adnerscarpelini/ouroboros-server/issues/23).
- **Sem recuperação para administradores** (veja [Contas de backoffice](#contas-de-backoffice)).
- **Envio sem outbox.** Se o envio falhar depois do commit, a mensagem se perde, e o token gravado fica pendente sem ter sido entregue ([Envio de e-mail](../architecture/email.md#limitações)).
- **Fora de `Development`, nenhum e-mail sai.** Ainda não há adaptador real; todo envio falha (com o mesmo `202`) e vai para o log.
- **Sem aviso de "senha alterada"** por e-mail; ficou fora do escopo.
- **Sem front-end.** O link aponta para `http://localhost:3000/senha/redefinir`, que não faz parte deste repositório.

## Como verificar

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests   # exige Docker
```

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

O limite de `identity-password-reset` é elevado para 100.000 na [`IntegrationApiFactory`](../../tests/Ouroboros.IntegrationTests/IntegrationApiFactory.cs), como os demais ([Testes de integração](../architecture/integration-tests.md)). O critério "quando uso o link da primeira, então ele não é aceito" é coberto, por ora, pela revogação do primeiro token; o uso do link depende da #23.

Na validação da #22, a suíte tinha 332 testes.

## Referências

- [OWASP Forgot Password Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Forgot_Password_Cheat_Sheet.html)

## Origem

- [#22](https://github.com/adnerscarpelini/ouroboros-server/issues/22): solicitar redefinição de senha.
- Depende de [#10](https://github.com/adnerscarpelini/ouroboros-server/issues/10) (envio de e-mail), [#12](https://github.com/adnerscarpelini/ouroboros-server/issues/12) (login) e [#14](https://github.com/adnerscarpelini/ouroboros-server/issues/14) (limite de requisições por IP).

[Voltar à documentação](../README.md)
