# Documentação do Ouroboros

Documentação das funcionalidades e notas gerais do projeto.

## Organização

- [Arquitetura](architecture/README.md): notas gerais e decisões do projeto.
- [Estrutura dos módulos](architecture/module-structure.md): estrutura atual e padrão para novos módulos.
- [Hosts da API externa e do backoffice](architecture/hosts.md): API externa e backoffice em hosts distintos, cada um com os controllers do seu público no próprio projeto.
- [Controllers HTTP nos hosts](architecture/http-controllers.md): controllers declarados nos hosts, casos de uso expostos em `Contracts` dos módulos e como criar um endpoint.
- [Versionamento da API HTTP](architecture/api-versioning.md): versão na URL (`/api/v1/...`) e como declarar ou descontinuar versões.
- [Respostas de erro com ProblemDetails](architecture/error-responses.md): formato único de erro (RFC 9457) com `traceId`, validação, falhas de negócio e erros inesperados.
- [Catálogo de erros](errors/README.md): `type` estável de cada falha de negócio, uma página por módulo.
- [Persistência com PostgreSQL e EF Core](architecture/persistence.md): um banco com schema, DbContext e histórico de migrations por módulo, connection string e migrations por comando.
- [Identificadores, datas e nomes das entidades](architecture/entity-conventions.md): chave interna `long`, `ExternalId` UUIDv7 exposto, datas de auditoria e snake_case.
- [Código compartilhado entre módulos (BuildingBlocks)](architecture/building-blocks.md): base `Entity` e tipos de persistência comuns em `Ouroboros.BuildingBlocks`, sem dependência de módulos nem hosts.
- [Testes de integração com PostgreSQL](architecture/integration-tests.md): PostgreSQL em container, banco modelo migrado e um banco clonado por teste.
- [Domínio de identidade próprio e hash de senha](architecture/identity.md): entidade `User` própria, só o `PasswordHasher<T>` do ASP.NET Core Identity com 210.000 iterações e rehash na verificação.
- [Autenticação JWT e autorização por políticas](architecture/authentication.md): JWT HS256 com audiência por host, chave de assinatura fora do Git e endpoints que exigem token, salvo `[AllowAnonymous]`.
- [Envio de e-mail por porta com adaptador local](architecture/email.md): porta de envio no Identity, `.eml` em `.local/emails` em desenvolvimento e falha explícita sem provedor.
- [Parâmetros do sistema com histórico de alterações](architecture/system-parameters.md): módulo Settings, leitura por `ISystemParameters` sem cache, validação por tipo e padrão e histórico espelho na mesma transação.
- As funcionalidades serão documentadas em `<module-name>/<feature-name>.md`, conforme os módulos forem implementados e validados.

Os nomes das pastas e arquivos são escritos em inglês; o conteúdo das notas, em português.

## Requisitos

Requisitos, bugs e tarefas técnicas são registrados em [GitHub Issues](https://github.com/adnerscarpelini/ouroboros-server/issues), com os templates de `.github/ISSUE_TEMPLATE/`, e não em arquivos deste diretório. Cada issue traz contexto, comportamento esperado e critérios de aceite; o PR que a implementa a fecha com `Closes #<número>`.

Labels de tipo (`feature`, `bug`, `refactor`, `architecture`, `maintenance`) e de módulo (`module:<nome>`) classificam as issues. O andamento é acompanhado no [GitHub Project Ouroboros](https://github.com/users/adnerscarpelini/projects/1).

## Branches e pull requests

O projeto segue o GitHub Flow: `main` é a única branch permanente, sempre compilável, e toda mudança chega a ela por pull request. Não há branch `develop`, pois existe uma única versão em evolução, sem releases paralelas para manter.

1. Cada issue ganha uma branch curta criada a partir da `main`, no formato `<tipo>/<número>-<descrição>`, por exemplo `feature/12-open-account`. Tipos: `feature`, `fix`, `refactor`, `docs` e `chore`.
2. O trabalho na branch inclui implementação, testes, validação de QA e documentação.
3. O pull request para a `main` tem título em [Conventional Commits](https://www.conventionalcommits.org/) e descrição com resumo, decisões de design, resultado do QA, documentação afetada e `Closes #<número>`.
4. O merge é feito por squash: cada pull request vira um único commit na `main`, com o título e a descrição do PR, e a branch é apagada em seguida.

A `main` é protegida por um ruleset do GitHub: push direto, force push e exclusão são bloqueados, o histórico é linear e o merge só ocorre por pull request com squash.

## Fluxo de documentação

Após a implementação e a aprovação do QA, o Tech Writer cria ou atualiza a nota da funcionalidade no módulo correspondente, com link para as issues de origem, e adiciona seu link neste índice. As notas descrevem o comportamento implementado; discussões e critérios de aceite permanecem nas issues e nos PRs.
