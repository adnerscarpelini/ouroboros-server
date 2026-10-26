# Aceitação durável e processamento de Notifications

## Responsabilidade local

A [#87](https://github.com/adnerscarpelini/ouroboros-server/issues/87) completa a aceitação persistente, deduplicação e recuperação de Notifications conforme a [política da #79](../architecture/reliable-delivery.md). O worker usa somente `ConnectionStrings:Notifications`, banco/role próprios, sem fallback para Identity ou consulta aos seus dados. A migration `20261026081751_InitialNotificationsInbox` cria no schema `notifications` as tabelas `inbox`, `supersessions`, `failure_outbox` e `processing_recoveries`. Migrations são explícitas, com role separada; o runtime não as aplica nem lê seu histórico. [Configuração e operação](../development.md#executar-o-worker-de-notifications).

O consumidor vem desabilitado por padrão. Habilitá-lo exige provedor, banco, chaves e as duas credenciais mínimas de broker. O único adaptador de entrega disponível é `PickupDirectory`, restrito a Development; não há SMTP ou serviço externo implementado. Consumer, executor, relay de falhas e manutenção são serviços hospedados separados no mesmo processo.

## Aceitação antes do ACK

`InboxAcceptance` valida o envelope v1, produtor e identidade AMQP e calcula fingerprint HMAC-SHA256 sobre sua representação canônica. A PK é `messageId`; advisory locks por identidade e alias serializam aceitação e substituição na transação local. O ACK só ocorre depois do commit da inbox ou da classificação segura. Falha de banco/commit/classificação não recebe ACK: o canal fecha, o original fica reentregável e a nova conexão espera cinco segundos, com prefetch 1. ACK perdido depois do commit produz uma duplicata, não outro trabalho. O consumidor não espera o provedor para reconhecer a mensagem.

| Mensagem recebida | Efeito persistido |
| --- | --- |
| Identidade nova, contrato válido e ainda elegível | `Accepted`, com envelope protegido; executor assume a entrega |
| Mesma identidade e conteúdo canônico igual | ACK sem recriar trabalho, reiniciar tentativas ou reabrir terminal |
| Mesma identidade com conteúdo diferente | `content-conflict`, sem sobrescrever fingerprint; trabalho ainda ativo termina `Failed`, com descarte. `Delivered` permanece concluído |
| Contrato inválido ou versão desconhecida | Classificação e referência de falha na mesma transação; somente ids, fingerprint e metadados seguros, sem corpo/ciphertext original |
| Prazo vencido | `Expired`, sem payload ou envio |
| Mensagem anterior a uma sucessora já aceita | `Cancelled`, sem payload ou envio |

O alias da inbox é SHA256 da chave HMAC opaca enviada por Identity; Notifications não recebe o segredo de alias ou o e-mail para fazer essa associação. O cursor ordena por `occurredAt`, com UUID como desempate. Uma sucessora válida mais recente **atualiza o cursor e cancela predecessoras ativas mesmo quando já chegou expirada**; não retém seu payload nem envia. Chegada fora de ordem não faz um link antigo voltar a ser o trabalho atual. Um efeito externo já iniciado não pode ser desfeito atomicamente: Identity continua recusando token revogado e não se promete que e-mail obsoleto jamais chegue.

## Executor, tentativas e ambiguidade

`InboxExecutor` reivindica uma mensagem por ciclo, com `SKIP LOCKED`, autocommit e lease de **60 segundos**. Revalida proteção, propriedade e prazo; registra duravelmente o início da tentativa antes de chamar o provedor, com timeout de **10 segundos**. Não mantém transação ou lock de negócio durante essa chamada. Conclusão condicionada à lease impede um executor antigo de concluir a tentativa de outro.

São no máximo **cinco chamadas** por intenção, incluindo a primeira, com intervalos de 30 s, 2 min, 10 min e 30 min, com jitter de até 20%. `Retry-After` pode adiar, sem ultrapassar `expiresAt`; reinício não zera contador. Lease vencida permite recuperação. Uma queda após iniciar a quinta tentativa termina em `retry-exhausted` na manutenção, sem sexta chamada. Recusa permanente ou esgotamento termina `Failed`, elimina o payload e exige **nova intenção de negócio** após tratar a causa; não se reabre o terminal nem se zera a contagem.

Privada ausente/incorreta ou RSA indisponível suspende com `protection-unavailable`, sem gastar chamada ao provedor. Depois de corrigir as chaves/configuração, a retomada SQL explícita só alcança trabalho suspenso válido, não terminal, com conteúdo e tentativas disponíveis; grava o ator `current_user` em `processing_recoveries`. Falha de autenticação AES-GCM ou conteúdo inválido é terminal `invalid-contract`, não uma suspensão que possa trocar o conteúdo. [Scripts de recuperação](../development.md#recuperar-processamento-e-referências-de-falha).

Falha depois de o provedor aceitar e antes de registrar `Delivered` deixa resultado ambíguo: recuperação limitada pode enviar novamente. O pickup usa nome/`Message-ID` derivados de `messageId` e não sobrescreve arquivo existente, mas isso não é garantia geral de exactly-once. O teste com provedor falso demonstra a duplicação possível nessa fronteira; deduplicação da aceitação não elimina a ambiguidade externa.

## Referências sanitizadas de falha

A classificação grava `failure_outbox` na mesma transação, com ids, código permitido e prazos. O relay usa a credencial separada `notifications_failures`, aprovada na #93, para publicar somente `notification.email.failed` v1 em `ouroboros.notification-failures`. Nunca usa DLX ou cópia do envelope, nem corpo, destinatário, token ou ciphertext. Uma referência de falha **não envia e-mail** e não deve ser republicada na fila principal para reabrir trabalho.

`FailureReferenceRelay` reivindica lote de até quatro, com lease de dois minutos e timeout de cinco segundos, publicação persistente, `mandatory` e confirms. A identidade da referência é estável mesmo numa queda após confirm e antes do progresso local. Recalcula o TTL imediatamente antes de publicar: no máximo o tempo restante até `expiresAt` original + 30 dias e o teto de 30 dias da fila. Datas no limite são tratadas sem overflow; a publicação tardia não reinicia a retenção.

Indisponibilidade transitória recebe backoff limitado a cinco minutos, somente até o prazo da referência. Rota/autenticação/permissão/topologia inválida suspende até reparo e retomada SQL auditada. A retomada de referência altera somente o relay, preservando o estado terminal do e-mail; não recupera payload já descartado.

## Proteção, retenção e restauração

O conteúdo permanece no envelope cifrado RSA-OAEP-256+A256GCM até o terminal. Fingerprints usam chave HMAC própria de Notifications, externa e distinta da privada RSA, da assinatura JWT, do segredo de alias de Identity e da chave AES de backup. O manifesto admite até 16 entradas e conserva antigas para comparar tombstones pelo `keyId` original, inclusive após rotação. Não retire uma chave de fingerprint enquanto houver tombstone retido que dependa dela: até 30 dias após o prazo absoluto. Nunca distribua essas chaves para Identity.

`Delivered`, `Failed`, `Expired` e `Cancelled` eliminam envelope, alias de trabalho e lease, mantendo somente fingerprint/`keyId`, ids, prazos, estado, contagem e timestamps mínimos. `InboxMaintenance` roda na subida e a cada 30 segundos, independentemente do executor, com lotes de até 100: apaga conteúdo vencido e remove tombstones depois de `expiresAt + 30 dias`, além de cursores, referências e auditorias pelo respectivo limite. Reentrega expirada não envia mesmo após remover o tombstone.

`Notifications:BackupDirectory` permite purgar backups gerenciados por serviço, antes da tentativa SQL, mesmo com o banco indisponível. Reutiliza somente o helper técnico de retenção em BuildingBlocks e o [wrapper de backup protegido](../development.md#backup-protegido-das-intenções-de-e-mail), sem compartilhar modelos ou bancos com Identity. Use diretórios, credenciais e chaves separados; wrapper AES256-GCM aceita dump custom de até 64 MiB e recusa restauração após 30 dias. Sua auditoria cobre operações pelo wrapper; acesso direto ao filesystem continua sob controles do operador.

Restaure com processamento parado, preserve terminais e reaplique expiração/limpeza antes de habilitar consumo, executor e relay. Não estenda prazos ou reabra trabalho concluído a partir do backup. A recuperação foi exercitada com backup cifrado real; não há promessa de RPO zero.

## Como verificar

[`NotificationInboxTests`](../../tests/Ouroboros.IntegrationTests/NotificationInboxTests.cs) cobre duplicatas concorrentes, conflitos, rotação de fingerprint, ordem de substituição inclusive sucessora expirada, cinco tentativas/reinícios, suspensão/recuperação, expiração/retenção, permissões e backup/restauração reais. A classe parcial `NotificationsWorkerTests`, nos arquivos [worker](../../tests/Ouroboros.IntegrationTests/NotificationsWorkerTests.cs) e [durabilidade](../../tests/Ouroboros.IntegrationTests/NotificationsDurabilityTests.cs), exercita dois processos, ACK perdido, falha/rollback do banco, relay confirmado, TTL real, indisponibilidade e retomada de rota. O [ensaio integrado do lote](../../tests/Ouroboros.IntegrationTests/MicroservicesBatchEndToEndTests.cs) atravessa cadastro/reset → outbox → RabbitMQ → worker separado → `.eml` → conclusão → login RS256, com bancos distintos na mesma instância PostgreSQL.

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class '*NotificationInboxTests'
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class '*NotificationsWorkerTests'
```

Os cenários usam PostgreSQL/RabbitMQ e processos reais, credenciais/chaves efêmeras e provedor local/falso, sem depender de segredos do operador. Não validam SMTP, exporter/observabilidade da #88 nem empacotamento operacional/probes da #98.

[Voltar à documentação](../README.md)
