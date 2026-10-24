# Arquitetura

O Ouroboros adota microsserviços com propriedade de dados e implantação independente. Estas notas definem as regras do projeto. A disponibilidade dos artefatos está no [estado de implementação](../implementation-status.md).

- [Visão dos microsserviços](microservices.md).
- [Estrutura dos serviços](module-structure.md).
- [Dependências entre camadas e serviços](layer-dependencies.md).
- [Plano de entregas e preservação das garantias](migration-plan.md).
- [APIs e workers](hosts.md).
- [Controllers HTTP](http-controllers.md).
- [Persistência e instância PostgreSQL local](persistence.md).
- [Parâmetros por serviço](system-parameters.md).
- [Código técnico compartilhado](building-blocks.md).
- [Autenticação e autorização](authentication.md).
- [Domínio de identidade](identity.md).
- [Entrega de e-mail por Notifications](email.md).
- [Testes de integração](integration-tests.md).
- [Versionamento HTTP](api-versioning.md).
- [ProblemDetails](error-responses.md).
- [Identificadores, datas e nomes](entity-conventions.md).
- [Política de senha](password-policy.md).
- [Rate limiting de autenticação](../identity/auth-rate-limiting.md).

[Voltar à documentação](../README.md)
