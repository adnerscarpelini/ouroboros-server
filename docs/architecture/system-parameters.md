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

Um módulo que precise de parâmetros passa a referenciar o projeto do Settings e usa apenas `Contracts`, conforme a [regra de dependência entre módulos](module-structure.md#fronteiras-adotadas-para-a-evolução). O primeiro será o Identity, na [#11](https://github.com/adnerscarpelini/ouroboros-server/issues/11); hoje nenhum módulo lê parâmetros.

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

- Uma chave apagada ou com valor inválido gravado fora da aplicação só é percebida quando a funcionalidade a lê, não na subida da API.
- Cada leitura é uma consulta ao banco.
- O histórico guarda valores de parâmetros sensíveis em texto: segredos nunca devem virar parâmetros.
- A consistência entre o parâmetro e o histórico vale porque os dois estão no mesmo DbContext; uma gravação que envolva outro módulo exigiria uma decisão de consistência ([Estrutura dos módulos](module-structure.md#fronteiras-adotadas-para-a-evolução)).

## Testes

Em [`tests/Ouroboros.IntegrationTests`](../../tests/Ouroboros.IntegrationTests/Ouroboros.IntegrationTests.csproj) (o Settings concede `InternalsVisibleTo` a esse projeto):

- [`SystemParametersTests`](../../tests/Ouroboros.IntegrationTests/SystemParametersTests.cs) (contra PostgreSQL real): leitura dos valores iniciais pelo contrato; descrição, tipo e histórico de inclusão dos valores iniciais; alteração visível na próxima leitura; histórico com valores e descrição anteriores e novos e autor; valor inválido recusado sem gravar nada; valor e histórico na mesma transação; chave inexistente como erro explícito; inclusão com histórico; detecção de alterações concorrentes.
- [`ParameterRulesTests`](../../tests/Ouroboros.IntegrationTests/ParameterRulesTests.cs) (unidade, sem banco): valores aceitos e recusados por tipo, padrão aplicado ao valor inteiro, alteração sem diferença recusada, cópia de tipo, padrão e sensibilidade no histórico, descrição obrigatória e incremento da revisão.
- [`ModuleDataBoundaryTests`](../../tests/Ouroboros.IntegrationTests/ModuleDataBoundaryTests.cs): cada DbContext de módulo só mapeia tabelas do próprio schema, e os DbContexts do Identity e do Settings são descobertos.

Na validação da #33, a suíte tinha 167 testes.

## Origem

- [#33](https://github.com/adnerscarpelini/ouroboros-server/issues/33): módulo Settings, tabelas de parâmetros e histórico, contrato de leitura e valores iniciais.

[Voltar à arquitetura](README.md)
