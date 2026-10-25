# Estado de implementação

As notas de [arquitetura](architecture/README.md) definem a organização adotada. Esta página concentra a disponibilidade dos artefatos, sem repetir avisos nas notas.

| Entrega | Disponibilidade no checkout | Issue |
| --- | --- | --- |
| APIs externa e administrativa de Identity | `src/Services/Identity/Ouroboros.Identity.Api`: um processo com dois públicos separados por listener (`Identity:Listeners`), com controllers externos e administrativos, convenção de rota por público, audiências `Audience`/`AdminAudience` com esquemas `Bearer`/`Bearer.Admin`, política padrão (Admin nas rotas administrativas), rate limiting por público, cabeçalhos encaminhados só de proxies configurados (`Identity:ForwardedHeaders`) e o comando `create-admin`, sem mudança de contrato HTTP. Alternativa avaliada (dois executáveis) e risco aceito em [APIs e workers](architecture/hosts.md#alternativa-avaliada-e-risco-aceito). Não depende de gateway. Execução em [desenvolvimento](development.md#executar-a-api-de-identity) | [#95](https://github.com/adnerscarpelini/ouroboros-server/issues/95) |
| Login, sessões, cadastro e recuperação | Implementados na API de Identity, nos dois públicos, que é o único executável HTTP do checkout e o único alvo dos testes de integração e de arquitetura | [#95](https://github.com/adnerscarpelini/ouroboros-server/issues/95), [#96](https://github.com/adnerscarpelini/ouroboros-server/issues/96) |
| Portas de persistência de sessões | Implementadas em Application de Identity (`ISessionStore`/`ISessionTransaction`), com adaptador EF em Infrastructure e a mesma unidade de trabalho local; contagem atômica, rehash, locks e integração com reset preservados ([detalhes](identity/session-persistence.md)) | [#74](https://github.com/adnerscarpelini/ouroboros-server/issues/74) |
| Porta de persistência do cadastro | Implementada em Application de Identity (`IRegistrationStore`), com adaptador scoped em Infrastructure; substituição de token sob advisory lock e conclusão com consumo/conta atômicos, rollback em conflito e transporte local após commit ([detalhes](identity/registration.md#persistência)) | [#75](https://github.com/adnerscarpelini/ouroboros-server/issues/75) |
| Porta de persistência da redefinição | Implementada em Application de Identity (`IPasswordResetStore`), com adaptador scoped em Infrastructure e transação compartilhada com `ISessionStore`; consumo, senha, desbloqueio e revogação atômicos, ordem de locks preservada e transporte local após commit na solicitação ([detalhes](identity/password-reset.md#persistência)) | [#76](https://github.com/adnerscarpelini/ouroboros-server/issues/76) |
| Porta de persistência do primeiro administrador | Implementada em Application de Identity (`IFirstAdminStore`), com adaptador scoped em Infrastructure; advisory lock global, ausência de Admin, unicidade e gravação na mesma transação, com parâmetros locais e hash calculado antes dela ([detalhes](identity/first-admin.md#persistência)) | [#77](https://github.com/adnerscarpelini/ouroboros-server/issues/77) |
| Porta de emissão de access tokens | Implementada em Application de Identity, com adaptador JWT em Infrastructure; casos de uso recebem apenas a duração do refresh token, sem chave de assinatura; HS256 e contratos preservados | [#71](https://github.com/adnerscarpelini/ouroboros-server/issues/71) |
| Porta de projeção do usuário autenticado | Implementada em Application de Identity, com consulta e projeção EF em Infrastructure; preserva ExternalId, campos de resposta e recusa de usuário inexistente, inativo ou sem o papel do canal nos dois públicos ([detalhes](identity/me.md#caso-de-uso-exposto)) | [#78](https://github.com/adnerscarpelini/ouroboros-server/issues/78) |
| Portas e propriedade de parâmetros | Portas da #73 transferidas para Identity pela #81, com `Identity.Contracts.ISystemParameters`, regras e adaptadores locais no mesmo `IdentityDbContext`; leitura sem cache, escrita/histórico atômicos e migração validada com origem preservada ([detalhes e operação](identity/parameters.md)) | [#73](https://github.com/adnerscarpelini/ouroboros-server/issues/73), [#81](https://github.com/adnerscarpelini/ouroboros-server/issues/81) |
| Serviços em src/Services | Identity está em `src/Services/Identity/Ouroboros.Identity` (assembly e namespace `Ouroboros.Identity`; Domain, Application, Infrastructure e Contracts são pastas da biblioteca), sem mudança de comportamento, rotas, `IdentityDbContext`, schema ou ids de migrations. Não há `src/Modules`: o checkout contém `Ouroboros.BuildingBlocks`, `Ouroboros.Hosting` e o serviço Identity (biblioteca e API); nenhum serviço novo foi criado | [#94](https://github.com/adnerscarpelini/ouroboros-server/issues/94) (Identity), [#96](https://github.com/adnerscarpelini/ouroboros-server/issues/96) (retirada de `src/Modules`) e #72 |
| Banco próprio de Identity e schema `settings` retido | Parâmetros/histórico já estão em `identity`; o projeto Settings não existe mais, e o schema `settings` com seus dados fica retido no banco original, sem projeto que o leia ou escreva; só a migration `OwnIdentityParameters` o lê, ao copiar os parâmetros para `identity`. O provisionamento de bancos e roles por serviço (`ouroboros_identity` e `ouroboros_notifications`, com roles de runtime e de migration) existe e roda por comando explícito ([operação](development.md#provisionar-bancos-e-roles-por-serviço), [roles e privilégios](architecture/persistence.md#roles-e-privilégios-por-serviço)). A transferência do banco inteiro de Identity para `ouroboros_identity` existe como procedimento versionado, executado por comando explícito pelo operador: backup restaurado e comparado, janela sem escrita, cópia, validação, `cutover`, `rollback` e `reconcile` ([operação](development.md#transferir-o-banco-de-identity), [decisão e limites](architecture/persistence.md#transferência-do-banco-de-identity)). A API de Identity usa `ConnectionStrings:Identity` (role de runtime) quando o operador a define depois do `cutover`; sem ela, segue em `ConnectionStrings:Ouroboros`, o banco original com a credencial administrativa. Nenhuma troca é automática: nada é transferido ou trocado sem o operador, e as migrations não são aplicadas na subida (no banco próprio, só com a role de migration). O banco original permanece intacto; seu destino e a revogação da credencial antiga estão pendentes | [#92](https://github.com/adnerscarpelini/ouroboros-server/issues/92) (provisionamento), [#82](https://github.com/adnerscarpelini/ouroboros-server/issues/82) (transferência e connection string própria), [#96](https://github.com/adnerscarpelini/ouroboros-server/issues/96) (remoção do projeto Settings, sem excluir dados), #97 (destino do schema `settings`, dos dados antigos e da credencial antiga) |
| Administração de parâmetros e integrações | Modelo/histórico e leitura de parâmetros implementados; endpoints administrativos, reautenticação, avisos e clients de integração pendentes | #34, #35, #37–#39 |
| Contrato e política de entrega confiável | Decisão documentada: contrato lógico v1, recuperação, duplicatas, retries, expiração, proteção e retenção; não implementa o transporte ([detalhes](architecture/reliable-delivery.md)) | [#79](https://github.com/adnerscarpelini/ouroboros-server/issues/79) |
| Notifications e entrega confiável | Envio local em Identity, sem outbox; processos e entrega confiável pendentes | #72, #85–#87 |
| Broker e schema executável de mensagem | Inexistentes no checkout; sem RabbitMQ no Compose. Contrato lógico definido pela #79; envelope executável, compatibilidade e topologia pendentes | #93 |
| Assinatura assimétrica | Checkout utiliza HS256 | #84 |
| Observabilidade | Pendente | #66–#68, #88 |
| Imagens, Compose dos serviços e validação independente | Pendentes; o Compose atual só executa o PostgreSQL e os comandos de provisionamento (profile `provision`) e de transferência do banco de Identity (profile `transfer`) | #98, #89 |
| Gateways | Etapa posterior, sem bloquear serviços | #83 |
| Customers, Accounts, Transactions e domínio financeiro | Sem funcionalidades e sem projetos: o scaffolding vazio foi retirado. Cada um só será criado como serviço, em `src/Services/<Serviço>`, com API ou worker, banco, credencial, connection string e migrations próprios, quando houver funcionalidade e fronteira financeira aprovadas; a fronteira não se infere de nomes de pasta, e não se criam serviços vazios | Escopo de negócio a definir; scaffolding retirado pela [#96](https://github.com/adnerscarpelini/ouroboros-server/issues/96) |

## Retirada dos hosts agregadores e do scaffolding (#96)

Foram removidos `Ouroboros.Api`, `Ouroboros.Backoffice.Api`, o projeto Settings e o scaffolding vazio de Accounts, Customers e Transactions (todo `src/Modules`). `Ouroboros.Hosting` ficou com mecanismos técnicos HTTP (controllers, versionamento por URL, ProblemDetails com `traceId`, política padrão "autenticado" e ordem do pipeline), sem papéis, públicos ou regra de negócio (`HostAudience` e `AdminRole` foram removidos); seu consumidor é `Ouroboros.Identity.Api`. Os dados do schema `settings` não foram excluídos. A remoção não depende de gateways.

Comportamentos dos hosts removidos e onde estão na API de Identity:

| Comportamento | Destino na API de Identity |
| --- | --- |
| Controllers do host externo (Sessions, Me, Registrations, PasswordResets) | `Controllers/External` |
| Controllers do backoffice (Sessions, Me) | `Controllers/Admin` |
| Modelos de sessão (`SessionModels`) | Unificados em `Controllers/SessionModels.cs` |
| Política padrão do backoffice (papel `Admin`) | Convenção de rota `ListenerRouteConvention` |
| Rate limiting (7 políticas) | `Identity:RateLimiting` no `appsettings.json` |
| `Authentication:Jwt` (`Audience` e `AdminAudience`) | `appsettings.json` |
| `Identity:Email` de desenvolvimento | `appsettings.Development.json` |
| `create-admin` | `CommandLine/CreateAdminCommand` |
| Portas de desenvolvimento 5141, 5142, 7135 e 7136 | `Properties/launchSettings.json` e `Identity:Listeners` |
| `UserSecretsId` | Preservado em `Ouroboros.Identity.Api.csproj`; os segredos já gravados continuam valendo |
| Testes dos hosts | `LoginTests`, `MeTests`, `RefreshSessionTests`, `LogoutTests`, `LogoutAllTests`, `RegistrationRequestTests`, `PasswordResetRequestTests`, `HostSeparationTests`, `AuthorizationTests`, `KestrelListenerTests`, `CreateFirstAdminTests`, `RateLimitingTests` e demais, todos sobre a API de Identity |

Os testes de integração reproduzem o schema legado `settings` por SQL versionado, [`settings-legacy-schema.sql`](../tests/Ouroboros.IntegrationTests/Resources/settings-legacy-schema.sql) (gerado do antigo projeto Settings), para provar a migration `OwnIdentityParameters`. A descoberta de projetos pelos testes de arquitetura considera `src/Services/<Serviço>/<projeto>`, e a lista de transição está vazia.

## Decisões substituídas

| Premissa substituída (#69, #95, #96) | Decisão vigente |
| --- | --- |
| Monólito modular com dois hosts agregadores permanentes | Microsserviços em src/Services, API própria por serviço HTTP; a API de Identity (#95) atende os dois públicos, e os agregadores foram retirados pela #96 |
| Separação dos públicos de Identity por hosts distintos, com a ausência estrutural das rotas administrativas no host externo | Um processo com dois listeners; a separação depende de configuração (listener, rota, audiência, política padrão), verificada por testes. Risco aceito e alternativa em [APIs e workers](architecture/hosts.md#alternativa-avaliada-e-risco-aceito) |
| Schema por módulo como isolamento entre proprietários | Banco, credencial, connection string e migrations por serviço persistente; schemas somente internos |
| Uma connection string (`ConnectionStrings:Ouroboros`) e o banco original da instância para Identity | `ConnectionStrings:Identity` (banco próprio, role de runtime) tem precedência; o banco original só vale sem ela. Transferência única por procedimento versionado ([#82](https://github.com/adnerscarpelini/ouroboros-server/issues/82)) |
| Settings central com referência de Contracts por outros módulos | Parâmetros no serviço que aplica a regra; Identity lê seus parâmetros localmente, e o projeto Settings foi retirado pela #96, com o schema `settings` retido até a #97 |
| Módulos Accounts, Customers e Transactions como pastas e registros de DI vazios | Sem scaffolding: cada serviço será criado em `src/Services/<Serviço>` quando houver funcionalidade e fronteira financeira aprovadas, sem inferir regras financeiras por nome de pasta |
| Notifications como biblioteca em DI nos dois hosts | Processo independente, integração por contrato de mensagem, sem consultar Identity |
| HS256 compartilhado entre emissor e consumidores | Assinatura assimétrica e rotação na #84 |
| Gateways como condição para extrair os serviços | Acesso direto protegido, validação #89 e gateways #83 depois |

As premissas substituídas não autorizam novas violações. A #69 altera documentação; não extrai processos ou transfere persistência.

## Inventário de dependências a isolar

Este inventário descreve as entregas de isolamento ainda pendentes, sem conceder exceções por namespace. Os caminhos de Identity são relativos a `src/Services/Identity/Ouroboros.Identity/`. A [lista de transição](../tests/Ouroboros.ArchitectureTests/Dependencies/KnownViolations.cs) da #70 está vazia: não resta exceção de projeto nem de Application. A [matriz normativa](architecture/layer-dependencies.md) define o destino.

| Tipos e localização | Dependência atual a remover | Entrega |
| --- | --- | --- |
| Infrastructure/Email: PickupDirectoryEmailSender, UnconfiguredEmailSender; DependencyInjection | Transporte/configuração de e-mail locais em Identity | #72, #85–#87 |

Os testes de arquitetura verificam, para todo projeto de `src`, as dependências de camada (Domain, Contracts e Application), inclusive em corpos de métodos, ciclos entre projetos e serviços, `ProjectReference` (incluindo BuildingBlocks sem referência a projetos de `src`, entre serviços e a executáveis) e visibilidade (Contracts e DependencyInjection como fronteira pública). Também cobrem descoberta, rotas e schemas por DbContext. Nova violação ou exceção obsoleta na lista de transição, hoje vazia, falha a suíte ([regras e limitações](architecture/layer-dependencies.md#verificação)).

Esses testes não verificam mensageria confiável, implantação independente, regras de API e worker em nível de tipo nem `typeof(...)` em argumentos de atributo; essas garantias pertencem às entregas indicadas. O isolamento entre bancos e a ausência da credencial administrativa em `src` têm testes próprios, descritos em [Persistência](architecture/persistence.md#roles-e-privilégios-por-serviço).

## Preservação e próximos passos

O [plano de entregas](architecture/migration-plan.md) relaciona dependências e garantias de segurança, locks, atomicidade e recuperação. Cada issue conserva sua fatia: a #79 definiu a [política de entrega](architecture/reliable-delivery.md); #72 cria consumidor/adaptador; #85–#87 completam o fluxo confiável, sobre o broker e o contrato executável da #93. #94 moveu Identity para `src/Services`; #95 entregou as APIs de Identity; #96 retirou os agregadores, o projeto Settings e o scaffolding sem uso; #92 provisionou os bancos e a #82 entregou o procedimento de transferência do banco de Identity; #98 entrega imagens e Compose; #97 decide o destino do banco original; #83 aguarda #89.

Não excluir dados como efeito da reorganização. Comandos disponíveis no [guia de desenvolvimento](development.md). Acompanhar entregas no [Project](https://github.com/users/adnerscarpelini/projects/1).
