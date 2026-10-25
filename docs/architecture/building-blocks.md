# Código técnico compartilhado

## Decisão

BuildingBlocks guarda mecanismos técnicos realmente reutilizados, como identificadores, datas e mapeamentos. Não contém regras de negócio, orquestração ou entidades de outros serviços.

O transporte de mensagens fica na biblioteca técnica separada `Ouroboros.Messaging`: envelope opaco, validação de wire contract e fábrica de conexão, sem entidades nem referência a serviços. A decisão de compartilhar somente esse mecanismo, preservando DTOs de conteúdo próprios, está em [RabbitMQ e contrato de mensagem](messaging.md#compartilhamento-sem-referência-entre-serviços). O receptor de Notifications usa essa biblioteca; a origem Identity ainda não a usa para publicação.

## Regras

- Não referencia serviços, APIs ou workers.
- Domain não usa tipos de persistência, mesmo no mesmo assembly técnico.
- Extrair código somente quando houver reutilização concreta.
- Evoluir de forma compatível, permitindo implantação independente.
- Não compartilhar entidades para padronizar HTTP ou mensagens.

## Verificação

Testes inspecionam dependências e usos de tipos ([regras](layer-dependencies.md#verificação)): BuildingBlocks não referencia projeto algum de `src`, e a base de Domain segue a regra de Domain. Mudanças compartilhadas executam testes dos consumidores afetados. Compartilhar fonte não obriga publicar todos os serviços juntos.

[Voltar à arquitetura](README.md)
