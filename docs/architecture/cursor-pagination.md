# Paginação por cursor

## Contrato e posição

A [#30](https://github.com/adnerscarpelini/ouroboros-server/issues/30) introduz paginação keyset, aplicada primeiro à [listagem administrativa de parâmetros de Identity](../identity/parameters.md#listagem-administrativa). O contrato [`PageRequest` e `CursorPage<T>`](../../src/Services/Identity/Ouroboros.Identity/Contracts/Parameters/IListParameters.cs) recebe `limit` (padrão 50, mínimo 1, máximo 100) e `after` opcional. A resposta HTTP usa nomes camelCase:

```json
{
  "data": [],
  "hasMore": false,
  "nextCursor": null
}
```

O cliente começa sem `after` e, enquanto `hasMore` for `true`, envia o `nextCursor` recebido como `after` da próxima chamada. `data` contém até `limit` itens. O leitor busca `limit + 1` para detectar continuidade; o cursor aponta para o último item devolvido, não para o item adicional. Na última página, `hasMore` é `false` e `nextCursor` é `null`; uma página vazia usa `data: []`.

A primeira consulta ordena pelo identificador público `ExternalId` (UUIDv7) crescente. As seguintes selecionam `ExternalId > posição`, com a mesma ordenação e `Take(limit + 1)`, sem `OFFSET`. A posição é exclusiva: o item que originou o cursor não se repete. Inserções anteriores à posição não deslocam a navegação; registros posteriores podem aparecer nas próximas páginas. Cada chamada consulta o estado atual, sem snapshot de toda a navegação, contagem total ou salto por número de página.

## Cursor e validação

[`ParameterCursor`](../../src/Services/Identity/Ouroboros.Identity/Application/Parameters/ParameterCursor.cs) codifica um JSON de versão 1 com escopo, posição Guid e `ChangedAt` opcional em Base64url, sem padding. Na listagem, o escopo é `identity:parameters:v1:external-id:asc`, que vincula a posição à consulta e à ordenação. O consumidor deve tratar o cursor como opaco e devolvê-lo sem editar. O limite não integra o cursor: é permitido mudar o tamanho da página entre chamadas.

O cursor não é token de autorização, não é assinado e não contém segredo. É uma posição de consulta, que não concede acesso a dados: autenticação e autorização continuam obrigatórias em cada chamada.

Limite fora de 1–100, valor não numérico, cursor vazio, maior que 1024 caracteres, Base64url/JSON inválido, versão ou escopo incompatíveis e posição Guid vazia/inválida retornam `400` com ProblemDetails. Ausência de `after` inicia a navegação; enviar `after=` é inválido. Não reutilize cursores entre consultas ou ordenações diferentes.

### Histórico de parâmetro

A [#35](https://github.com/adnerscarpelini/ouroboros-server/issues/35) reutiliza o envelope na [consulta de histórico](../identity/parameters.md#histórico-administrativo), com ordenação composta `ChangedAt DESC, ExternalId DESC`. A posição guarda ambos: a próxima página seleciona `ChangedAt < instante` ou, para o mesmo instante, `ExternalId < id`, sem `OFFSET`. Revisões novas anteriores à posição na ordem de navegação não deslocam os itens restantes; cada chamada continua sem snapshot global.

O escopo é o prefixo `identity:parameter-history:v1:changed-at-desc:id-desc:` seguido do SHA-256 hexadecimal dos bytes UTF-8 da chave. A chave bruta não integra o cursor. O hash vincula o recurso à ordenação com comprimento fixo, inclusive para chaves Unicode longas; não assina o cursor nem substitui autorização.

O campo opcional `ChangedAt` foi acrescentado à versão 1: cursores antigos da listagem, sem esse campo, continuam compatíveis. No histórico, a data é obrigatória e somente UTC (offset zero) é aceita. Cursores sem data, com data inválida ou não UTC, de outra chave ou da listagem são recusados com `400`. O sentido inverso, cursor de histórico usado na listagem, também é recusado pelo escopo. O limite permanece fora do cursor, permitindo mudar o tamanho da página.

## Camadas e verificação

[`ListParameters`](../../src/Services/Identity/Ouroboros.Identity/Application/Parameters/ListParameters.cs) valida a página, monta o envelope e usa a porta interna `IParameterListReader`. [`ParameterListReader`](../../src/Services/Identity/Ouroboros.Identity/Infrastructure/Persistence/ParameterListReader.cs) aplica filtro, ordenação, limite e projeção no banco, sem rastreamento. O DTO e os contratos públicos pertencem a Identity; esta entrega não impõe uma biblioteca de regras entre serviços.

Com os pré-requisitos de [desenvolvimento](../development.md), execute com Microsoft.Testing.Platform:

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class "*ParameterPaginationTests"
```

O QA independente aprovou 13 casos de [`ParameterPaginationTests`](../../tests/Ouroboros.IntegrationTests/ParameterPaginationTests.cs), incluindo página padrão/máxima, continuação após inserção, troca de limite, erros de cursor e autorização atual do administrador. O encaminhamento administrativo e a recusa externa têm cobertura em [`GatewayIntegrationTests`](../../tests/Ouroboros.IntegrationTests/GatewayIntegrationTests.cs), com TestServer e `InMemoryIdentityDownstreamFactory`. Esses resultados não representam execução da suíte completa de integração nem teste desses cenários em Kestrel real.

[Voltar à arquitetura](README.md)
