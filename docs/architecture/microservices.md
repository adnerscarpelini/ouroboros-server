# Arquitetura de microsserviços

## Serviços e propriedade

A arquitetura aprovada do Ouroboros é de microsserviços em monorepo para estudar sistemas bancários distribuídos. Domain, Application e Infrastructure organizam cada proprietário.

| Serviço | Responsabilidade |
| --- | --- |
| Identity | Usuários, credenciais, sessões, tokens, convites e parâmetros de segurança |
| Notifications | Entrega de comunicações, processamento, retentativas e falhas |
| Customers | Perfil e ciclo de vida cadastral, conforme funcionalidades aprovadas |
| Fronteira financeira | Contas, saldo e lançamentos que precisam de atomicidade; Banking é nome provisório |

Não separar saldo e lançamentos por conveniência de pastas. Ledger e suas regras exigem escopo de negócio próprio. Customers e a fronteira financeira (Accounts, Transactions) só são criados como serviços, em `src/Services`, quando houver funcionalidade e fronteira financeira aprovadas; não há serviços nem projetos vazios, e nenhuma regra financeira se infere de nomes de pasta. Settings não é serviço central obrigatório: parâmetros pertencem ao serviço que aplica a regra.

## Organização e execução

Serviços ficam em src/Services. Cada serviço HTTP possui sua API; Notifications pode ser worker sem API de negócio. Bibliotecas técnicas não compartilham entidades ou regras nem obrigam implantação conjunta. O [plano de entregas](migration-plan.md) define dependências, segurança, locks e critérios de transferência.

APIs podem ser acessadas diretamente no desenvolvimento, com autorização e restrição de rede administrativa no próprio serviço. Os pontos de entrada opcionais de borda são separados por público em `src/Gateways`: [`Ouroboros.ExternalGateway.Api`](../../src/Gateways/Ouroboros.ExternalGateway.Api) encaminha `/api/v1/identity/*` ao listener público de Identity e [`Ouroboros.AdminGateway.Api`](../../src/Gateways/Ouroboros.AdminGateway.Api) encaminha `/api/v1/admin/identity/*` ao listener administrativo, restrito a loopback por padrão ([#83](https://github.com/adnerscarpelini/ouroboros-server/issues/83); [detalhes](hosts.md#gateways)). Os gateways são finos: não contêm `DbContext`, regras de domínio nem orquestração síncrona entre serviços, repassam `X-Forwarded-For`/`X-Forwarded-Proto` a partir da conexão real, respondem `502`/`504` sanitizados dentro do timeout configurado e nunca substituem a autenticação, a validação de audiência ou a autorização no serviço proprietário.

## Um banco por módulo/serviço, uma instância local

Cada módulo que constitui serviço persistente possui banco, credencial, connection string e migrations próprios. Todos os bancos ficam em uma única instância PostgreSQL, em um único container Docker local, por decisão consciente de economizar recursos no projeto de aprendizado.

Não precisamos de um servidor por serviço nesta etapa. Sabemos que CPU, memória, armazenamento, manutenção e disponibilidade são compartilhados: uma falha da instância pode afetar todos os bancos. Não é isolamento físico ou alta disponibilidade.

O container único é o PostgreSQL; APIs, workers e broker executam separadamente. Topologia futura depende de necessidade, custo e recuperação, sem impor um servidor físico por serviço. Ver [persistência](persistence.md).

## Comunicação e segurança

HTTP para respostas imediatas, com timeout e tratamento de indisponibilidade. RabbitMQ para mensagens versionadas, com outbox na origem, confirmação de publicação, deduplicação persistente e retries limitados no consumidor. Estado e intenção são atômicos localmente; transporte não participa da transação. A #79 detalha contrato e política; #85–#87 implementam o fluxo.

Não prometer exactly-once no provedor de e-mail. Links de uso único exigem proteção, retenção e tratamento de expiração/obsolescência, sem segredos em logs.

Identity emite tokens com assinatura assimétrica e rotação; consumidores validam chave pública, issuer, audience e principal. Logout mantém o contrato de validade do access token até expiração. Revogação de integrador exige validação autoritativa na próxima chamada, com recusa em indisponibilidade.

## Operação e qualidade

CI Linux, PostgreSQL real, contratos e testes de falha acompanham cada serviço. HTTP e mensagens são correlacionados pelo [W3C Trace Context](trace-correlation.md), com logs JSON no console de cada executável; OpenTelemetry exporta essa correlação. Diagnóstico sanitizado usa pipeline independente dos bancos de negócio, com coletor e destino dedicado, e não substitui auditoria ([pipeline de diagnósticos](diagnostics-pipeline.md)).

A atualização e reversão (rollback) de Notifications entre versões compatíveis do contrato v1 sem reimplantar Identity, a recuperação de mensagens após parada de Notifications ou do RabbitMQ, a deduplicação e preservação de `MessageId` em reentregas do publicador e do consumidor, o impacto comum e recuperação na parada da instância PostgreSQL compartilhada, o isolamento de bancos e o acesso administrativo direto são verificados por [`ServiceUpdateAndRecoveryTests`](../../tests/Ouroboros.IntegrationTests/ServiceUpdateAndRecoveryTests.cs) ([#89](https://github.com/adnerscarpelini/ouroboros-server/issues/89); [procedimento e comandos](../development.md#validação-de-atualização-independente-rollback-e-recuperação-dos-serviços)). Kubernetes não é requisito.

Backup, validação e preservação de dados são obrigatórios na transferência de persistência. Não apagar origem ou volumes incidentalmente. Rollback depois de novas escritas precisa preservá-las/reconciliá-las.

## Referências

- [Microsoft: análise de domínio](https://learn.microsoft.com/en-us/azure/architecture/microservices/model/domain-analysis).
- [Microsoft: dados por serviço](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/architect-microservice-container-applications/data-sovereignty-per-microservice).
- [AWS: transactional outbox](https://docs.aws.amazon.com/prescriptive-guidance/latest/cloud-design-patterns/transactional-outbox.html).
- [Decisão #69](https://github.com/adnerscarpelini/ouroboros-server/issues/69).
- [Estado de implementação](../implementation-status.md).

[Voltar à arquitetura](README.md)
