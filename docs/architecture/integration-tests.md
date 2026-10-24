# Testes de integração dos serviços

## Decisão

Cada proprietário é testado contra PostgreSQL real com Testcontainers, banco isolado e dados fictícios. Não depender do Compose ou segredos locais. Compartilhar instância não permite compartilhar dados entre suítes.

## Como executar

Comandos disponíveis no [guia de desenvolvimento](../development.md). CI Linux: #80. Não declarar comandos antes de existirem.

## Como escrever um teste

Exercitar API/caso de uso do proprietário e verificar seu banco. Usar migrations e fixtures específicas, com isolamento entre testes. PostgreSQL real comprova constraints e locks; SQLite/InMemory não substituem isso.

Testes locais usam adaptadores controlados; testes de contrato verificam HTTP/mensagens; cenários distribuídos usam processos e broker reais para validar falhas e recuperação.

## Cenários essenciais

- Login, refresh, reuso, logout e reset concorrentes.
- Cadastro único e consumo atômico de token.
- Acesso negado a banco alheio e runtime sem superuser.
- Outbox, rollback, recuperação e duplicatas.
- Administração protegida sem gateway.
- Atualização de Notifications sem implantar Identity.
- Correlação sem segredos.

Não referenciar implementação de outro serviço para simular contrato. Adaptar testes dos hosts antes da remoção na #82.

[Voltar à arquitetura](README.md)
