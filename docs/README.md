# Documentação do Ouroboros

- [Arquitetura de microsserviços](architecture/README.md).
- [Contrato e política de entrega confiável](architecture/reliable-delivery.md): decisão da #79 para Identity e Notifications, com recuperação, duplicatas, expiração e proteção de payloads; implementação distribuída em entregas próprias.
- [Estrutura dos serviços](architecture/module-structure.md).
- [Assinatura RS256, JWKS e rotação](architecture/authentication.md): principal, audiências e transição HS256; [geração e operação das chaves](development.md#chave-de-assinatura-dos-tokens).
- [Estado de implementação](implementation-status.md).
- [Executar o checkout atual](development.md): build, testes, banco local, provisionamento de bancos e roles e [transferência do banco de Identity](development.md#transferir-o-banco-de-identity).
- [Catálogo de erros](errors/README.md).

As funcionalidades ficam em `<service-name>/<feature-name>.md`, na pasta do serviço proprietário:

- Identity
  - [Parâmetros locais e migração de propriedade](identity/parameters.md): leitura e escrita no IdentityDbContext, preservação do histórico, procedimento com backup/restauração antes do corte e relação com a transferência do banco.
  - [Portas de leitura e escrita de parâmetros](identity/parameter-ports.md): contratos internos de Application e adaptadores locais, com leitura sem cache e valor, autor, revisão e histórico confirmados atomicamente.
  - [Persistência do ciclo de sessões por portas](identity/session-persistence.md): orquestração em Application, EF e SQL em Infrastructure, unidade de trabalho completa e locks compartilhados com a redefinição de senha.
  - [Criar o primeiro administrador](identity/first-admin.md): comando `create-admin` da API de Identity, com a senha em variável de ambiente, só enquanto não houver `Admin`.
  - [Fazer login com e-mail e senha](identity/login.md): `POST /api/v1/identity/sessions` (só `Customer`) e `POST /api/v1/admin/identity/sessions` (só `Admin`), com sessão nova, access token JWT e refresh token opaco gravado só como SHA-256; a sessão é aberta numa transação que trava o usuário e recusa o login se a senha mudou desde a verificação.
  - [Bloquear a conta após falhas consecutivas de senha](identity/account-lockout.md): cinco falhas seguidas bloqueiam a conta por 15 minutos (parâmetros do sistema), com o mesmo `401` do login e contagem atômica no banco; a redefinição de senha remove o bloqueio.
  - [Renovar o access token com rotação do refresh token](identity/refresh-session.md): `POST /api/v1/identity/sessions/refresh` (só sessões de `Customer`) e `POST /api/v1/admin/identity/sessions/refresh` (só de `Admin`), com refresh token de uso único revogado por `UPDATE` condicional na mesma transação do novo, mesmo `sid` e papel lido do banco.
  - [Revogar a sessão ao detectar reuso de refresh token](identity/refresh-token-reuse.md): token revogado apresentado na renovação revoga todos os tokens da sessão, sem afetar as outras, com o mesmo `401`, transação própria, lock da sessão (`FOR UPDATE`) e aviso no log sem o token.
  - [Encerrar a sessão atual (logout)](identity/logout.md): `POST /api/v1/identity/sessions/logout` e `POST /api/v1/admin/identity/sessions/logout`, anônimos e sem checagem de canal, revogam o refresh token informado com `204` idempotente, sem afetar as outras sessões, com lock da sessão; o access token emitido vale até expirar.
  - [Encerrar todas as sessões (logout-all)](identity/logout-all.md): `POST /api/v1/identity/sessions/logout-all` (token de `Customer`) e `POST /api/v1/admin/identity/sessions/logout-all` (token de `Admin`), autenticados e sem corpo, revogam os refresh tokens ativos de todas as sessões do usuário do `sub`, inclusive a atual, com `204` idempotente e lock das sessões em ordem de id; os access tokens emitidos valem até expirar.
  - [Consultar o próprio usuário (me)](identity/me.md): `GET /api/v1/identity/me` (token de `Customer`) e `GET /api/v1/admin/identity/me` (token de `Admin`) devolvem só `id` (`ExternalId`), `email` e `role`, lidos do banco a cada chamada, com `401` e `WWW-Authenticate: Bearer` também para usuário inexistente, inativo ou sem o papel do canal; o access token de uma sessão encerrada vale até expirar.
  - [Cadastrar cliente por e-mail](identity/registration.md): `POST /api/v1/identity/registrations` (só listener público, anônima) recebe só o e-mail e responde sempre `202` com a mesma mensagem; para e-mail sem conta, grava um token de uso único (só o SHA-256, em `identity.email_tokens`), revoga os pendentes e envia o link de conclusão depois do commit, com validade e URL nos parâmetros do sistema e regra estrita de e-mail; `POST /api/v1/identity/registrations/complete` (anônima) recebe o token e a senha, valida a senha pela política de senha e, numa transação, consome o token por `UPDATE` condicional e cria o usuário `Customer` ativo, com `204` sem tokens (sem login automático), `400` `invalid-registration-token` igual para todos os motivos ou `400` `password-rejected` com a regra violada, sem consumir o token.
  - [Redefinir a senha por e-mail](identity/password-reset.md): `POST /api/v1/identity/password-resets` (só listener público, anônima) recebe só o e-mail e responde sempre `202` com a mesma mensagem; só para conta ativa com o papel `Customer`, numa transação com lock por conta, revoga os tokens de redefinição pendentes da conta, grava o novo (só o SHA-256, em `identity.email_tokens`, com `user_id`) e envia o link ao e-mail da conta depois do commit, com validade (60 minutos) e URL nos parâmetros do sistema; a senha atual continua valendo até a conclusão. `POST /api/v1/identity/password-resets/complete` (anônima) recebe o token e a senha nova, valida a política e recusa senha igual à atual e, numa única transação com lock do usuário e das sessões, consome o token, troca a senha, remove o bloqueio e revoga todos os refresh tokens do usuário, com `204` sem tokens, `400` `invalid-password-reset-token` igual para todos os motivos ou `400` `password-rejected` sem consumir o token.
  - [Limitar requisições de autenticação por IP](identity/auth-rate-limiting.md): rate limiter nativo com janela fixa por IP e política por endpoint (login: 10 por minuto; renovação e logout: 30 por minuto; solicitação de cadastro: 5 por minuto; conclusão do cadastro: 10 por minuto; solicitação de redefinição de senha: 5 por minuto; conclusão da redefinição: 10 por minuto), configurado em `Identity:RateLimiting` e validado na subida, com `429`, `Retry-After` e log.

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
