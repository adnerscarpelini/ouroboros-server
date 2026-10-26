# Entrega de e-mail por Notifications

## Responsabilidades

Identity determina destinatário, conteúdo, token, validade e regras. Notifications transporta sem consultar usuários ou bancos alheios.

## Entrega confiável

A origem grava intenção na outbox junto do estado de negócio. RabbitMQ recebe mensagens com identidade estável e confirmação. Notifications persiste processamento em banco próprio, reconhece duplicatas e controla concorrência, retries limitados e fila de falhas.

Rollback não produz intenção publicável. Commit seguido de falha permite recuperação. Nenhum envio acontece dentro da transação de negócio.

O [contrato e a política de entrega confiável](reliable-delivery.md), definidos na #79, fixam identidade estável, aceitação durável, confirmação de publicação, tentativas limitadas, recuperação e classificação de falhas. A #93 materializa o envelope e a topologia; #72 e #85–#87 implementam os processos e a persistência. A disponibilidade do transporte no checkout permanece no [estado de implementação](../implementation-status.md).

## Configuração e adaptadores

Notifications possui configuração e segredos próprios. Adaptador local gera .eml apenas em Development, em caminho configurável compatível com Linux. Provedor inválido falha explicitamente. Configuração disponível: [desenvolvimento](../development.md).

O [worker](../notifications/worker.md) executa separadamente, com banco e configuração próprios. A [#87](../notifications/durable-processing.md) persiste aceitação/classificação antes do ACK, deduplica por identidade/fingerprint, controla tentativas/leases e publica referências sanitizadas de falha com credencial separada. Falha de banco/commit não recebe ACK; o envio ao provedor ocorre em outro executor. Consumo vem desabilitado e o único provedor local disponível é restrito a Development. Identity grava token/intenção na mesma transação pela #85 e publica com confirm pela #86, sem referência de projeto a Notifications.

## Proteção e limites

Links de uso único são segredos. A [política](reliable-delivery.md#proteção-acesso-e-retenção) exige proteção do payload armazenado/em transporte, acesso mínimo, descarte no estado terminal ou na confirmação de publicação e retenção limitada de metadados sanitizados. Tokens e conteúdo sensível não aparecem em logs. Expiração e substituição seguem a [regra de obsolescência](reliable-delivery.md#expiração-e-obsolescência): cancelar intenção ainda não publicada não garante impedir um envio externo em curso; Identity continua recusando o link revogado.

Aceitação durável não significa entrega única. Falha após envio e antes de registrar conclusão pode duplicar e-mail. Documentar ambiguidade e política do provedor; não prometer exactly-once.

## Testes

Cobrir rollback, falha pós-commit, broker indisponível, duplicatas, concorrência, recuperação, expiração e sanitização. Entregas: #72, #79, #93 (broker e contrato) e #85–#87.

[Voltar à arquitetura](README.md)
