# Código técnico compartilhado

## Decisão

BuildingBlocks guarda mecanismos técnicos realmente reutilizados, como identificadores, datas e mapeamentos. Não contém regras de negócio, orquestração ou entidades de outros serviços.

`Storage/ProtectedEmailBackupRetention` é reutilizado pela manutenção de Identity e Notifications para apagar somente arquivos gerenciados vencidos. O helper não compartilha modelos, contexto, credenciais ou diretório de backup entre os serviços. [Política e operação](../development.md#backup-protegido-das-intenções-de-e-mail).

O transporte de mensagens fica na biblioteca técnica separada `Ouroboros.Messaging`: envelope opaco, validação de wire contract e fábrica de conexão, sem entidades nem referência a serviços. A decisão de compartilhar somente esse mecanismo, preservando DTOs de conteúdo próprios, está em [RabbitMQ e contrato de mensagem](messaging.md#compartilhamento-sem-referência-entre-serviços). O receptor de Notifications e o publicador da outbox de Identity usam essa biblioteca, sem referência entre os serviços. Ela também contém `MessageTracing`, que continua no processamento o contexto W3C do envelope ([correlação](trace-correlation.md#propagação-por-mensagens)).

A configuração de diagnóstico e de correlação fica na biblioteca técnica separada `Ouroboros.Observability`, usada pela API de Identity e pelo worker de Notifications: OpenTelemetry como único provedor do `ILogger`, sanitização antes de qualquer destino, console JSON, exportação OTLP opcional, sinal de falha do pipeline em stderr, propagador W3C, ouvinte das atividades `Ouroboros.*` e identidade do executável (nome e versão). Referencia apenas o framework ASP.NET Core e os pacotes do OpenTelemetry, sem serviços, bancos ou regras de negócio. [Correlação e formato](trace-correlation.md) e [pipeline de diagnósticos](diagnostics-pipeline.md).

## Regras

- Não referencia serviços, APIs ou workers.
- Domain não usa tipos de persistência, mesmo no mesmo assembly técnico.
- Extrair código somente quando houver reutilização concreta.
- Evoluir de forma compatível, permitindo implantação independente.
- Não compartilhar entidades para padronizar HTTP ou mensagens.

## Verificação

Testes inspecionam dependências e usos de tipos ([regras](layer-dependencies.md#verificação)): BuildingBlocks não referencia projeto algum de `src`, e a base de Domain segue a regra de Domain. Mudanças compartilhadas executam testes dos consumidores afetados. Compartilhar fonte não obriga publicar todos os serviços juntos.

[Voltar à arquitetura](README.md)
