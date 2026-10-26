# Arquitetura

O Ouroboros adota microsserviços com propriedade de dados e implantação independente. Estas notas definem as regras do projeto. A disponibilidade dos artefatos está no [estado de implementação](../implementation-status.md).

- [Visão dos microsserviços](microservices.md).
- [Estrutura dos serviços](module-structure.md).
- [Dependências entre camadas e serviços](layer-dependencies.md).
- [Plano de entregas e preservação das garantias](migration-plan.md).
- [APIs e workers, listeners e camadas de proteção](hosts.md).
- [Controllers HTTP](http-controllers.md).
- [Persistência e instância PostgreSQL local](persistence.md): roles por serviço, connection strings de Identity e transferência do banco com rollback e reconciliação.
- [Parâmetros por serviço](system-parameters.md).
- [Código técnico compartilhado](building-blocks.md).
- [Autenticação e autorização](authentication.md).
- [Domínio de identidade](identity.md).
- [Entrega de e-mail por Notifications](email.md).
- [Contrato e política de entrega confiável](reliable-delivery.md).
- [RabbitMQ, topologia, contratos e biblioteca cliente](messaging.md).
- [Testes de integração](integration-tests.md).
- [Versionamento HTTP](api-versioning.md).
- [ProblemDetails e política de captura de falhas](error-responses.md): classificação de respostas, ocorrência técnica sem duplicação e fronteiras HTTP, de comandos e workers.
- [Correlação por W3C Trace Context e logs estruturados](trace-correlation.md): HTTP e envelope das mensagens, spans com links, console JSON com `traceId`/`spanId`/`occurrenceId` e correspondência com o `traceId` do ProblemDetails.
- [Pipeline de diagnósticos sanitizados](diagnostics-pipeline.md): logs, traces e métricas por OpenTelemetry, sanitização, dimensões restritas, coletor com fila persistente limitada, arquivos rotacionados, recuperação, saturação e retenção.
- [Identificadores, datas e nomes](entity-conventions.md).
- [Política de senha](password-policy.md).
- [Rate limiting de autenticação](../identity/auth-rate-limiting.md).

[Voltar à documentação](../README.md)
