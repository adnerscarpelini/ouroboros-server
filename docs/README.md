# Documentação do Ouroboros

Documentação das funcionalidades e notas gerais do projeto.

## Organização

- [Arquitetura](architecture/README.md): notas gerais e decisões do projeto.
- [Estrutura dos módulos](architecture/module-structure.md): estrutura vazia atual e padrão para novos módulos.
- As funcionalidades serão documentadas em `<module-name>/<feature-name>.md`, conforme os módulos forem implementados e validados.

Os nomes das pastas e arquivos são escritos em inglês; o conteúdo das notas, em português.

## Requisitos

Requisitos, bugs e tarefas técnicas são registrados em [GitHub Issues](https://github.com/adnerscarpelini/ouroboros-server/issues), com os templates de `.github/ISSUE_TEMPLATE/`, e não em arquivos deste diretório. Cada issue traz contexto, comportamento esperado e critérios de aceite; o PR que a implementa a fecha com `Closes #<número>`.

Labels de tipo (`feature`, `bug`, `refactor`, `architecture`, `maintenance`) e de módulo (`module:<nome>`) classificam as issues.

## Fluxo de documentação

Após a implementação e a aprovação do QA, o Tech Writer cria ou atualiza a nota da funcionalidade no módulo correspondente, com link para as issues de origem, e adiciona seu link neste índice. As notas descrevem o comportamento implementado; discussões e critérios de aceite permanecem nas issues e nos PRs.
