# Envio de e-mail por porta com adaptador local

## Contexto

Cadastro ([#20](https://github.com/adnerscarpelini/ouroboros-server/issues/20)), redefinição de senha ([#22](https://github.com/adnerscarpelini/ouroboros-server/issues/22)) e convite de administradores ([#25](https://github.com/adnerscarpelini/ouroboros-server/issues/25)) vão enviar links com tokens por e-mail. O projeto não tem servidor SMTP, broker de mensagens nem Docker para a aplicação. O token não pode voltar na resposta HTTP nem aparecer no log: quem informa um e-mail não prova que é dono dele, e só o dono da caixa de entrada deve receber o link.

## Decisão

- **Porta no Identity.** O envio passa pela interface `internal` [`IEmailSender`](../../src/Modules/Ouroboros.Modules.Identity/Application/Notifications/IEmailSender.cs), em `Application/Notifications`; os adaptadores ficam em `Infrastructure/Email`. Os casos de uso não conhecem o meio de entrega.
- **Adaptador local em desenvolvimento (pickup directory).** Cada mensagem vira um arquivo `.eml` numa pasta fora do Git, que qualquer cliente de e-mail abre.
- **Sem provedor, enviar é erro.** Sem configuração, o adaptador "não configurado" lança exceção em vez de descartar a mensagem em silêncio.
- **Envio depois do commit, sem outbox.** O caso de uso grava a transação e só então envia. Se o envio falhar, o usuário refaz a solicitação.
- **O adaptador local só sobe em `Development`.** Configurá-lo em outro ambiente impede o host de subir.

## A porta

`IEmailSender.SendAsync(EmailMessage message, CancellationToken cancellationToken)`.

`EmailMessage(To, Subject, Body)` é uma mensagem em texto simples:

| Campo | Regra |
| --- | --- |
| `To` | Obrigatório; sem quebras de linha (`\r` ou `\n`). |
| `Subject` | Obrigatório; sem quebras de linha. |
| `Body` | Texto livre. Pode conter links com tokens: nunca vai para o log. |

Destinatário e assunto viram cabeçalhos. Uma quebra de linha neles permitiria injetar outros cabeçalhos (por exemplo, um `Bcc`), então o próprio `EmailMessage` lança `ArgumentException` ao recebê-la, valendo para qualquer adaptador. Valor vazio ou em branco também lança `ArgumentException`.

## Configuração

Seção `Identity:Email`, lida por `EmailOptions` e validada na subida por `EmailOptionsValidator` (`ValidateOnStart`):

| Chave | Padrão | Uso |
| --- | --- | --- |
| `Provider` | `None` | `None` (sem envio) ou `PickupDirectory` (adaptador local). |
| `PickupDirectory` | vazio | Pasta dos `.eml`; quando relativa, é resolvida a partir do content root do host. Obrigatória com `PickupDirectory`. |
| `From` | `nao-responda@ouroboros.local` | Remetente das mensagens. |

A validação falha, e o host não sobe, quando `Provider` é `PickupDirectory` e:

- o ambiente não é `Development` (a mensagem cita `Identity:Email:Provider` e o ambiente atual); ou
- `PickupDirectory` está vazio.

Os `appsettings.Development.json` dos dois hosts ([API externa](../../src/Ouroboros.Api/appsettings.Development.json) e [backoffice](../../src/Ouroboros.Backoffice.Api/appsettings.Development.json)) configuram:

```json
"Identity": {
  "Email": {
    "Provider": "PickupDirectory",
    "PickupDirectory": "../../.local/emails"
  }
}
```

Como o content root é a pasta de cada host (`src/<Host>`), os dois gravam em `.local/emails` na raiz do repositório, ignorada pelo Git (`.local/`). O `appsettings.json` não define a seção, então fora de `Development` o provedor é `None`.

## Adaptadores

| Tipo | Quando | Comportamento |
| --- | --- | --- |
| [`PickupDirectoryEmailSender`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Email/PickupDirectoryEmailSender.cs) | `Provider = PickupDirectory` | Cria a pasta, se preciso, e grava `<yyyyMMddHHmmssfff>-<guid>.eml`. O log (`Information`) registra só o nome do arquivo. |
| [`UnconfiguredEmailSender`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Email/UnconfiguredEmailSender.cs) | `Provider = None` | Lança `InvalidOperationException` citando `Identity:Email:Provider`. |

O `.eml` é uma mensagem MIME simples (RFC 5322): cabeçalhos `From`, `To`, `Subject`, `Date`, `Message-ID`, `MIME-Version`, `Content-Type: text/plain; charset=utf-8` e `Content-Transfer-Encoding: 8bit`, linhas terminadas em CRLF e corpo em UTF-8 sem BOM. O `Date` sai em UTC, a partir do `TimeProvider`. Um assunto com caracteres fora do ASCII (acentos) é codificado conforme a RFC 2047 (`=?utf-8?B?...?=`).

`AddIdentityModule` registra as opções, o validador, os dois adaptadores como singletons e `IEmailSender`, que resolve o adaptador conforme `Provider` (veja [`DependencyInjection.cs`](../../src/Modules/Ouroboros.Modules.Identity/DependencyInjection.cs)).

## Como usar nos casos de uso

Ainda nenhum caso de uso envia e-mail; o cadastro (#20), a redefinição de senha (#22) e o convite (#25) devem seguir estas regras:

1. Gravar a transação (usuário, token etc.) antes de enviar.
2. Montar o `EmailMessage` com o link no corpo e chamar `IEmailSender.SendAsync`.
3. Não devolver o token na resposta HTTP nem registrá-lo no log; também não registrar o corpo da mensagem.
4. Se o envio falhar, não há reenvio automático: o usuário faz uma nova solicitação.

## Ver os e-mails em desenvolvimento

Com um host rodando em `Development`, cada envio cria um arquivo em `.local/emails/`, na raiz do repositório. Abra o `.eml` em qualquer cliente de e-mail (Outlook, Thunderbird, Mail) ou num editor de texto. Para limpar, apague os arquivos da pasta.

## Limitações

- Não há envio real: SMTP, Mailpit, mensageria, outbox e a notificação "sua senha foi alterada" estão fora do escopo. Um adaptador SMTP entra quando houver infraestrutura, como novo valor de `Provider`.
- Sem outbox, uma falha entre o commit e o envio perde a mensagem; a recuperação é o usuário refazer a solicitação.
- Mensagens só em texto simples, sem HTML nem anexos.
- `From` não é validado; o cabeçalho é escrito como configurado.

## Alternativas descartadas

- **Devolver o token na resposta ou no log em desenvolvimento:** facilita testar, mas o mesmo código poderia vazar o token em outro ambiente e quebraria a regra de que só o dono do e-mail recebe o link.
- **SMTP com Mailpit ou outro servidor local:** exige infraestrutura que o projeto ainda não tem; o pickup directory dá o mesmo resultado para inspeção, sem dependências.
- **Outbox transacional:** garante a entrega depois do commit, mas exige tabela e processamento em segundo plano. O custo não se justifica enquanto refazer a solicitação basta.
- **Descartar mensagens sem provedor (no-op):** esconderia configuração esquecida; a falha explícita aparece no primeiro envio.

## Consequências

- Um ambiente que precise enviar e-mails (Staging, Production) depende de um adaptador real; até lá, os fluxos que enviam falham nesses ambientes.
- Os casos de uso dependem só da porta; trocar o meio de entrega não muda o código deles.
- Os arquivos em `.local/emails` contêm tokens válidos de desenvolvimento e ficam fora do Git.

## Testes

- [`EmailTests`](../../tests/Ouroboros.IntegrationTests/EmailTests.cs), em `Ouroboros.IntegrationTests`, sem banco nem Docker: o `.eml` tem destinatário, assunto e corpo; o log não contém o corpo nem o token; o assunto acentuado é codificado; quebras de linha no destinatário ou no assunto são recusadas; o `Date` sai em UTC; o validador recusa o adaptador local fora de `Development` e o aceita em `Development`; enviar sem provedor falha; a API sem provedor resolve o `UnconfiguredEmailSender`.
- [`EmailConfigurationTests`](../../tests/Ouroboros.ArchitectureTests/EmailConfigurationTests.cs), em `Ouroboros.ArchitectureTests`: o host fora de `Development` com o adaptador local não sobe; sem provedor, sobe.

Na validação da #10, a suíte tinha 108 testes. O QA também subiu um host real em `Development`, que gravou o `.eml`, e confirmou que em `Production` e `Staging` o host não sobe, com mensagem clara.

## Referências

- [OWASP Forgot Password Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Forgot_Password_Cheat_Sheet.html)

## Origem

- [#10](https://github.com/adnerscarpelini/ouroboros-server/issues/10): envio de e-mails por uma porta com adaptador local.

[Voltar à arquitetura](README.md)
