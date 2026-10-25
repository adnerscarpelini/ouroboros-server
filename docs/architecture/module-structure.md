# Estrutura dos serviços

## Organização

O Ouroboros usa microsserviços em monorepo. Cada serviço possui executável, configuração, testes e implantação próprios. Todo serviço fica em `src/Services/<Serviço>/`; não há `src/Modules`, pasta de módulos de uma aplicação única. Estrutura de referência:

```text
src/
  Services/
    Identity/
      Ouroboros.Identity.Api/
      Ouroboros.Identity/
    Notifications/
      Ouroboros.Notifications.Worker/
      Ouroboros.Notifications/
  Ouroboros.BuildingBlocks/
tests/
```

A biblioteca própria organiza Domain, Application, Infrastructure e contratos como pastas, com assembly e namespace raiz `Ouroboros.<Serviço>`. Identity segue esse formato em `src/Services/Identity/Ouroboros.Identity`. Não é necessário criar um projeto por camada. Serviços HTTP possuem API; consumidores de mensagens podem ser workers sem API de negócio.

## Projetos existentes

| Projeto | Papel |
| --- | --- |
| `src/Services/Identity/Ouroboros.Identity` | Biblioteca do serviço Identity (Domain, Application, Infrastructure e Contracts como pastas), com os parâmetros de segurança no mesmo `IdentityDbContext` |
| `src/Services/Identity/Ouroboros.Identity.Api` | API de Identity: controllers externos e administrativos, listeners, políticas, rate limiting, configuração e o comando `create-admin` ([APIs e workers](hosts.md)) |
| `src/Services/Notifications/Ouroboros.Notifications` | Biblioteca do receptor de e-mail, portas, caso de uso e adaptadores de proteção/pickup; sem persistência nesta etapa |
| `src/Services/Notifications/Ouroboros.Notifications.Worker` | Generic Host independente, consumidor RabbitMQ e imagem Linux; consumo direto habilitável somente em Development ([worker](../notifications/worker.md)) |
| `src/Ouroboros.Hosting` | Mecanismos técnicos HTTP das APIs, sem papéis, públicos nem regras de negócio; consumidor: `Ouroboros.Identity.Api` |
| `src/Ouroboros.BuildingBlocks` | Código técnico compartilhado ([BuildingBlocks](building-blocks.md)) |
| `src/Ouroboros.Messaging` | Biblioteca técnica neutra de envelope opaco, codec/AAD e fábrica RabbitMQ; sem entidades ou referência a serviços, usada pelo receptor Notifications ([mensageria](messaging.md)) |

Não há executável agregador que componha vários serviços, nem projeto Settings: os parâmetros pertencem ao serviço que aplica a regra, e Identity é a proprietária dos seus. Accounts, Customers e Transactions não existem como projetos.

## Criação de novos serviços

Accounts, Customers e Transactions só serão criados como serviços, em `src/Services/<Serviço>`, quando houver funcionalidade e fronteira financeira aprovadas, cada um com API ou worker, banco, credencial, connection string e migrations próprios. A fronteira financeira não se infere de nomes de pasta: contas, saldo e lançamentos que exigem atomicidade pertencem à mesma fronteira, definida na análise de negócio. Não criar serviços ou projetos vazios para reproduzir pastas. Todo projeto novo de `src` precisa constar em `Ouroboros.slnx` e é analisado pelas regras de [dependências](layer-dependencies.md#descoberta) sem editar os testes.

## Visibilidade dos tipos

Entidades e implementações são internas por padrão. A API usa contratos públicos mínimos e um ponto de composição. Migrations geradas podem ser públicas para descoberta do EF. InternalsVisibleTo de testes não permite compartilhar internals entre serviços.

Contratos expõem ExternalId e dados necessários, nunca entidades, DbContext, IQueryable ou transações. Interfaces locais de DI não constituem integração entre processos.

## Fronteiras adotadas para a evolução

Cada serviço possui seu banco, migrations e regras. Integrações usam HTTP ou mensagens versionadas, sem referência de implementação ou acesso a banco alheio. Saldo e lançamentos que exigem atomicidade permanecem na mesma fronteira financeira.

Reorganizar caminhos, namespaces, referências, solution, fixtures, scripts e documentação. Renomear a pasta não realiza a separação por si só.

## Pacotes NuGet

Versões centralizadas em Directory.Packages.props. Bibliotecas técnicas devem permitir implantação independente. Ver [dependências](layer-dependencies.md).

## Execução e limites

APIs são acessadas diretamente no desenvolvimento, com autorização e restrição administrativa. Gateways são posteriores. Projetos disponíveis: [estado de implementação](../implementation-status.md).

[Voltar à arquitetura](README.md)
