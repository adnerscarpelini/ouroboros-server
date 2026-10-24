<p align="center"><img src="logo.png" alt="Logo do Ouroboros Server" width="240" /></p>

# Ouroboros Server

Fintech fictícia e educacional em C#/.NET e PostgreSQL, cuja arquitetura aprovada é de microsserviços. O projeto desenvolve conhecimentos de domínio bancário, SOLID, Clean Architecture, segurança e operação de sistemas distribuídos, sem movimentar dinheiro real.

## Arquitetura

Cada serviço tem responsabilidade de negócio, processo e implantação próprios. Identity cuida de identidade e segurança; Notifications processa comunicações. Customers e o domínio financeiro evoluem conforme funcionalidades aprovadas. Contas, saldo e lançamentos que exigem atomicidade pertencem à mesma fronteira financeira.

Cada serviço persistente possui banco e credencial próprios. Por decisão consciente de aprendizado, todos os bancos compartilham uma instância PostgreSQL em um único container Docker local para economizar recursos. Disponibilidade, CPU, memória e armazenamento são compartilhados.

A organização definida para o monorepo é por serviços em src/Services. Serviços HTTP possuem API própria; consumidores de mensagens podem executar como workers. Gateways são uma etapa posterior e não bloqueiam acesso direto no desenvolvimento.

- [Arquitetura](docs/architecture/README.md).
- [Estrutura dos serviços](docs/architecture/module-structure.md).
- [Estado de implementação](docs/implementation-status.md): disponibilidade e entregas pendentes.
- [Executar o checkout atual](docs/development.md): SDK, banco, testes e comandos.
- [Funcionalidades](docs/README.md).

## Qualidade

Testes de arquitetura protegem dependências e contratos. Integração usa PostgreSQL real para concorrência e atomicidade. Contratos distribuídos consideram idempotência, duplicatas, falhas e compatibilidade.

Entregas no [Project Ouroboros](https://github.com/users/adnerscarpelini/projects/1).
