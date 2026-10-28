# Parâmetros locais de Identity

Identity possui seus parâmetros e histórico desde a [#81](https://github.com/adnerscarpelini/ouroboros-server/issues/81). As tabelas `identity.parameters` e `identity.parameter_history` usam o mesmo `IdentityDbContext`, banco e connection string do restante de Identity. A migração de propriedade acontece dentro do banco em uso; o [provisionamento de bancos e roles](../development.md#provisionar-bancos-e-roles-por-serviço) é da [#92](https://github.com/adnerscarpelini/ouroboros-server/issues/92) e a [transferência do banco inteiro de Identity](../development.md#transferir-o-banco-de-identity), da [#82](https://github.com/adnerscarpelini/ouroboros-server/issues/82); os parâmetros e o histórico seguem junto com o restante do schema `identity` nessa transferência.

## Leitura e escrita

O contrato [`Identity.Contracts.ISystemParameters`](../../src/Services/Identity/Ouroboros.Identity/Contracts/ISystemParameters.cs) recebe chave e cancelamento e devolve valor textual. `SystemParameters` usa a porta interna `IParameterReader`; `ParameterReader` consulta `identity.parameters` sem cache e sem rastreamento. Chave em branco é recusada, ausência gera `ParameterNotFoundException` e não há fallback para o schema `settings` nem valor padrão silencioso. Login, cadastro, recuperação e primeiro administrador leem localmente pelo contrato do proprietário.

`ParameterWriter` usa `IParameterPersistence`; `ParameterPersistence` aplica as [regras de tipo, padrão e sensibilidade](../architecture/system-parameters.md), grava parâmetro e histórico num único `SaveChangesAsync` e preserva autor, instante e revisão. Os tipos de domínio e os adaptadores estão no próprio Identity; registros são scoped em `AddIdentityModule`. EF, SQL e tradução de concorrência/unicidade permanecem em Infrastructure. Revisão é token de concorrência; a combinação chave/revisão do histórico é única, e conflito desfaz a operação inteira.

A listagem administrativa está disponível desde a #30, a consulta HTTP de histórico desde a #35 e a edição com aviso transacional desde a #34, descritas abaixo. As [portas introduzidas na #73](parameter-ports.md) mantêm sua unidade de trabalho, agora no proprietário.

## Listagem administrativa

`GET /api/v1/admin/settings/parameters?limit=50&after=<cursor>` lista os parâmetros de Identity. Na primeira chamada, omita `after`. O segmento histórico `settings` preserva o contrato v1; não representa outro serviço nem leitura do schema legado. O endpoint existe somente no listener administrativo, acessível diretamente ou pelo gateway administrativo; o gateway externo recusa essa rota com `404`.

A política nomeada `settings:parameters.read` exige papel `Admin`. Além da audiência administrativa do token, cada chamada consulta o usuário atual no banco e exige que exista, esteja ativo e continue `Admin`; token antigo não preserva autorização após desativação ou mudança de papel. Ausência de autenticação ou audiência inválida retorna `401`; papel do token incompatível é recusado pela autorização (`403`), e usuário ausente/inativo ou sem papel administrativo atual retorna `401`.

A resposta `200` é `CursorPage<ParameterView>`, com `data`, `hasMore` e `nextCursor`. Cada item contém `id` (ExternalId), `key`, `value`, `description`, `dataType`, `validationPattern`, `isSensitive`, `revision`, `updatedBy` e `updatedAt`. O valor é devolvido como texto; `isSensitive` informa a classificação do parâmetro. O contrato de [paginação por cursor](../architecture/cursor-pagination.md) define ordenação por ExternalId crescente, limite padrão 50/máximo 100, continuação, erros `400` e limites de consistência.

[`ParametersController`](../../src/Services/Identity/Ouroboros.Identity.Api/Controllers/Admin/ParametersController.cs) usa `IListParameters` e `IGetCurrentUser`. `ControllerRouteTests` mantém uma exceção nominal para a rota compatível deste controller de Identity. A #30 entrega a listagem atual; a #35 acrescenta o histórico abaixo. A edição é descrita na seção seguinte (#34).

Origem: [#30](https://github.com/adnerscarpelini/ouroboros-server/issues/30). Comando e cobertura de verificação em [Paginação por cursor](../architecture/cursor-pagination.md#camadas-e-verificação).

## Histórico administrativo

`GET /api/v1/admin/settings/parameters/{key}/history?limit=50&after=<cursor>` consulta o histórico da chave informada, incluindo a criação (`revision: 0`). Use a chave codificada como segmento de URL e omita `after` na primeira chamada. Os registros vêm do banco próprio de Identity, em `identity.parameter_history`, sem consultar o schema legado `settings`.

A rota reutiliza `settings:parameters.read`, audiência administrativa e a consulta do usuário ativo com papel `Admin` atual no banco, como a listagem. Também é encaminhada pelo gateway administrativo e recusada no externo. Não escreve parâmetros nem cria revisões.

A resposta `200` usa o envelope `data`, `hasMore`, `nextCursor`. Cada item contém `id` (ExternalId), `key`, `revision`, `previousValue`, `newValue`, `previousDescription`, `newDescription`, `dataType`, `validationPattern`, `isSensitive`, `changedBy` e `changedAt`. Valores anteriores podem ser nulos na criação; `changedBy` pode ser nulo. Valores e descrições são os da revisão registrada, não uma reconstrução do estado atual.

A ordenação é `changedAt` decrescente, com desempate por `ExternalId` decrescente. A continuação seleciona datas menores que a posição ou, na mesma data, ids menores. O [cursor de histórico](../architecture/cursor-pagination.md#histórico-de-parâmetro) vincula chave, ordenação, instante UTC e id; limite padrão 50/máximo 100, tamanho de página variável e envelope são os mesmos da listagem. Chave inexistente retorna `404` com título `Parâmetro não encontrado.`. Limite/cursor inválido, cursor de outra chave ou da listagem e cursor sem data retornam `400`. A autorização é avaliada antes da consulta.

[`IGetParameterHistory`](../../src/Services/Identity/Ouroboros.Identity/Contracts/Parameters/IGetParameterHistory.cs) expõe o caso de uso [`GetParameterHistory`](../../src/Services/Identity/Ouroboros.Identity/Application/Parameters/GetParameterHistory.cs), que valida e pagina pela porta interna `IParameterHistoryReader`; [`ParameterHistoryReader`](../../src/Services/Identity/Ouroboros.Identity/Infrastructure/Persistence/ParameterHistoryReader.cs) consulta existência da chave e projeta o histórico sem rastreamento.

Com os pré-requisitos de [desenvolvimento](../development.md), execute:

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class "*ParameterHistoryTests"
```

O QA independente aprovou 12/12 casos de [`ParameterHistoryTests`](../../tests/Ouroboros.IntegrationTests/ParameterHistoryTests.cs), além de 13/13 da paginação e 2/2 dos cenários selecionados de gateway. A validação inclui paginação de chave Unicode longa, sem crescimento do cursor proporcional ao tamanho da chave. A persistência usa PostgreSQL real; as chamadas HTTP usam TestServer, e o encaminhamento de gateway usa `InMemoryIdentityDownstreamFactory`. Essa evidência não representa execução completa da suíte de integração nem desses cenários em Kestrel real.

Origem: [#35](https://github.com/adnerscarpelini/ouroboros-server/issues/35).

## Edição administrativa

`PUT /api/v1/admin/settings/parameters/{key}` altera valor, descrição ou ambos. Exige token da audiência administrativa, política `settings:parameters.write` com papel `Admin` e reautenticação pela senha atual. Consulte a revisão na listagem antes de alterar:

```json
{
  "revision": 0,
  "currentPassword": "uma frase administrativa longa e exclusiva",
  "value": "6",
  "description": "Limite de tentativas consecutivas"
}
```

`revision` é obrigatório e não negativo; `currentPassword` é obrigatório, com no máximo 1024 caracteres. `value` e `description` são opcionais, separadamente ou juntos; é necessário produzir mudança real. O domínio aplica tipo, padrão e demais [regras de parâmetros](../architecture/system-parameters.md). O endpoint não altera chave, tipo, padrão ou sensibilidade.

| Resposta | Comportamento |
| --- | --- |
| `204` | Alteração e nova revisão do histórico confirmadas, com intenções de aviso quando sensível; sem corpo. |
| `400` | Corpo inválido, ausência de mudança real ou valor/descrição recusado pelo domínio; ProblemDetails com a regra pertinente. |
| `401` | Senha inválida ou administrador inexistente, inativo, bloqueado ou sem papel atual; audiência inválida também é recusada. |
| `403` | Papel do token incompatível com a política. |
| `404` | Parâmetro inexistente. |
| `409` | Revisão divergente ou conflito concorrente; consulte o estado atual antes de decidir nova alteração. |
| `429` | Política `identity-parameter-write`: 10 requisições/minuto/IP, com `Retry-After`, antes de reautenticar. |

[`ChangeParameter`](../../src/Services/Identity/Ouroboros.Identity/Application/Parameters/ChangeParameter.cs), exposto por [`IChangeParameter`](../../src/Services/Identity/Ouroboros.Identity/Contracts/Parameters/IChangeParameter.cs), relê o administrador e verifica senha, atividade e bloqueio. A verificação do hash ocorre antes da transação; depois de travar o usuário, relê hash, atividade, bloqueio e papel. Uma credencial ou autorização modificada nesse intervalo impede a gravação. Senha incorreta retorna `401` sem incrementar a contagem de falhas neste contrato.

`ParameterPersistence.ChangeWithIntentsAsync` exige transação local ativa, confere a revisão e aplica a alteração pelo domínio. Parâmetro, histórico e intenções de e-mail são gravados no mesmo `SaveChangesAsync` e commit. Falha em qualquer gravação reverte o conjunto; duas alterações concorrentes da mesma revisão não confirmam ambas.

### Avisos de alteração sensível

Quando `isSensitive` é verdadeiro, inclusive na mudança somente de descrição, os destinatários são todos os administradores ativos resolvidos dentro da transação. O aviso contém somente a chave, o ExternalId do autor e a data UTC da alteração; não contém senha, valores anteriores/novos nem descrições. Parâmetros não sensíveis não geram aviso.

Cada aviso usa a [outbox protegida](email-outbox.md#avisos-de-parâmetros-sensíveis) com finalidade `parameter-change`, prazo de 24 horas e alias HMAC independente, sem cancelar avisos anteriores. Não há HTTP, broker ou transporte dentro da transação. A publicação e o processamento assíncronos reutilizam #86/#87: falha posterior não desfaz a alteração e mantém recuperação até o prazo da intenção.

**Ordem de atualização:** atualize Notifications para a versão que aceita `parameter-change` antes de utilizar o endpoint na nova versão de Identity. Consumidor antigo rejeita finalidade desconhecida. O envelope continua v1, e cadastro/reset mantêm seus contratos e links.

Com os pré-requisitos de [desenvolvimento](../development.md), execute:

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class "*ParameterChangeTests"
```

O QA independente aprovou 21/21 casos de [`ParameterChangeTests`](../../tests/Ouroboros.IntegrationTests/ParameterChangeTests.cs), incluindo concorrência, rollback por triggers, revalidação de hash/papel após lock, contratos inválidos, autorização e 11ª requisição recusada. Também aprovou 2 casos selecionados de gateway e 3 de proteção de e-mail. PostgreSQL real, HTTP em TestServer e transporte de gateway em memória foram usados. A falha de publicação após commit preservou intenção Pending e payload protegido; esta etapa não comprovou entrega até arquivo `.eml` via broker nem execução da suíte completa de integração.

Origem: [#34](https://github.com/adnerscarpelini/ouroboros-server/issues/34).

## Migração e preservação

A migration [`20261025142848_OwnIdentityParameters`](../../src/Services/Identity/Ouroboros.Identity/Infrastructure/Persistence/Migrations/20261025142848_OwnIdentityParameters.cs) cria as duas tabelas em `identity`, com PKs, índices únicos e colunas equivalentes. Na existência das duas tabelas legadas, obtém `SHARE` locks sobre elas, valida o histórico da revisão corrente, copia todos os campos e compara as linhas completas nos dois sentidos com `EXCEPT ALL` sobre JSONB. Preserva ids internos, identificadores públicos, valores, descrições, tipos, padrões, sensibilidade, revisões, autores e instantes, inclusive todo o histórico. Reposiciona as sequências acima dos ids copiados.

Cópia e validação pertencem à transação da migration. Origem incompleta, histórico corrente inconsistente ou divergência da cópia abortam a migration, inclusive a criação das tabelas de destino; o histórico de migrations não registra essa aplicação. A origem não é alterada. A versão nova não deve ser ativada nesse caso: seu leitor não acessa o schema `settings` como alternativa.

Sem nenhuma das duas tabelas de origem, a instalação limpa cria os oito parâmetros iniciais e seus históricos de revisão 0, usando `uuidv7()` do PostgreSQL 18. A migration posterior `AddAdminInvitations` acrescenta `Identity:AdminInvitations:TokenLifetimeHours` e `Identity:AdminInvitations:CompletionUrl`, com seus históricos, totalizando dez parâmetros iniciais no checkout atual; ver [convites](admin-invitations.md#parâmetros-e-segurança). Esse caminho não exige o schema `settings`. Não use instalação limpa para contornar uma origem existente incompleta ou inválida.

O schema `settings` fica retido no banco original, sem novas escritas. Nenhum projeto o referencia nem registra leitores ou escritores dele em runtime: o projeto Settings foi retirado pela [#96](https://github.com/adnerscarpelini/ouroboros-server/issues/96), que não exclui dados, e o destino dos dados antigos será decidido na #97.

## Upgrade de banco existente

O procedimento é manual e deve ser ensaiado em cópia isolada. Os exemplos usam o Compose local (`localhost:5432`, banco e usuário `ouroboros`); ajuste os nomes ao ambiente. Exigem clientes PostgreSQL 18 (`pg_dump`, `createdb`, `pg_restore`, `psql`) no PATH, autenticação protegida por prompt ou arquivo de senha com acesso restrito e espaço para backup/restauração. Não coloque senha na linha de comando nem no Git.

1. Pare a API de Identity, `create-admin`, comandos de migration concorrentes e qualquer outro escritor. Mantenha a janela sem escrita até validar o corte; o lock da cópia não substitui essa janela. Confira o histórico de migrations legado (`settings."__EFMigrationsHistory"`): o schema `settings` deve ter as quatro migrations antigas (`InitialCreate`, `AddLockoutParameters`, `AddRegistrationParameters` e `AddPasswordResetParameters`). O projeto Settings não existe mais, e este checkout não as aplica. Um ambiente que nunca as aplicou, sem as tabelas, usa a instalação limpa; um ambiente com o schema `settings` incompleto aplica as migrations restantes a partir do histórico do Git anterior à #96, que ainda contém o projeto, **antes** de migrar Identity e ainda nessa janela. A API de Identity não carrega nem aplica migrations de Settings.

2. Faça backup completo em formato custom e restaure-o em um banco novo e isolado. O nome com instante deve ser exclusivo; `createdb` deve falhar se já existir. Não reutilize nem sobrescreva banco preexistente:

   ```powershell
   $migrationStamp = Get-Date -Format 'yyyyMMddHHmmssfff'
   $backupDirectory = Join-Path (Get-Location) '.local/backups'
   New-Item -ItemType Directory -Force -Path $backupDirectory | Out-Null
   $parameterBackup = Join-Path $backupDirectory "before-identity-parameters-$migrationStamp.dump"
   $restoreDatabase = "ouroboros_restore_$migrationStamp"
   pg_dump --host localhost --port 5432 --username ouroboros --dbname ouroboros --format custom --file $parameterBackup
   if ($LASTEXITCODE -ne 0) { throw 'Backup falhou; não migrar.' }
   createdb --host localhost --port 5432 --username ouroboros $restoreDatabase
   if ($LASTEXITCODE -ne 0) { throw 'Banco de restauração não criado; não migrar.' }
   pg_restore --host localhost --port 5432 --username ouroboros --dbname $restoreDatabase --exit-on-error --no-owner $parameterBackup
   if ($LASTEXITCODE -ne 0) { throw 'Restauração falhou; não migrar.' }
   ```

   Proteja e preserve o arquivo fora do Git. O backup deve permitir recuperar o banco inteiro, não somente a lista de parâmetros. A migration não cria backup automaticamente.

3. Compare a restauração com a origem, incluindo todos os campos e histórico. Com os escritores ainda parados:

   ```powershell
   $parameterSnapshotSql = @'
   SELECT jsonb_build_object(
       'parameters', (SELECT jsonb_agg(to_jsonb(p) ORDER BY p.key) FROM settings.parameters p),
       'history', (SELECT jsonb_agg(to_jsonb(h) ORDER BY h.parameter_key, h.revision) FROM settings.parameter_history h))::text;
   '@
   $sourceSnapshot = psql --host localhost --port 5432 --username ouroboros --dbname ouroboros --tuples-only --no-align --set ON_ERROR_STOP=1 --command $parameterSnapshotSql
   if ($LASTEXITCODE -ne 0) { throw 'Falha ao ler origem; não migrar.' }
   $restoreSnapshot = psql --host localhost --port 5432 --username ouroboros --dbname $restoreDatabase --tuples-only --no-align --set ON_ERROR_STOP=1 --command $parameterSnapshotSql
   if ($LASTEXITCODE -ne 0) { throw 'Falha ao ler restauração; não migrar.' }
   if (($sourceSnapshot -join "`n") -cne ($restoreSnapshot -join "`n")) { throw 'Backup divergente; não migrar.' }
   ```

   Confira também os demais dados relevantes do banco restaurado antes de considerá-lo recuperável. Conteúdos e snapshots são restritos e não devem ir para logs ou repositório.

4. Aplique a migration de Identity com a connection string atual e a aplicação nova ainda parada:

   ```powershell
   dotnet ef database update --project src/Services/Identity/Ouroboros.Identity --startup-project src/Services/Identity/Ouroboros.Identity.Api --context IdentityDbContext
   if ($LASTEXITCODE -ne 0) { throw 'Migração de Identity falhou; não ativar a versão nova.' }
   ```

   Em êxito, repita o snapshot da etapa 3 para `identity.parameters`/`identity.parameter_history` e compare com `$sourceSnapshot`; confira também que `settings` permanece equivalente e que `OwnIdentityParameters` consta do histórico de Identity. Não aplique migrations de Settings depois do corte.

5. Somente após sucesso e equivalência, ative a versão nova e confira leitura local, login e os fluxos afetados. Preserve backup e banco de restauração para recuperação; não apague origem, schema ou volume como parte do procedimento. A suíte abaixo valida a migração em bancos isolados, sem migrar o banco do usuário.

Se qualquer validação falhar, mantenha a versão anterior e a origem preservada, investigue a causa e repita o ensaio com backup conferido. Não habilite o leitor novo sobre migration incompleta.

### Relação com a transferência do banco

Este upgrade acontece no banco original e precede a [transferência do banco de Identity](../development.md#transferir-o-banco-de-identity): a transferência exige que as migrations de Identity, inclusive `OwnIdentityParameters`, já estejam aplicadas na origem, e leva `identity.parameters`, `identity.parameter_history` e o histórico de migrations para `ouroboros_identity`. A validação da transferência compara valores, revisões e histórico completo entre origem e destino. Depois da troca da connection string, o schema `settings` legado continua no banco original, que permanece intacto e somente leitura; seu destino é decisão da #97. As migrations do banco próprio rodam só com a role de migration, e a de runtime é recusada.

## Rollback

O `Down` da migration recusa reversão automática com `NotSupportedException`, evitando excluir dados após novas escritas. Antes de novas escritas, a versão anterior pode continuar usando o legado preservado, depois de confirmar sua equivalência e a configuração da versão anterior. Não executar esse retorno enquanto a versão nova ainda escreve.

Depois de novas escritas em Identity, voltar somente a imagem perde alterações de parâmetros. Pare escritores, faça novos backups recuperáveis de ambos os schemas e reconcilie valores, descrições, ids públicos, revisões, autores, instantes e todas as linhas de histórico novas com o destino de retorno. Resolva colisões de revisão explicitamente, valide equivalência e somente então reative a versão anterior. O procedimento não automatiza essa reconciliação nem autoriza apagar tabelas, bancos ou volumes. Para o retorno depois da transferência do banco de Identity, o `rollback` e o `reconcile` preservam e aplicam na origem as linhas novas ou alteradas de `identity.parameters` e `identity.parameter_history`, entre as demais; veja [Voltar ao banco original](../development.md#voltar-ao-banco-original).

## Como verificar

Com Docker e o SDK do [guia de desenvolvimento](../development.md#testes), execute os testes de integração. [`IdentityParameterMigrationTests`](../../tests/Ouroboros.IntegrationTests/IdentityParameterMigrationTests.cs) usa PostgreSQL 18 real e reproduz o schema legado `settings` pelo SQL versionado [`settings-legacy-schema.sql`](../../tests/Ouroboros.IntegrationTests/Resources/settings-legacy-schema.sql) para verificar backup `pg_dump` custom restaurado por `pg_restore` antes da cópia, preservação de todos os campos/histórico, origem intacta, sequências utilizáveis, abortamento por histórico inconsistente, ausência de fallback e sementes sem schema legado. [`SystemParametersTests`](../../tests/Ouroboros.IntegrationTests/SystemParametersTests.cs) cobre leitura, histórico, rollback e conflito de revisão; [`ParameterRulesTests`](../../tests/Ouroboros.IntegrationTests/ParameterRulesTests.cs) preserva as validações do domínio. A suíte de arquitetura não admite exceções de dependência (a lista de transição está vazia) e falha em `ProjectReference` para projeto inexistente.

[Voltar à documentação](../README.md)
