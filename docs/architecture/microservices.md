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

APIs podem ser acessadas diretamente no desenvolvimento, com autorização e restrição de rede administrativa. Gateways são posteriores, sem bloquear as primeiras entregas. Não conterão DbContexts ou regras de negócio; cada serviço continuará autorizando chamadas.

## Um banco por módulo/serviço, uma instância local

Cada módulo que constitui serviço persistente possui banco, credencial, connection string e migrations próprios. Todos os bancos ficam em uma única instância PostgreSQL, em um único container Docker local, por decisão consciente de economizar recursos no projeto de aprendizado.

Não precisamos de um servidor por serviço nesta etapa. Sabemos que CPU, memória, armazenamento, manutenção e disponibilidade são compartilhados: uma falha da instância pode afetar todos os bancos. Não é isolamento físico ou alta disponibilidade.

O container único é o PostgreSQL; APIs, workers e broker executam separadamente. Topologia futura depende de necessidade, custo e recuperação, sem impor um servidor físico por serviço. Ver [persistência](persistence.md).

## Comunicação e segurança

HTTP para respostas imediatas, com timeout e tratamento de indisponibilidade. RabbitMQ para mensagens versionadas, com outbox na origem, confirmação de publicação, deduplicação persistente e retries limitados no consumidor. Estado e intenção são atômicos localmente; transporte não participa da transação. A #79 detalha contrato e política; #85–#87 implementam o fluxo.

Não prometer exactly-once no provedor de e-mail. Links de uso único exigem proteção, retenção e tratamento de expiração/obsolescência, sem segredos em logs.

Identity emite tokens com assinatura assimétrica e rotação; consumidores validam chave pública, issuer, audience e principal. Logout mantém o contrato de validade do access token até expiração. Revogação de integrador exige validação autoritativa na próxima chamada, com recusa em indisponibilidade.

## Operação e qualidade

CI Linux, PostgreSQL real, contratos e testes de falha acompanham cada serviço. HTTP e mensagens são correlacionados pelo [W3C Trace Context](trace-correlation.md), com logs JSON no console de cada executável; OpenTelemetry exporta essa correlação. Diagnóstico sanitizado usa pipeline independente dos bancos de negócio e não substitui auditoria.

Validar atualização de Notifications sem implantar Identity, recuperação de mensagens, isolamento de bancos e acesso administrativo direto. Kubernetes não é requisito.

Backup, validação e preservação de dados são obrigatórios na transferência de persistência. Não apagar origem ou volumes incidentalmente. Rollback depois de novas escritas precisa preservá-las/reconciliá-las.

## Referências

- [Microsoft: análise de domínio](https://learn.microsoft.com/en-us/azure/architecture/microservices/model/domain-analysis).
- [Microsoft: dados por serviço](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/architect-microservice-container-applications/data-sovereignty-per-microservice).
- [AWS: transactional outbox](https://docs.aws.amazon.com/prescriptive-guidance/latest/cloud-design-patterns/transactional-outbox.html).
- [Decisão #69](https://github.com/adnerscarpelini/ouroboros-server/issues/69).
- [Estado de implementação](../implementation-status.md).

[Voltar à arquitetura](README.md)
