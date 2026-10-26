# Outbox de e-mail e publicação confirmada

## Comportamento disponível

Na solicitação de cadastro ou redefinição de senha, Identity grava token e intenção protegida de e-mail na mesma transação local. A intenção fica em `identity.email_intents`, criada pela migration `20261026015350_AddEmailIntents`; a migration `20261026025858_AddEmailPublicationLeases` acrescenta progresso de publicação. O caso de uso HTTP não chama broker nem provedor, e o envio local foi retirado de Identity. Origens: [#85](https://github.com/adnerscarpelini/ouroboros-server/issues/85) e [#86](https://github.com/adnerscarpelini/ouroboros-server/issues/86), sobre a [política da #79](../architecture/reliable-delivery.md).

A intenção confirmada é recuperável após queda do processo. `EmailOutboxPublisher`, serviço hospedado isolado dentro de Identity, publica no RabbitMQ quando explicitamente habilitado; vem desabilitado por padrão. O worker com a #87 persiste aceitação/deduplicação antes do ACK e controla tentativas em banco próprio. O provedor local continua restrito a Development, e aceitação não significa entrega única ([processamento](../notifications/durable-processing.md)). `202` não comprova persistência nem entrega e mantém o mesmo corpo contra enumeração.

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

Cada intenção possui `ExternalId` UUIDv7 estável, usado como `messageId` do envelope `notification.email.requested` v1 e nas propriedades AMQP. Guarda tipo/versão, origem do token, instantes, prazo absoluto, estado e progresso (`attempts`, lease, próxima tentativa, código sanitizado e `published_at`). Uma intenção criada começa `Pending`. `expiresAt` é o prazo do token, sem extensão por serialização; instantes do envelope respeitam a precisão de microssegundos persistida pelo PostgreSQL.

O envelope segue os [limites da #93](../architecture/messaging.md#contrato-e-compatibilidade), com ids de correlação/causa e contexto W3C da requisição de origem. `correlationId` deriva do trace id; quando não há Activity W3C, a fábrica gera contexto opaco. O publicador continua esse contexto nos logs e spans de cada publicação e o worker, na aceitação e na entrega, inclusive depois da resposta HTTP. Cada mensagem tem link para o contexto da origem; o ciclo do lote é uma raiz com links das intenções ([correlação](../architecture/trace-correlation.md#propagação-por-mensagens)). Nenhum identificador de trace é identidade da mensagem.

Destinatário, assunto, texto e link/token bruto existem **somente dentro do payload cifrado** na outbox. A tabela de tokens mantém seu hash como antes; persistir o link protegido não significa gravá-lo em claro. Não há coluna de destinatário em `email_intents`.

`ProtectedEmailIntentFactory` usa chave AES256 aleatória por intenção e AES-GCM, com AAD canônico de `EnvelopeCodec.AuthenticationData`. RSA-OAEP-SHA256 envolve a chave AES usando a pública de proteção de Notifications. A origem conhece `public.json`, com `activeKeyId` e entradas `keyId`/`n`/`e`; recusa material privado ou reutilização de uma pública JWT conhecida. A privada permanece exclusivamente em Notifications. Chaves AES, buffer de conteúdo e segredo de alias são zerados após o uso; erros/logs não contêm destinatário, assunto, link, payload cifrado ou caminhos secretos.

A chave opaca de substituição é HMAC-SHA256 de finalidade e e-mail normalizado, com segredo externo próprio de 32 bytes. Ela permite relacionar solicitações sem deixar o endereço como alias adivinhável. Não registre esse segredo nem o compartilhe com Notifications. Mantenha-o estável durante o ciclo das mensagens; rotacionar RSA de proteção não altera o alias. Uma troca desse segredo precisa ser planejada separadamente, sem quebrar a associação de mensagens ainda válidas; não há migração automática de aliases.

## Chaves e rotação

Identity exige caminhos externos absolutos em `Identity:EmailIntents:PublicKeyRingPath` e `SupersessionKeyPath`; a antiga `Identity:Email` é recusada. Na subida, uma prova de proteção local valida a configuração sem persistência, token de negócio ou chamada externa. A fábrica relê pública ativa/segredo por operação; chave privada de Notifications nunca é montada em Identity. [Geração e configuração](../development.md#configurar-a-outbox-de-identity).

Para rotacionar RSA, gere outro par de Notifications com `keyId` novo, instale a privada no receptor e distribua o manifesto público atualizado à origem antes de ativar a nova entrada. Conserve capacidade de decifrar mensagens e backups ainda necessários pelo prazo aprovado; não remova a privada antiga enquanto houver conteúdo recuperável válido que dependa dela. Com backups de até 30 dias, a retenção da privada deve considerar essa janela e terminar quando não houver conteúdo necessário, sem conservação indefinida. Não reutilize a chave de assinatura JWT nem o segredo HMAC de substituição.

## Publicação e recuperação

O publicador usa o cliente/topologia da #93 e somente `identity_publisher`, sem declarar objetos. Cada ciclo reivindica lote limitado por uma única instrução `UPDATE` com `FOR UPDATE SKIP LOCKED`, em autocommit: incrementa a tentativa e persiste lease UUID por cinco minutos. Não há cursor de maior id, portanto um commit tardio continua elegível. Locks terminam antes de qualquer chamada ao broker; publicadores concorrentes não reivindicam a mesma lease válida.

As publicações são sequenciais, com timeout de cinco segundos, até oito intenções por ciclo conforme configuração. Mantém no máximo uma conexão/canal ativos, reutilizados no lote e descartados ao fim; falha descarta os recursos antes de nova abertura. TLS é obrigatório fora de Development, com validação padrão de certificado. Revalida lease, prazo e cancelamento antes de publicar e condiciona conclusão/retry à identidade da lease; um executor antigo não conclui a reivindicação de outro. Não impede atomicamente um efeito externo já iniciado ao mesmo tempo que um cancelamento.

Publicação é persistente, `mandatory`, com publisher confirms e tracking de `basic.return`/`basic.nack`. Só após confirm positivo, sem retorno, marca `Published`, preenche `published_at` e elimina envelope, chave de proteção, alias e contexto na mesma atualização local. Confirmação do broker não significa aceitação durável de Notifications nem envio de e-mail. Queda entre o confirm e o progresso local pode republicar **o mesmo id**, após recuperação da lease; não há promessa de publicação única ou exactly-once.

| Resultado | Progresso local e retomada |
| --- | --- |
| Indisponibilidade transitória, timeout ou confirm desconhecido | Continua `Pending`, código sanitizado e próxima tentativa persistidos; backoff exponencial com jitter de 20%, teto de cinco minutos, somente até `expiresAt` |
| `basic.nack`, incluindo pressão por capacidade | `Pending`, `broker-rejected`, retry transitório; nunca marca publicada |
| Autenticação/TLS/permissão/topologia conhecida inválida ou retorno por rota fixa ausente | `Pending` com `configuration-suspended`; não há retry automático, mesmo depois de reparar ou reiniciar. Exige retomada SQL explícita |
| Envelope armazenado inválido ou divergente dos metadados da intenção | `Failed` e conteúdo descartado; exige nova intenção de negócio após corrigir a causa |
| Prazo vencido ou substituição | Não publica; limpeza/cancelamento descarta conteúdo e não ressuscita a intenção |

Não descarta uma intenção válida apenas pela contagem de tentativas de publicação. Suspensas continuam pertencendo à unicidade `Pending`, podem ser substituídas normalmente e expiram pelo prazo original. [Configuração e retomada sem ler payload](../development.md#publicar-e-retomar-a-outbox-de-identity).

`Ouroboros.Identity.Outbox` expõe `outbox.backlog`, `outbox.oldest.age` (segundos), `outbox.publish.failures`, `outbox.publish.retries` e `outbox.terminal`. Falhas/retries recebem somente `code` de conjunto fechado; estados terminais recebem `state=Cancelled/Expired/Failed`. Não há dados pessoais ou identificadores de mensagem nas dimensões. Gauges só emitem depois de um snapshot e refletem o último ciclo bem-sucedido; backlog inclui pendentes válidas, inclusive suspensas, e terminais são linhas ainda retidas, sem contagem histórica durável. A #88 exporta logs, traces e métricas por OTLP quando o endpoint está configurado ([instrumentos e limites](../architecture/diagnostics-pipeline.md#métricas-do-fluxo)). A indisponibilidade do broker foi testada com publicador habilitado e login funcionando. Não existe endpoint readiness nesta base; sua validação pertence à #98 e não foi antecipada.

## Expiração, descarte e retenção

`EmailIntentMaintenance` roda na inicialização e a cada **30 segundos** quando `MaintenanceEnabled=true`, padrão. Cada operação de banco seleciona lotes de até 100 registros:

- Ao vencer uma intenção ainda com envelope, marca `Expired` e elimina envelope, `ProtectionKeyId`, alias e contexto W3C. A limpeza não depende de publicador ou de worker de envio.
- Tombstones terminais sem payload (`Published`, `Cancelled`, `Expired`, `Failed`) são removidos depois de `expiresAt + 30 dias`; linhas pendentes não são purgadas por idade como se publicadas fossem.
- Cancelamento elimina o conteúdo na transação de substituição; publicação confirmada e falha permanente também o eliminam na atualização terminal. Nunca antecipa `Published` antes do confirm. O publicador executa a mesma limpeza limitada em cada ciclo, além da manutenção periódica.

O ciclo purga separadamente os backups cifrados gerenciados configurados em `BackupDirectory`, antes de acessar o banco; indisponibilidade de PostgreSQL não impede essa tentativa de limpeza. Configuração ausente não ativa purga de arquivos. Só aceita diretório marcado pelo wrapper, nomes e prazos reconhecidos, sem seguir diretório/arquivos como links. Não adota ou remove backups/CSVs legados. Falhas são sanitizadas e retomadas no ciclo seguinte. Limpeza é periódica, limitada e recuperável, não uma promessa de remoção instantânea.

## Backups e transferência

`protected-email-backup.ps1` protege um `pg_dump` custom produzido pelo operador, de até 64 MiB, com AES256-GCM, chave externa separada de 32 bytes e `KeyId`. Metadados de versão, chave e prazo são autenticados. O arquivo expira 30 dias após a proteção; o modo Restore recusa arquivo vencido e escreve somente um destino novo. Não acessa o banco nem administra as credenciais dele. [Passos, auditoria e limites](../development.md#backup-protegido-das-intenções-de-e-mail).

Após restaurar, mantenha o processamento parado e reaplique expiração, estados terminais e limpeza antes de reabilitá-lo. Restauração não autoriza reenviar intenção cancelada/expirada. O wrapper audita suas operações sem conteúdo ou caminhos; acesso direto ao filesystem requer controles/auditoria do operador e não é coberto automaticamente.

A transferência de Identity inclui `email_intents` no resumo, linha de base e reconciliação quando a tabela existe, sem impedir uma origem anterior à #85. `rollback` e `reconcile` recusam históricos de migrations divergentes: aplicar #85 somente no destino impede simplesmente voltar à origem pré-#85. Preserve/reconcilie manualmente o schema e as escritas antes de trocar a connection string. Dumps/CSVs da transferência são sensíveis e não entram automaticamente no diretório de backups gerenciado pela manutenção. [Persistência e limites](../architecture/persistence.md#transferência-do-banco-de-identity).

## Como verificar

[`EmailOutboxPublisherTests`](../../tests/Ouroboros.IntegrationTests/EmailOutboxPublisherTests.cs), com PostgreSQL/RabbitMQ reais, cobre confirm e descarte, queda antes do progresso local com id estável, concorrência/leases/fencing, lotes, expiração/retenção, indisponibilidade com login, retorno/nack reais, suspensão e retomada SQL, além das dimensões fechadas das métricas e dos logs da publicação no trace da requisição de origem, com `MessageId` distintos para requisições do mesmo trace. A exportação distribuída com publicação posterior e worker separado é verificada em [`MicroservicesBatchEndToEndTests`](../../tests/Ouroboros.IntegrationTests/MicroservicesBatchEndToEndTests.cs).

[`EmailIntentTests`](../../tests/Ouroboros.IntegrationTests/EmailIntentTests.cs) exercita PostgreSQL real: commit/rollback, interrupção após commit, substituição e concorrência, ausência de envio e contexto W3C. [`EmailIntentProtectionTests`](../../tests/Ouroboros.IntegrationTests/EmailIntentProtectionTests.cs) cobre rotação com privadas retidas, alias estável, adulteração e recusa de chave JWT/material privado na origem. [`EmailIntentMaintenanceTests`](../../tests/Ouroboros.IntegrationTests/EmailIntentMaintenanceTests.cs) verifica a limpeza de backups gerenciados quando a manutenção SQL falha. [`IdentityDatabaseTransferSchemaTests`](../../tests/Ouroboros.IntegrationTests/IdentityDatabaseTransferSchemaTests.cs) cobre a recusa de retorno com migrations divergentes. Testes não dependem de chaves ou bancos locais.

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class '*EmailOutboxPublisherTests'
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class '*EmailIntentTests'
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class '*EmailIntentProtectionTests'
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class '*EmailIntentMaintenanceTests'
```

[Voltar à documentação](../README.md)
