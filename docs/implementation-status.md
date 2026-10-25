# Estado de implementação

As notas de [arquitetura](architecture/README.md) definem a organização adotada. Esta página concentra a disponibilidade dos artefatos, sem repetir avisos nas notas.

| Entrega | Disponibilidade no checkout | Issue |
| --- | --- | --- |
| Login, sessões, cadastro e recuperação | Implementados; controllers ainda em Ouroboros.Api e Ouroboros.Backoffice.Api | #95 migra os comportamentos; #96 remove os executáveis |
| Portas de persistência de sessões | Implementadas em Application de Identity (`ISessionStore`/`ISessionTransaction`), com adaptador EF em Infrastructure e a mesma unidade de trabalho local; contagem atômica, rehash, locks e integração com reset preservados ([detalhes](identity/session-persistence.md)) | [#74](https://github.com/adnerscarpelini/ouroboros-server/issues/74) |
| Porta de persistência do cadastro | Implementada em Application de Identity (`IRegistrationStore`), com adaptador scoped em Infrastructure; substituição de token sob advisory lock e conclusão com consumo/conta atômicos, rollback em conflito e transporte local após commit ([detalhes](identity/registration.md#persistência)) | [#75](https://github.com/adnerscarpelini/ouroboros-server/issues/75) |
| Porta de persistência da redefinição | Implementada em Application de Identity (`IPasswordResetStore`), com adaptador scoped em Infrastructure e transação compartilhada com `ISessionStore`; consumo, senha, desbloqueio e revogação atômicos, ordem de locks preservada e transporte local após commit na solicitação ([detalhes](identity/password-reset.md#persistência)) | [#76](https://github.com/adnerscarpelini/ouroboros-server/issues/76) |
| Porta de emissão de access tokens | Implementada em Application de Identity, com adaptador JWT em Infrastructure; casos de uso recebem apenas a duração do refresh token, sem chave de assinatura; HS256 e contratos preservados | [#71](https://github.com/adnerscarpelini/ouroboros-server/issues/71) |
| Porta de projeção do usuário autenticado | Implementada em Application de Identity, com consulta e projeção EF em Infrastructure; preserva ExternalId, campos de resposta e recusa de usuário inexistente, inativo ou sem o papel do canal nos dois hosts ([detalhes](identity/me.md#caso-de-uso-exposto)) | [#78](https://github.com/adnerscarpelini/ouroboros-server/issues/78) |
| Portas de parâmetros | Implementadas em Application de Settings, com adaptadores locais em Infrastructure, leitura sem cache e escrita de parâmetro/histórico atômica; contrato público preservado ([detalhes](settings/parameter-ports.md)) | [#73](https://github.com/adnerscarpelini/ouroboros-server/issues/73) |
| Serviços em src/Services | Organização definida; projetos ainda em src/Modules | #94, #72 e #96 |
| Banco e parâmetros próprios de Identity | Parâmetros e histórico continuam em Settings, consumidos localmente por Identity; Identity e Settings têm DbContexts, schemas e migrations distintos, mas usam ConnectionStrings:Ouroboros e credencial/banco comuns no Compose; a credencial da aplicação é a administrativa da instância | #81 (parâmetros), #92 (provisionamento), #82 (transferência do banco), #97 (banco original) |
| Administração de parâmetros e integrações | Modelo/histórico e leitura de parâmetros implementados; endpoints administrativos, reautenticação, avisos e clients de integração pendentes | #34, #35, #37–#39 |
| Contrato e política de entrega confiável | Decisão documentada: contrato lógico v1, recuperação, duplicatas, retries, expiração, proteção e retenção; não implementa o transporte ([detalhes](architecture/reliable-delivery.md)) | [#79](https://github.com/adnerscarpelini/ouroboros-server/issues/79) |
| Notifications e entrega confiável | Envio local em Identity, sem outbox; processos e entrega confiável pendentes | #72, #85–#87 |
| Broker e schema executável de mensagem | Inexistentes no checkout; sem RabbitMQ no Compose. Contrato lógico definido pela #79; envelope executável, compatibilidade e topologia pendentes | #93 |
| Assinatura assimétrica | Checkout utiliza HS256 | #84 |
| Observabilidade | Pendente | #66–#68, #88 |
| Imagens, Compose dos serviços e validação independente | Pendentes; o Compose atual só executa o PostgreSQL | #98, #89 |
| Gateways | Etapa posterior, sem bloquear serviços | #83 |
| Customers e domínio financeiro | Sem funcionalidades | Escopo de negócio a definir |

Transferir controllers, políticas, configuração, create-admin e testes antes de remover os hosts. A remoção não depende de gateways. Ouroboros.Hosting será reduzido ao código técnico útil ou removido se não houver consumidores.

## Decisões substituídas

| Premissa substituída pela #69 | Decisão vigente |
| --- | --- |
| Monólito modular com dois hosts agregadores permanentes | Microsserviços em src/Services, API própria por serviço HTTP; #95 migra os comportamentos e testes, e #96 retira os agregadores |
| Schema por módulo como isolamento entre proprietários | Banco, credencial, connection string e migrations por serviço persistente; schemas somente internos |
| Settings central com referência de Contracts por outros módulos | Parâmetros no serviço que aplica a regra; Identity lê seus parâmetros localmente |
| Notifications como biblioteca em DI nos dois hosts | Processo independente, integração por contrato de mensagem, sem consultar Identity |
| HS256 compartilhado entre emissor e consumidores | Assinatura assimétrica e rotação na #84 |
| Gateways como condição para extrair os serviços | Acesso direto protegido, validação #89 e gateways #83 depois |

As premissas substituídas não autorizam novas violações. A #69 altera documentação; não extrai processos ou transfere persistência.

## Inventário de dependências a isolar

Este inventário descreve o código disponível, sem conceder exceções por namespace. Caminhos relativos a `src/Modules/`; cada dependência listada abaixo consta nominalmente na [lista de transição](../tests/Ouroboros.ArchitectureTests/Dependencies/KnownViolations.cs) da #70 até sua remoção. A [matriz normativa](architecture/layer-dependencies.md) define o destino.

| Tipos e localização | Dependência atual a remover | Entrega |
| --- | --- | --- |
| Identity/Application/Administrators: CreateFirstAdmin | IdentityDbContext, EF e SQL advisory lock | #77 |
| Identity/Application: CreateFirstAdmin, AccountLockout, RequestRegistration, RequestPasswordReset | Settings.Contracts.ISystemParameters; CreateFirstAdmin também usa ParameterNotFoundException | #73, #81 |
| Identity/Infrastructure/Email: PickupDirectoryEmailSender, UnconfiguredEmailSender; DependencyInjection | Transporte/configuração de e-mail locais em Identity | #72, #85–#87 |
| Ouroboros.Api, Ouroboros.Backoffice.Api e Ouroboros.Hosting | Composição agregada e proteção HTTP acopladas aos hosts | #95, #96 |

Os testes de arquitetura verificam, para todo projeto de `src`, as dependências de camada (Domain, Contracts e Application), inclusive em corpos de métodos, ciclos entre projetos e serviços, `ProjectReference` (incluindo BuildingBlocks sem referência a projetos de `src`, entre serviços e a executáveis) e visibilidade (Contracts e DependencyInjection como fronteira pública). Também cobrem descoberta, rotas e schemas por DbContext. A lista de transição só diminui, nas issues que nela constam; nova violação ou exceção obsoleta falha a suíte ([regras e limitações](architecture/layer-dependencies.md#verificação)).

Não são verificados isolamento por credencial ou banco, mensageria confiável, implantação independente, regras de API e worker em nível de tipo nem `typeof(...)` em argumentos de atributo; essas garantias pertencem às entregas indicadas.

## Preservação e próximos passos

O [plano de entregas](architecture/migration-plan.md) relaciona dependências e garantias de segurança, locks, atomicidade e recuperação. Cada issue conserva sua fatia: a #79 definiu a [política de entrega](architecture/reliable-delivery.md); #72 cria consumidor/adaptador; #85–#87 completam o fluxo confiável, sobre o broker e o contrato executável da #93. #94 organiza os projetos; #95 migra as APIs; #96 remove os agregadores depois disso; #92 e #82 provisionam e transferem o banco de Identity; #98 entrega imagens e Compose; #97 decide o destino do banco original; #83 aguarda #89.

Não excluir dados como efeito da reorganização. Comandos disponíveis no [guia de desenvolvimento](development.md). Acompanhar entregas no [Project](https://github.com/users/adnerscarpelini/projects/1).
