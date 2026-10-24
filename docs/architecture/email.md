# Entrega de e-mail por Notifications

## Responsabilidades

Identity determina destinatário, conteúdo, token, validade e regras. Notifications transporta sem consultar usuários ou bancos alheios.

## Entrega confiável

A origem grava intenção na outbox junto do estado de negócio. RabbitMQ recebe mensagens com identidade estável e confirmação. Notifications persiste processamento em banco próprio, reconhece duplicatas e controla concorrência, retries limitados e fila de falhas.

Rollback não produz intenção publicável. Commit seguido de falha permite recuperação. Nenhum envio acontece dentro da transação de negócio.

## Configuração e adaptadores

Notifications possui configuração e segredos próprios. Adaptador local gera .eml apenas em Development, em caminho configurável compatível com Linux. Provedor inválido falha explicitamente. Configuração disponível: [desenvolvimento](../development.md).

## Proteção e limites

Links de uso único são segredos. Proteger payload armazenado/em transporte, restringir acesso e definir retenção e descarte. Tokens e conteúdo sensível não aparecem em logs. Expiração e substituição exigem política para mensagens obsoletas.

Aceitação durável não significa entrega única. Falha após envio e antes de registrar conclusão pode duplicar e-mail. Documentar ambiguidade e política do provedor; não prometer exactly-once.

## Testes

Cobrir rollback, falha pós-commit, broker indisponível, duplicatas, concorrência, recuperação, expiração e sanitização. Entregas: #72, #79 e #85–#87.

[Voltar à arquitetura](README.md)
