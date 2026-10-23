# Parâmetros do sistema com histórico de alterações

## Contexto

O sistema terá muitos parâmetros de negócio, como o domínio aceito para administradores e os aprovadores de convites. Os valores precisam ser editáveis sem nova versão do código, consultáveis por chave e auditáveis: deve ser possível saber quem mudou o quê e quando.

## Decisão

- **Módulo próprio, `Settings`**, em [`src/Modules/Ouroboros.Modules.Settings`](../../src/Modules/Ouroboros.Modules.Settings/Ouroboros.Modules.Settings.csproj), com schema `settings`, `SettingsDbContext` e histórico de migrations próprio ([Persistência](persistence.md)). Os dois hosts o registram com `AddSettingsModule`.
- **Valores no banco, lidos a cada uso.** Os outros módulos leem parâmetros só pelo contrato [`ISystemParameters`](../../src/Modules/Ouroboros.Modules.Settings/Contracts/ISystemParameters.cs) de `Settings.Contracts`. Toda leitura consulta a tabela: não há valor em código, em arquivo de configuração nem em cache, e uma alteração vale na próxima leitura, sem reiniciar a API.
- **Histórico espelho gravado pela aplicação.** Cada inclusão ou alteração grava uma cópia completa em `parameter_history` no mesmo `SaveChanges` (mesma transação) da alteração. A aplicação grava a cópia, e não um trigger, porque é ela que conhece o autor.
- **Validação na gravação.** Cada parâmetro tem tipo e padrão opcional na própria tabela; um valor que não os respeita é recusado antes de gravar.
- **Segredos ficam fora.** A chave de assinatura dos tokens JWT, senhas e a connection string não são parâmetros desta tabela: continuam em user-secrets ou variáveis de ambiente ([README](../../README.md#chave-de-assinatura-dos-tokens)).

### Leitura pelo contrato

```csharp
public interface ISystemParameters
{
    Task<string> GetAsync(string key, CancellationToken cancellationToken);
}
```

- Devolve o valor como texto; quem lê interpreta conforme o tipo do parâmetro (por exemplo, separa a lista de e-mails por vírgula).
- Chave inexistente lança `ParameterNotFoundException` (também em `Contracts`), com a chave na propriedade `Key`. Chave nula ou em branco lança `ArgumentException`.
- A implementação ([`SystemParameters`](../../src/Modules/Ouroboros.Modules.Settings/Application/Parameters/SystemParameters.cs)) é `internal` e registrada como scoped.

Um módulo que precise de parâmetros passa a referenciar o projeto do Settings e usa apenas `Contracts`, conforme a [regra de dependência entre módulos](module-structure.md#fronteiras-adotadas-para-a-evolução). O primeiro é o Identity, desde a [#11](https://github.com/adnerscarpelini/ouroboros-server/issues/11): a [criação do primeiro administrador](../identity/first-admin.md) lê `Identity:AdminInvitations:AllowedEmailDomain`, desde a [#13](https://github.com/adnerscarpelini/ouroboros-server/issues/13), o [login](../identity/login.md) lê os parâmetros do [bloqueio de conta](../identity/account-lockout.md#configuração) em toda tentativa, e, desde a [#20](https://github.com/adnerscarpelini/ouroboros-server/issues/20), a [solicitação de cadastro](../identity/registration.md#configuração) lê a validade e o endereço do link a cada solicitação, e, desde a [#22](https://github.com/adnerscarpelini/ouroboros-server/issues/22), a [solicitação de redefinição de senha](../identity/password-reset.md#configuração) faz o mesmo com os parâmetros próprios.

### Tabela `settings.parameters`

Entidade [`Parameter`](../../src/Modules/Ouroboros.Modules.Settings/Domain/Parameters/Parameter.cs), mapeada por [`ParameterConfiguration`](../../src/Modules/Ouroboros.Modules.Settings/Infrastructure/Persistence/Configurations/ParameterConfiguration.cs). Além de `id`, `external_id`, `created_at` e `updated_at` das [convenções das entidades](entity-conventions.md):

| Coluna | Tipo | Conteúdo |
| --- | --- | --- |
| `key` | `varchar(200)`, índice único | Chave do parâmetro; por convenção, `<Módulo>:<Área>:<Nome>`, por exemplo `Identity:AdminInvitations:AllowedEmailDomain`. |
| `value` | `text` | Valor, sempre em texto. |
| `description` | `text` | Obrigatória: para que serve, quem usa, formato e exemplos aceitos e o efeito de alterar. |
| `data_type` | `varchar(20)` | Nome do enum [`ParameterDataType`](../../src/Modules/Ouroboros.Modules.Settings/Domain/Parameters/ParameterDataType.cs): `String`, `Integer`, `Boolean`, `Email`, `EmailList` ou `Domain`. |
| `validation_pattern` | `text`, nulo | Regex opcional, aplicada ao valor inteiro. |
| `is_sensitive` | `boolean` | Marca parâmetro sensível, cuja alteração deverá avisar os administradores (o aviso não faz parte desta entrega). |
| `revision` | `integer` | Começa em 0 e sobe a cada alteração; é o token de concorrência. |
| `updated_by` | `uuid`, nulo | `ExternalId` de quem fez a última alteração; nulo quando o valor veio de migration. |

### Tabela `settings.parameter_history`

Entidade [`ParameterHistory`](../../src/Modules/Ouroboros.Modules.Settings/Domain/Parameters/ParameterHistory.cs). Cada linha é uma cópia do parâmetro naquela gravação: `parameter_key`, `revision` (a revisão depois da gravação), `previous_value` e `new_value`, `previous_description` e `new_description`, `data_type`, `validation_pattern`, `is_sensitive`, `changed_by` (`ExternalId` do autor, nulo quando veio de migration) e `changed_at`, além das colunas das convenções. Na inclusão, a revisão é 0 e os campos anteriores são nulos. O índice único `(parameter_key, revision)` impede duas linhas para a mesma revisão.

O histórico não tem foreign key para `parameters`: é uma cópia, ligada pela chave.

### Gravação e concorrência

O [`ParameterWriter`](../../src/Modules/Ouroboros.Modules.Settings/Application/Parameters/ParameterWriter.cs) (`internal`) inclui e altera parâmetros, adicionando a linha de histórico ao mesmo `SaveChanges`:

- `CreateAsync` inclui um parâmetro, com autor opcional.
- `ChangeAsync` altera valor e/ou descrição (nulo mantém o atual), com autor obrigatório. Chave inexistente lança `ParameterNotFoundException`. Tipo, padrão e sensibilidade não são alterados por ele.

Se duas alterações concorrentes partem da mesma revisão, a primeira grava e a segunda falha inteira com `DbUpdateException` (pela revisão ou pelo índice único do histórico): nem o valor nem o histórico da segunda são gravados, e a primeira não se perde.

Ainda não há endpoints: a alteração e a consulta pelo backoffice chegam com a [#34](https://github.com/adnerscarpelini/ouroboros-server/issues/34) e a [#35](https://github.com/adnerscarpelini/ouroboros-server/issues/35).

### Regras de validação

Aplicadas na inclusão e na alteração por [`Parameter`](../../src/Modules/Ouroboros.Modules.Settings/Domain/Parameters/Parameter.cs) e [`ParameterValueRules`](../../src/Modules/Ouroboros.Modules.Settings/Domain/Parameters/ParameterValueRules.cs). Uma violação lança `InvalidParameterException` (interna ao módulo), com a regra violada, e nada é gravado.

- A chave é obrigatória e tem até 200 caracteres; a descrição é obrigatória (não pode ser vazia nem só espaços).
- Uma alteração que não muda nem o valor nem a descrição é recusada.
- Nenhum valor aceita caracteres de controle (como `\n` e `\t`), caracteres de formato invisíveis (como o zero-width space) nem separadores de linha ou parágrafo Unicode.
- Por tipo:

| Tipo | Valor aceito | Exemplo fictício |
| --- | --- | --- |
| `String` | Qualquer texto, respeitada a regra de caracteres. | `Bem-vindo` |
| `Integer` | Inteiro de 64 bits, com sinal opcional, sem espaços nem separadores. | `-10` |
| `Boolean` | Exatamente `true` ou `false`. | `true` |
| `Email` | E-mail em minúsculas, sem espaços, com parte local ASCII sem aspas (letras, dígitos e `._%+-`, até 64) e host com domínio válido. | `ana@example.com` |
| `EmailList` | E-mails válidos separados por vírgula (espaços em volta de cada um são ignorados), sem entradas vazias nem repetidas. | `ana@example.com,bia@example.com` |
| `Domain` | Domínio em minúsculas, sem `@`, com rótulos de letras, dígitos e hífen e terminação alfabética. | `example.com` |

- `validation_pattern`, quando existe, precisa casar com o valor inteiro: ele é envolvido em `\A(?:<padrão>)\z`, com tempo limite de 1 segundo. As âncoras `\A` e `\z` são usadas porque, em regex .NET, `$` também casa antes de um `\n` final.

## Valores iniciais e novas chaves

Novas chaves entram pela migration da funcionalidade que as usa, com valor, descrição e tipo iniciais e uma linha de histórico de inclusão; depois, ficam editáveis (pelo backoffice, a partir da #34).

A migration `InitialCreate` do Settings cria as duas tabelas e inclui, por SQL, os parâmetros abaixo, cada um com sua linha de histórico (revisão 0, sem autor):

| Chave | Valor | Tipo | Sensível |
| --- | --- | --- | --- |
| `Identity:AdminInvitations:AllowedEmailDomain` | `adnerscarpelini.com` | `Domain` | sim |
| `Identity:AdminInvitations:ApproverEmails` | `oi@adnerscarpelini.com` | `EmailList` | sim |

A migration `AddLockoutParameters` do Settings, da [#13](https://github.com/adnerscarpelini/ouroboros-server/issues/13), inclui da mesma forma os parâmetros do [bloqueio de conta](../identity/account-lockout.md#configuração):

| Chave | Valor | Tipo | Padrão (`validation_pattern`) | Sensível |
| --- | --- | --- | --- | --- |
| `Identity:Lockout:MaxFailedAttempts` | `5` | `Integer` | `[1-9][0-9]?` (1 a 99) | não |
| `Identity:Lockout:DurationMinutes` | `15` | `Integer` | `[1-9][0-9]{0,2}\|1[0-3][0-9]{2}\|14[0-3][0-9]\|1440` (1 a 1440) | não |

A migration `AddRegistrationParameters` do Settings, da [#20](https://github.com/adnerscarpelini/ouroboros-server/issues/20), inclui da mesma forma os parâmetros da [solicitação de cadastro de cliente](../identity/registration.md#configuração):

| Chave | Valor | Tipo | Padrão (`validation_pattern`) | Sensível |
| --- | --- | --- | --- | --- |
| `Identity:Registration:TokenLifetimeHours` | `24` | `Integer` | `[1-9]\|[1-9][0-9]\|1[0-5][0-9]\|16[0-8]` (1 a 168) | não |
| `Identity:Registration:CompletionUrl` | `http://localhost:3000/cadastro/concluir` | `String` | `https?://[^\s?#]+` (URL http ou https sem espaços, `?` nem `#`) | não |

O sistema acrescenta `?token=<token>` ao `CompletionUrl`, por isso o padrão recusa query string e fragmento. Alterar qualquer um dos dois vale para os próximos links; os já enviados mantêm a validade com que foram gerados.

A migration `AddPasswordResetParameters` do Settings, da [#22](https://github.com/adnerscarpelini/ouroboros-server/issues/22), inclui da mesma forma os parâmetros da [solicitação de redefinição de senha](../identity/password-reset.md#configuração), com as mesmas regras de `CompletionUrl` e de alteração:

| Chave | Valor | Tipo | Padrão (`validation_pattern`) | Sensível |
| --- | --- | --- | --- | --- |
| `Identity:PasswordReset:TokenLifetimeMinutes` | `60` | `Integer` | `[1-9][0-9]{0,2}\|1[0-3][0-9]{2}\|14[0-3][0-9]\|1440` (1 a 1440) | não |
| `Identity:PasswordReset:CompletionUrl` | `http://localhost:3000/senha/redefinir` | `String` | `https?://[^\s?#]+` (URL http ou https sem espaços, `?` nem `#`) | não |

Quem lê um parâmetro numérico continua responsável por interpretá-lo: o Identity recusa, com erro, um valor de bloqueio que não seja inteiro positivo ou uma validade de link fora de 1 a 168 horas (cadastro) ou de 1 a 1440 minutos (redefinição de senha), mesmo que tenha sido gravado fora da aplicação.

O SQL usa `uuidv7()` para o `external_id`, função que só existe a partir do PostgreSQL 18 (a versão do [`compose.yaml`](../../compose.yaml) e dos testes).

## Como aplicar as migrations

Na raiz do repositório, com o banco local configurado conforme o [README](../../README.md#banco-de-dados-local):

```powershell
dotnet ef database update --project src/Modules/Ouroboros.Modules.Settings --startup-project src/Ouroboros.Api --context SettingsDbContext
```

Para gerar uma migration nova do Settings:

```powershell
dotnet ef migrations add <Nome> --project src/Modules/Ouroboros.Modules.Settings --startup-project src/Ouroboros.Api --context SettingsDbContext --output-dir Infrastructure/Persistence/Migrations
```

## Alternativas descartadas

- **Parâmetros em `appsettings.json` ou variáveis de ambiente:** mudar um valor exigiria novo deploy ou reinício, e não haveria histórico de autor.
- **Cache dos valores:** leituras mais baratas, mas uma alteração não valeria na próxima leitura sem um mecanismo de invalidação; com o volume atual, a consulta por chave única basta.
- **Histórico por trigger no PostgreSQL:** o banco não conhece o usuário autenticado; seria preciso repassá-lo por variável de sessão. A gravação pela aplicação, no mesmo `SaveChanges`, mantém a atomicidade e registra o autor.
- **Validar todas as chaves na subida da API:** exigiria que o Settings conhecesse as chaves de cada módulo.

## Limitações e consequências

- Uma chave apagada ou com valor inválido gravado fora da aplicação só é percebida quando a funcionalidade a lê, não na subida da API. No caso dos parâmetros do bloqueio de conta, todo login passa a responder `500`; no dos parâmetros do cadastro, toda solicitação de cadastro; no dos parâmetros da redefinição de senha, toda solicitação de redefinição, qualquer que seja o e-mail.
- Cada leitura é uma consulta ao banco.
- O histórico guarda valores de parâmetros sensíveis em texto: segredos nunca devem virar parâmetros.
- A consistência entre o parâmetro e o histórico vale porque os dois estão no mesmo DbContext; uma gravação que envolva outro módulo exigiria uma decisão de consistência ([Estrutura dos módulos](module-structure.md#fronteiras-adotadas-para-a-evolução)).

## Testes

Em [`tests/Ouroboros.IntegrationTests`](../../tests/Ouroboros.IntegrationTests/Ouroboros.IntegrationTests.csproj) (o Settings concede `InternalsVisibleTo` a esse projeto):

- [`SystemParametersTests`](../../tests/Ouroboros.IntegrationTests/SystemParametersTests.cs) (contra PostgreSQL real): leitura dos valores iniciais pelo contrato; descrição, tipo e histórico de inclusão dos valores iniciais (uma linha de revisão 0 para cada parâmetro semeado); todo valor semeado respeita as regras do próprio tipo e padrão, o que cobre também os parâmetros que migrations futuras incluírem; alteração visível na próxima leitura; histórico com valores e descrição anteriores e novos e autor; valor inválido recusado sem gravar nada; valor e histórico na mesma transação; chave inexistente como erro explícito; inclusão com histórico; detecção de alterações concorrentes.
- [`ParameterRulesTests`](../../tests/Ouroboros.IntegrationTests/ParameterRulesTests.cs) (unidade, sem banco): valores aceitos e recusados por tipo, padrão aplicado ao valor inteiro, alteração sem diferença recusada, cópia de tipo, padrão e sensibilidade no histórico, descrição obrigatória e incremento da revisão.
- [`ModuleDataBoundaryTests`](../../tests/Ouroboros.IntegrationTests/ModuleDataBoundaryTests.cs): cada DbContext de módulo só mapeia tabelas do próprio schema, e os DbContexts do Identity e do Settings são descobertos.

Na validação da #33, a suíte tinha 167 testes. Os testes que exercitam os parâmetros do bloqueio no login estão em [Bloquear a conta](../identity/account-lockout.md#como-verificar).

## Origem

- [#33](https://github.com/adnerscarpelini/ouroboros-server/issues/33): módulo Settings, tabelas de parâmetros e histórico, contrato de leitura e valores iniciais.
- [#13](https://github.com/adnerscarpelini/ouroboros-server/issues/13): parâmetros do bloqueio de conta e migration `AddLockoutParameters`.
- [#20](https://github.com/adnerscarpelini/ouroboros-server/issues/20): parâmetros da solicitação de cadastro e migration `AddRegistrationParameters`.
- [#22](https://github.com/adnerscarpelini/ouroboros-server/issues/22): parâmetros da solicitação de redefinição de senha e migration `AddPasswordResetParameters`.

[Voltar à arquitetura](README.md)
