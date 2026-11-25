# Ouroboros

Fintech fictícia e educacional, sem dinheiro real, em .NET com PostgreSQL, em migração incremental de monólito modular para microsserviços. É projeto público de estudo e portfólio: o objetivo é praticar SOLID, Clean Architecture e qualidade de código, com decisões que o autor consiga explicar em entrevista. Comunicação em português; identificadores em inglês.

## Arquitetura

- A decisão vigente é [docs/architecture/microservices.md](docs/architecture/microservices.md). Ela prevalece sobre premissas de monólito, comunicação em memória ou banco único. Distinga o estado implementado ([docs/implementation-status.md](docs/implementation-status.md)) do destino aprovado.
- Cada serviço persistente tem banco, credencial e migrations próprios. No ambiente local, todos os bancos ficam numa única instância PostgreSQL em um container, por economia; isso não autoriza acesso a banco alheio.
- Estrutura, fronteiras e convenções: [module-structure.md](docs/architecture/module-structure.md), [layer-dependencies.md](docs/architecture/layer-dependencies.md), [entity-conventions.md](docs/architecture/entity-conventions.md), [persistence.md](docs/architecture/persistence.md) e [messaging.md](docs/architecture/messaging.md). O índice completo está em [docs/architecture/README.md](docs/architecture/README.md).
- Tipos `internal` por padrão; públicos só `DependencyInjection` e `Contracts`. Identificadores expostos fora da fronteira são sempre o `ExternalId` (UUIDv7), nunca o `Id` interno.
- Valores monetários em `decimal`; operações que alteram saldo consideram concorrência e idempotência.
- O [web client](docs/architecture/web-client.md) (`src/Clients/Ouroboros.WebClient`, Blazor com papel de BFF) é consumidor externo, não serviço: fala HTTP somente com gateways e nunca referencia serviços, `BuildingBlocks`, `Messaging` ou `Hosting`, nem acessa banco ou broker. Telas ficam em `Features/<Serviço>`, uma pasta por serviço do backend, sem dependência entre elas.

## Comandos

```bash
dotnet build Ouroboros.slnx                       # zero avisos (TreatWarningsAsErrors)
dotnet test --solution Ouroboros.slnx             # arquitetura + integração (exige Docker)
dotnet format Ouroboros.slnx --verify-no-changes
```

Pré-requisitos, testes específicos e execução local em [docs/development.md](docs/development.md).

## Processo

- Requisitos vivem em GitHub Issues de `adnerscarpelini/ouroboros-server`, acompanhadas no GitHub Project **Ouroboros** (owner `adnerscarpelini`, número `1`). Colunas: Backlog, Pronto para iniciar, Em andamento, Em QA, Concluído.
- Título de issue: `[<Módulo>] <ação no infinitivo>`, com o módulo dono em inglês (lista em [.github/ISSUE_TEMPLATE/feature.yml](.github/ISSUE_TEMPLATE/feature.yml) e serviços em `src/Services`), `[WebClient]` para o web client ou `[Transversal]`. Labels: uma de tipo (`feature`, `bug`, `refactor`, `architecture`, `maintenance`) e `module:<nome>` por módulo afetado. Corpo segue os templates de `.github/ISSUE_TEMPLATE/`.
- GitHub Flow: `main` protegida por ruleset (sem push direto, histórico linear, merge só por squash via PR).
- Commits e títulos de PR em Conventional Commits, em português, com a issue: `feat: abre conta corrente (#12)`.
- `docs/` descreve só comportamento implementado e decisões de arquitetura; não crie specs ou requisitos em Markdown.
- Repositório público: nunca versione segredos.
