# Intenção de e-mail transacional

## Comportamento disponível

Na solicitação de cadastro ou redefinição de senha, Identity grava token e intenção protegida de e-mail na mesma transação local. A intenção fica em `identity.email_intents`, criada pela migration `20261026015350_AddEmailIntents`. A API não chama broker nem provedor de e-mail, e o adaptador local de envio foi retirado de Identity. Origem: [#85](https://github.com/adnerscarpelini/ouroboros-server/issues/85), sobre a [política da #79](../architecture/reliable-delivery.md).

Essa entrega torna a intenção recuperável após commit, inclusive se o processo encerrar. **Ainda não há publicador da outbox**: a #86 fará a passagem ao RabbitMQ. O worker da #72 existe, mas não recebe solicitações HTTP por esse caminho até a publicação; aceitação durável, deduplicação e retries persistidos de Notifications pertencem à #87. `202` não comprova persistência nem entrega e mantém o mesmo corpo contra enumeração.

## Atomicidade e substituição

A aplicação normaliza e valida o e-mail, gera o token/link e prepara o envelope protegido antes de consultar existência/elegibilidade. A proteção é trabalho local, sem rede. Falha de proteção também não se torna um oráculo sobre a existência de conta. Parâmetros continuam sendo lidos primeiro, com a mesma falha genérica por configuração ausente/inválida.

Para e-mail livre no cadastro ou cliente elegível no reset, as portas `IRegistrationStore`/`IPasswordResetStore` fazem uma única unidade de trabalho sobre `IdentityDbContext`:

1. Abrem a transação e obtêm o advisory lock já adotado: por e-mail de cadastro ou por conta na solicitação de reset.
2. Identificam tokens pendentes substituídos e cancelam suas intenções `Pending`, ou as de mesma chave de substituição, na mesma transação. O cancelamento elimina envelope, identificador da chave de proteção, alias de substituição e contexto W3C da intenção anterior.
3. Revogam os tokens anteriores e gravam o token novo e a intenção vinculada pelo id externo do token. A porta recusa prazo divergente entre ambos ou intenção sem envelope protegido.
4. Confirmam tudo junto. Falha/rollback preserva o estado anterior e não deixa token/intenção substitutos publicáveis. Não existe chamada ao transporte nem lock de negócio durante rede.

O índice único filtrado de `email_intents` permite somente uma intenção `Pending` por chave de substituição. A concorrência usa a mesma ordem de locks anterior; não cria transação distribuída. O consumo de token na conclusão de cadastro/reset e suas garantias locais permanecem nos respectivos casos de uso.

Falhas posteriores à validação de entrada, incluindo proteção ou gravação na solicitação, geram somente diagnóstico sanitizado e mantêm `202`; nenhuma entrega é inferida. Cancelamento da requisição continua sendo cancelamento, não um sucesso de entrega. Conta inexistente/inelegível não recebe token ou intenção persistidos. Não há atraso artificial que iguale os tempos de execução de todos esses caminhos.

## Conteúdo persistido e proteção

Cada intenção possui `ExternalId` UUIDv7 estável, usado como `messageId` do envelope `notification.email.requested` v1. Guarda tipo/versão, origem do token, instantes, prazo absoluto, estado e campos de progresso. Tentativas e publicação estão reservadas à #86; nesta entrega, uma intenção criada começa `Pending`. `expiresAt` é o prazo do token, sem extensão por serialização; instantes do envelope respeitam a precisão de microssegundos persistida pelo PostgreSQL.

O envelope segue os [limites da #93](../architecture/messaging.md#contrato-e-compatibilidade), com ids de correlação/causa e contexto W3C da requisição de origem. `correlationId` deriva do trace id; quando não há Activity W3C, a fábrica gera contexto opaco. Isso preserva a informação para a #88 e não implementa observabilidade distribuída por si só.

Destinatário, assunto, texto e link/token bruto existem **somente dentro do payload cifrado** na outbox. A tabela de tokens mantém seu hash como antes; persistir o link protegido não significa gravá-lo em claro. Não há coluna de destinatário em `email_intents`.

`ProtectedEmailIntentFactory` usa chave AES256 aleatória por intenção e AES-GCM, com AAD canônico de `EnvelopeCodec.AuthenticationData`. RSA-OAEP-SHA256 envolve a chave AES usando a pública de proteção de Notifications. A origem conhece `public.json`, com `activeKeyId` e entradas `keyId`/`n`/`e`; recusa material privado ou reutilização de uma pública JWT conhecida. A privada permanece exclusivamente em Notifications. Chaves AES, buffer de conteúdo e segredo de alias são zerados após o uso; erros/logs não contêm destinatário, assunto, link, payload cifrado ou caminhos secretos.

A chave opaca de substituição é HMAC-SHA256 de finalidade e e-mail normalizado, com segredo externo próprio de 32 bytes. Ela permite relacionar solicitações sem deixar o endereço como alias adivinhável. Não registre esse segredo nem o compartilhe com Notifications. Mantenha-o estável durante o ciclo das mensagens; rotacionar RSA de proteção não altera o alias. Uma troca desse segredo precisa ser planejada separadamente, sem quebrar a associação de mensagens ainda válidas; não há migração automática de aliases.

## Chaves e rotação

Identity exige caminhos externos absolutos em `Identity:EmailIntents:PublicKeyRingPath` e `SupersessionKeyPath`; a antiga `Identity:Email` é recusada. Na subida, uma prova de proteção local valida a configuração sem persistência, token de negócio ou chamada externa. A fábrica relê pública ativa/segredo por operação; chave privada de Notifications nunca é montada em Identity. [Geração e configuração](../development.md#configurar-a-outbox-de-identity).

Para rotacionar RSA, gere outro par de Notifications com `keyId` novo, instale a privada no receptor e distribua o manifesto público atualizado à origem antes de ativar a nova entrada. Conserve capacidade de decifrar mensagens e backups ainda necessários pelo prazo aprovado; não remova a privada antiga enquanto houver conteúdo recuperável válido que dependa dela. Com backups de até 30 dias, a retenção da privada deve considerar essa janela e terminar quando não houver conteúdo necessário, sem conservação indefinida. Não reutilize a chave de assinatura JWT nem o segredo HMAC de substituição.

## Expiração, descarte e retenção

`EmailIntentMaintenance` roda na inicialização e a cada **30 segundos** quando `MaintenanceEnabled=true`, padrão. Cada operação de banco seleciona lotes de até 100 registros:

- Ao vencer uma intenção ainda com envelope, marca `Expired` e elimina envelope, `ProtectionKeyId`, alias e contexto W3C. A limpeza não depende de publicador ou de worker de envio.
- Tombstones terminais sem payload (`Published`, `Cancelled`, `Expired`, `Failed`) são removidos depois de `expiresAt + 30 dias`; linhas pendentes não são purgadas por idade como se publicadas fossem.
- Cancelamento já elimina o conteúdo na transação de substituição. O descarte na transação que confirma publicação será implementado na #86; o estado não deve ser antecipado antes do confirm.

O ciclo purga separadamente os backups cifrados gerenciados configurados em `BackupDirectory`, antes de acessar o banco; indisponibilidade de PostgreSQL não impede essa tentativa de limpeza. Configuração ausente não ativa purga de arquivos. Só aceita diretório marcado pelo wrapper, nomes e prazos reconhecidos, sem seguir diretório/arquivos como links. Não adota ou remove backups/CSVs legados. Falhas são sanitizadas e retomadas no ciclo seguinte. Limpeza é periódica, limitada e recuperável, não uma promessa de remoção instantânea.

## Backups e transferência

`protected-email-backup.ps1` protege um `pg_dump` custom produzido pelo operador, de até 64 MiB, com AES256-GCM, chave externa separada de 32 bytes e `KeyId`. Metadados de versão, chave e prazo são autenticados. O arquivo expira 30 dias após a proteção; o modo Restore recusa arquivo vencido e escreve somente um destino novo. Não acessa o banco nem administra as credenciais dele. [Passos, auditoria e limites](../development.md#backup-protegido-das-intenções-de-e-mail).

Após restaurar, mantenha o processamento parado e reaplique expiração, estados terminais e limpeza antes de reabilitá-lo. Restauração não autoriza reenviar intenção cancelada/expirada. O wrapper audita suas operações sem conteúdo ou caminhos; acesso direto ao filesystem requer controles/auditoria do operador e não é coberto automaticamente.

A transferência de Identity inclui `email_intents` no resumo, linha de base e reconciliação quando a tabela existe, sem impedir uma origem anterior à #85. `rollback` e `reconcile` recusam históricos de migrations divergentes: aplicar #85 somente no destino impede simplesmente voltar à origem pré-#85. Preserve/reconcilie manualmente o schema e as escritas antes de trocar a connection string. Dumps/CSVs da transferência são sensíveis e não entram automaticamente no diretório de backups gerenciado pela manutenção. [Persistência e limites](../architecture/persistence.md#transferência-do-banco-de-identity).

## Como verificar

[`EmailIntentTests`](../../tests/Ouroboros.IntegrationTests/EmailIntentTests.cs) exercita PostgreSQL real: commit/rollback, interrupção após commit, substituição e concorrência, ausência de envio e contexto W3C. [`EmailIntentProtectionTests`](../../tests/Ouroboros.IntegrationTests/EmailIntentProtectionTests.cs) cobre rotação com privadas retidas, alias estável, adulteração e recusa de chave JWT/material privado na origem. [`EmailIntentMaintenanceTests`](../../tests/Ouroboros.IntegrationTests/EmailIntentMaintenanceTests.cs) verifica a limpeza de backups gerenciados quando a manutenção SQL falha. [`IdentityDatabaseTransferSchemaTests`](../../tests/Ouroboros.IntegrationTests/IdentityDatabaseTransferSchemaTests.cs) cobre a recusa de retorno com migrations divergentes. Testes não dependem de chaves ou bancos locais.

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class '*EmailIntentTests'
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class '*EmailIntentProtectionTests'
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class '*EmailIntentMaintenanceTests'
```

[Voltar à documentação](../README.md)
