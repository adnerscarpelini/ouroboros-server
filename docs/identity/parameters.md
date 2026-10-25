# Parâmetros locais de Identity

Identity possui seus parâmetros e histórico desde a [#81](https://github.com/adnerscarpelini/ouroboros-server/issues/81). As tabelas `identity.parameters` e `identity.parameter_history` usam o mesmo `IdentityDbContext`, banco e connection string do restante de Identity. Esta entrega não troca banco ou credencial; o [provisionamento de bancos e roles](../development.md#provisionar-bancos-e-roles-por-serviço) é da #92 e a transferência do banco, da #82.

## Leitura e escrita

O contrato [`Identity.Contracts.ISystemParameters`](../../src/Services/Identity/Ouroboros.Identity/Contracts/ISystemParameters.cs) recebe chave e cancelamento e devolve valor textual. `SystemParameters` usa a porta interna `IParameterReader`; `ParameterReader` consulta `identity.parameters` sem cache e sem rastreamento. Chave em branco é recusada, ausência gera `ParameterNotFoundException` e não há fallback para Settings ou valor padrão silencioso. Login, cadastro, recuperação e primeiro administrador leem localmente pelo contrato do proprietário.

`ParameterWriter` usa `IParameterPersistence`; `ParameterPersistence` aplica as [regras de tipo, padrão e sensibilidade](../architecture/system-parameters.md), grava parâmetro e histórico num único `SaveChangesAsync` e preserva autor, instante e revisão. Os tipos de domínio e os adaptadores estão no próprio Identity; registros são scoped em `AddIdentityModule`. EF, SQL e tradução de concorrência/unicidade permanecem em Infrastructure. Revisão é token de concorrência; a combinação chave/revisão do histórico é única, e conflito desfaz a operação inteira.

O contexto é o mesmo que poderá gravar a intenção de aviso da #34; esta entrega não implementa endpoints administrativos nem outbox. As [portas introduzidas na #73](../settings/parameter-ports.md) mantêm sua unidade de trabalho, agora no proprietário.

## Migração e preservação

A migration [`20261025142848_OwnIdentityParameters`](../../src/Services/Identity/Ouroboros.Identity/Infrastructure/Persistence/Migrations/20261025142848_OwnIdentityParameters.cs) cria as duas tabelas em `identity`, com PKs, índices únicos e colunas equivalentes. Na existência das duas tabelas legadas, obtém `SHARE` locks sobre elas, valida o histórico da revisão corrente, copia todos os campos e compara as linhas completas nos dois sentidos com `EXCEPT ALL` sobre JSONB. Preserva ids internos, identificadores públicos, valores, descrições, tipos, padrões, sensibilidade, revisões, autores e instantes, inclusive todo o histórico. Reposiciona as sequências acima dos ids copiados.

Cópia e validação pertencem à transação da migration. Origem incompleta, histórico corrente inconsistente ou divergência da cópia abortam a migration, inclusive a criação das tabelas de destino; o histórico de migrations não registra essa aplicação. A origem não é alterada. A versão nova não deve ser ativada nesse caso: seu leitor não acessa Settings como alternativa.

Sem nenhuma das duas tabelas de origem, a instalação limpa cria os oito parâmetros iniciais e seus históricos de revisão 0, usando `uuidv7()` do PostgreSQL 18. Esse caminho não exige executar migrations de Settings. Não use instalação limpa para contornar uma origem existente incompleta ou inválida.

Depois do corte, o schema `settings` fica preservado e sem novas escritas. `AddSettingsModule` conserva somente o contexto legado para migrations/inspeção; não registra leitores ou escritores em runtime. O projeto será retirado pela #96 e o destino dos dados antigos será decidido na #97.

## Upgrade de banco existente

O procedimento é manual e deve ser ensaiado em cópia isolada. Os exemplos usam o Compose local (`localhost:5432`, banco e usuário `ouroboros`); ajuste os nomes ao ambiente. Exigem clientes PostgreSQL 18 (`pg_dump`, `createdb`, `pg_restore`, `psql`) no PATH, autenticação protegida por prompt ou arquivo de senha com acesso restrito e espaço para backup/restauração. Não coloque senha na linha de comando nem no Git.

1. Pare os dois hosts, `create-admin`, comandos de migration concorrentes e qualquer outro escritor. Mantenha a janela sem escrita até validar o corte; o lock da cópia não substitui essa janela. Confira o histórico de migrations legado. Se faltarem migrations antigas de Settings, aplique-as **antes** de migrar Identity, ainda nessa janela:

   ```powershell
   dotnet tool restore
   dotnet ef database update --project src/Modules/Ouroboros.Modules.Settings --startup-project src/Ouroboros.Api --context SettingsDbContext
   if ($LASTEXITCODE -ne 0) { throw 'Falha ao preparar o legado; não prosseguir.' }
   ```

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
   dotnet ef database update --project src/Services/Identity/Ouroboros.Identity --startup-project src/Ouroboros.Api --context IdentityDbContext
   if ($LASTEXITCODE -ne 0) { throw 'Migração de Identity falhou; não ativar a versão nova.' }
   ```

   Em êxito, repita o snapshot da etapa 3 para `identity.parameters`/`identity.parameter_history` e compare com `$sourceSnapshot`; confira também que `settings` permanece equivalente e que `OwnIdentityParameters` consta do histórico de Identity. Não execute migrations de Settings depois do corte.

5. Somente após sucesso e equivalência, ative a versão nova e confira leitura local, login e os fluxos afetados. Preserve backup e banco de restauração para recuperação; não apague origem, schema ou volume como parte do procedimento. A suíte abaixo valida a migração em bancos isolados, sem migrar o banco do usuário.

Se qualquer validação falhar, mantenha a versão anterior e a origem preservada, investigue a causa e repita o ensaio com backup conferido. Não habilite o leitor novo sobre migration incompleta.

## Rollback

O `Down` da migration recusa reversão automática com `NotSupportedException`, evitando excluir dados após novas escritas. Antes de novas escritas, a versão anterior pode continuar usando o legado preservado, depois de confirmar sua equivalência e a configuração da versão anterior. Não executar esse retorno enquanto a versão nova ainda escreve.

Depois de novas escritas em Identity, voltar somente a imagem perde alterações de parâmetros. Pare escritores, faça novos backups recuperáveis de ambos os schemas e reconcilie valores, descrições, ids públicos, revisões, autores, instantes e todas as linhas de histórico novas com o destino de retorno. Resolva colisões de revisão explicitamente, valide equivalência e somente então reative a versão anterior. O procedimento não automatiza essa reconciliação nem autoriza apagar tabelas, bancos ou volumes.

## Como verificar

Com Docker e o SDK do [guia de desenvolvimento](../development.md#testes), execute os testes de integração. [`IdentityParameterMigrationTests`](../../tests/Ouroboros.IntegrationTests/IdentityParameterMigrationTests.cs) usa PostgreSQL 18 real para verificar backup `pg_dump` custom restaurado por `pg_restore` antes da cópia, preservação de todos os campos/histórico, origem intacta, sequências utilizáveis, abortamento por histórico inconsistente, ausência de fallback e sementes sem schema legado. [`SystemParametersTests`](../../tests/Ouroboros.IntegrationTests/SystemParametersTests.cs) cobre leitura, histórico, rollback e conflito de revisão; [`ParameterRulesTests`](../../tests/Ouroboros.IntegrationTests/ParameterRulesTests.cs) preserva as validações do domínio. A suíte de arquitetura verifica a retirada da dependência de Identity para Settings.

[Voltar à documentação](../README.md)
