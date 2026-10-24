# Código técnico compartilhado

## Decisão

BuildingBlocks guarda mecanismos técnicos realmente reutilizados, como identificadores, datas e mapeamentos. Não contém regras de negócio, orquestração ou entidades de outros serviços.

## Regras

- Não referencia serviços, APIs ou workers.
- Domain não usa tipos de persistência, mesmo no mesmo assembly técnico.
- Extrair código somente quando houver reutilização concreta.
- Evoluir de forma compatível, permitindo implantação independente.
- Não compartilhar entidades para padronizar HTTP ou mensagens.

## Verificação

Testes inspecionam dependências e usos de tipos ([regras](layer-dependencies.md#verificação)): BuildingBlocks não referencia projeto algum de `src`, e a base de Domain segue a regra de Domain. Mudanças compartilhadas executam testes dos consumidores afetados. Compartilhar fonte não obriga publicar todos os serviços juntos.

[Voltar à arquitetura](README.md)
