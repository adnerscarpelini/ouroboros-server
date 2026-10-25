# Portas de leitura e escrita de parâmetros

Identity lê e escreve seus parâmetros por portas de Application, implementadas por adaptadores em Infrastructure. O contrato público é `Ouroboros.Identity.Contracts.ISystemParameters`; o contexto de runtime é `IdentityDbContext`. Consulte [parâmetros locais de Identity](parameters.md) para os caminhos, a migração, o backup e a recuperação. Origem: a [#73](https://github.com/adnerscarpelini/ouroboros-server/issues/73) introduziu as portas e a [#81](https://github.com/adnerscarpelini/ouroboros-server/issues/81) transferiu esses tipos, regras e dados para Identity; o schema `settings` do banco original fica retido, sem leitores nem escritores em runtime.

## Leitura

`SystemParameters` recebe `IParameterReader`. A porta retorna o valor textual ou `null` para chave ausente; o caso de uso rejeita chave nula, vazia ou em branco e converte a ausência em `ParameterNotFoundException`, preservando o contrato público.

O adaptador `ParameterReader`, em Infrastructure, consulta o PostgreSQL a cada chamada, sem cache e sem rastrear entidades. Uma leitura posterior observa a alteração confirmada, inclusive se o mesmo escopo tiver rastreado uma versão antiga do parâmetro. A interpretação do texto continua com o consumidor.

## Escrita e unidade de trabalho

`ParameterWriter` recebe `IParameterPersistence` e `TimeProvider`. A porta oferece operações completas:

| Operação | Unidade confirmada |
| --- | --- |
| `CreateAsync` | Parâmetro validado pelo domínio e seu histórico de criação, com revisão 0 |
| `ChangeAsync` | Carregamento por chave, alteração pelo domínio e histórico com revisão incrementada, autor e instante |

Valor e descrição aceitam alteração conjunta ou separada; `null` mantém o campo vigente. Tipo, padrão de validação e sensibilidade são preservados no histórico. Alteração sem diferença ou valor inválido continua recusada pelas [regras dos parâmetros](../architecture/system-parameters.md).

O adaptador `ParameterPersistence` adiciona o histórico e confirma a operação com um único `SaveChangesAsync`. A transação local mantém valor, descrição, autor, revisão e histórico juntos: falha na gravação do histórico também desfaz a inclusão ou alteração do parâmetro. Não há chamadas HTTP, broker ou envio de e-mail nessa unidade de trabalho.

Chave ausente na alteração produz `ParameterNotFoundException`. Conflitos de concorrência do EF e violações de unicidade do PostgreSQL são reconhecidos em Infrastructure e convertidos em `ParameterConflictException`, interna de Application, com a chave envolvida. Uma revisão concorrente recusada não sobrescreve o valor nem o autor da alteração vencedora.

As portas não expõem `DbContext`, `IQueryable`, transações do provedor ou operações SQL individuais. EF Core e reconhecimento de erros PostgreSQL ficam nos adaptadores. `AddIdentityModule` registra os casos de uso e as portas/adaptadores como scoped.

## Verificação

Use o SDK fixado em `global.json` e Docker em execução. A suíte de integração cria PostgreSQL 18 e bancos isolados via Testcontainers; não utiliza o banco do Compose. Comandos na raiz, também usados pelo QA independente da entrega:

```powershell
dotnet build Ouroboros.slnx --no-restore -c Release
dotnet test --solution Ouroboros.slnx -c Release --no-build
```

Restaure as dependências antes do build em um checkout novo, conforme o [guia de desenvolvimento](../development.md#visual-studio-e-compilação).

- [SystemParametersTests](../../tests/Ouroboros.IntegrationTests/SystemParametersTests.cs): leitura vigente, autor e revisão, criação/alteração com histórico, rollback por falha do histórico, chave ausente, valores inválidos e conflito de duas cópias da mesma revisão.
- [ParameterRulesTests](../../tests/Ouroboros.IntegrationTests/ParameterRulesTests.cs): tipos, padrão, sensibilidade e regras de alteração.
- [Testes de dependência](../../tests/Ouroboros.ArchitectureTests/Dependencies/SourceTreeDependencyTests.cs): Application sem EF ou Infrastructure, mantendo as regras da #70.

O QA executou esses cenários em Windows com PostgreSQL real. O conflito usa intercalação determinística de dois escopos; não é ensaio de carga nem execução da CI Linux.

[Voltar à documentação](../README.md)
