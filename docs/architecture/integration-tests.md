# Testes de integração dos serviços

## Decisão

Cada proprietário é testado contra PostgreSQL real com Testcontainers, banco isolado e dados fictícios. Não depender do Compose ou segredos locais. Compartilhar instância não permite compartilhar dados entre suítes.

## Como executar

Comandos disponíveis no [guia de desenvolvimento](../development.md#testes). A [CI Linux](../development.md#integração-contínua) executa a mesma suíte contra PostgreSQL em container.

## Como escrever um teste

Exercitar API/caso de uso do proprietário e verificar seu banco. Usar migrations e fixtures específicas, com isolamento entre testes. PostgreSQL real comprova constraints e locks; SQLite/InMemory não substituem isso.

Testes locais usam adaptadores controlados; testes de contrato verificam HTTP/mensagens; cenários distribuídos usam processos e broker reais para validar falhas e recuperação.

### API de Identity e listeners

Os testes de integração e de arquitetura sobem a API de Identity (`Ouroboros.Identity.Api`) com `WebApplicationFactory`, localizada pelo tipo `IdentityApiEntryPoint`. O `TestServer` não tem sockets e ignora os listeners do Kestrel; por isso a fábrica de testes substitui só o resolvedor de listener por um que lê o cabeçalho `X-Test-Listener` (`admin` ou ausente), registrado no projeto de testes. O código de produção nunca lê cabeçalho para decidir o público. Quando a separação por portas importa, o teste sobe um Kestrel real: [`KestrelListenerTests`](../../tests/Ouroboros.IntegrationTests/KestrelListenerTests.cs) usa portas livres de loopback e prova que cada porta serve só o seu público e a sua audiência, e que o listener administrativo não responde por endereço da máquina.

Comportamentos que dependem do IP de origem usam um filtro de inicialização só de teste (`X-Test-Client-IP`), como em [`ForwardedHeadersTests`](../../tests/Ouroboros.IntegrationTests/ForwardedHeadersTests.cs).

## Cenários essenciais

- Login, refresh, reuso, logout e reset concorrentes.
- Cadastro único e consumo atômico de token.
- Acesso negado a banco alheio e runtime sem superuser.
- Outbox, rollback, recuperação e duplicatas.
- Administração protegida sem gateway: listener, rota inexistente no outro público, audiência, papel Admin sem política declarada e cabeçalhos encaminhados ([APIs e workers](hosts.md#como-verificar)).
- Atualização de Notifications sem implantar Identity.
- Correlação sem segredos.

Não referenciar implementação de outro serviço para simular contrato. Fixtures, adaptadores de teste e cenários disponíveis estão no [guia de desenvolvimento](../development.md#fixtures-e-adaptadores-de-teste).

[Voltar à arquitetura](README.md)
