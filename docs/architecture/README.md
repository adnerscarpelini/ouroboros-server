# Arquitetura

Notas gerais e decisões do Ouroboros, distinguindo a estrutura implementada das regras para sua evolução.

- [Estrutura dos módulos](module-structure.md): projetos da solution, pastas padrão, injeção de dependência e fronteiras adotadas.
- [Hosts da API externa e do backoffice](hosts.md): `Ouroboros.Api` e `Ouroboros.Backoffice.Api` em processos e portas distintos, controllers declarados em cada host, configuração comum em `Ouroboros.Hosting`, ordem do pipeline (com o rate limiter antes da autenticação) e camadas de proteção do backoffice; substitui a API única.
- [Controllers HTTP nos hosts](http-controllers.md): controllers `public` nos hosts por módulo, casos de uso expostos em `Contracts`, como criar um endpoint e testes de arquitetura da visibilidade, da separação e das rotas; substitui os controllers nos módulos (#3, #32).
- [Versionamento da API HTTP](api-versioning.md): versão maior na URL com Asp.Versioning, declaração e descontinuação de versões.
- [Respostas de erro com ProblemDetails](error-responses.md): `application/problem+json` em todo erro, `traceId` ligado aos logs, `type` estável para falhas de negócio, `429` com `Retry-After` no limite de requisições e nenhum detalhe interno em erros inesperados.
- [Persistência com PostgreSQL e EF Core](persistence.md): um banco com um schema por módulo, DbContext e histórico de migrations por módulo, connection string validada na subida, migrations por comando e banco de desenvolvimento em Docker Compose.
- [Identificadores, datas e nomes das entidades](entity-conventions.md): `Id` `long` interno e `ExternalId` UUIDv7 público, `CreatedAt`/`UpdatedAt` preenchidos por interceptor, nomes em snake_case e como criar uma entidade.
- [Código compartilhado entre módulos (BuildingBlocks)](building-blocks.md): extração da base `Entity`, do `EntityConfiguration<TEntity>`, do `AuditTimestampsInterceptor` e do `SnakeCaseNaming` do Identity para `Ouroboros.BuildingBlocks`, que não referencia módulos nem hosts.
- [Testes de integração com PostgreSQL](integration-tests.md): PostgreSQL em container com Testcontainers, migrations aplicadas uma vez num banco modelo, um banco clonado por teste e como escrever um teste.
- [Domínio de identidade próprio e hash de senha](identity.md): `User` próprio em vez do ASP.NET Core Identity completo, `PasswordHasher<T>` V3 com 210.000 iterações atrás de uma porta do módulo, rehash na verificação, tabela `identity.users` e ordem dos locks do módulo (usuário, depois sessões).
- [Autenticação JWT e autorização por políticas](authentication.md): access token HS256 emitido e validado pelo Identity, audiência por host, chave fora do Git validada na subida, política padrão que nega por padrão (`Admin` no backoffice) e políticas por capacidade.
- [Envio de e-mail por porta com adaptador local](email.md): porta `IEmailSender` no Identity, `.eml` gravados em `.local/emails` só em `Development`, falha explícita sem provedor e envio depois do commit, sem outbox.
- [Política de senha](password-policy.md): regras do NIST SP 800-63B-4 em `PasswordPolicy` (15 a 128 code points após NFKC, sem composição, lista local de senhas comuns, sem `ouroboros` nem a parte local do e-mail), reutilizadas por todos os fluxos com senha nova, e NFKC no hasher.
- [Parâmetros do sistema com histórico de alterações](system-parameters.md): módulo Settings (schema `settings`), contrato `ISystemParameters` lido a cada uso, tabela `parameters` validada por tipo e padrão, `parameter_history` gravada na mesma transação e valores iniciais por migration.

O limite de requisições por IP da autenticação, com o rate limiter registrado pelo Identity, o `UseRateLimiter()` no pipeline comum e a decisão de manter os limites em configuração, e não nos parâmetros do sistema, está documentado no módulo: [Limitar requisições de autenticação por IP](../identity/auth-rate-limiting.md).

[Voltar à documentação](../README.md)
