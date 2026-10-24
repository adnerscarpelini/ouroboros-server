# Estado de implementação

As notas de [arquitetura](architecture/README.md) definem a organização adotada. Esta página concentra a disponibilidade dos artefatos, sem repetir avisos nas notas.

| Entrega | Disponibilidade no checkout | Issue |
| --- | --- | --- |
| Login, sessões, cadastro e recuperação | Implementados; controllers ainda em Ouroboros.Api e Ouroboros.Backoffice.Api | #95 migra os comportamentos; #96 remove os executáveis |
| Serviços em src/Services | Organização definida; projetos ainda em src/Modules | #94, #72 e #96 |
| Banco e parâmetros próprios de Identity | Identity e Settings têm DbContexts, schemas e migrations distintos, mas usam ConnectionStrings:Ouroboros e credencial/banco comuns no Compose; a credencial da aplicação é a administrativa da instância | #73, #81 (parâmetros), #92 (provisionamento), #82 (transferência do banco), #97 (banco original) |
| Administração de parâmetros e integrações | Modelo/histórico e leitura de parâmetros implementados; endpoints administrativos, reautenticação, avisos e clients de integração pendentes | #34, #35, #37–#39 |
| Notifications e entrega confiável | Envio local em Identity, sem outbox | #72, #79, #85–#87 |
| Broker e contrato de mensagem | Inexistentes no checkout; sem RabbitMQ no Compose | #93 |
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

Este inventário descreve o código disponível, sem conceder exceções por namespace. Caminhos relativos a `src/Modules/`; cada tipo deve ter dependências nominais verificadas pela #70 até sua remoção. A [matriz normativa](architecture/layer-dependencies.md) define o destino.

| Tipos e localização | Dependência atual a remover | Entrega |
| --- | --- | --- |
| Identity/Application/Sessions: Login, RefreshSession | AccessTokenIssuer, JwtOptions de Infrastructure.Security | #71 |
| Identity/Application/Sessions: Login, AccountLockout, RefreshSession, Logout, LogoutAll | IdentityDbContext, EF, consultas/escritas e coordenação de persistência | #74 |
| Identity/Application/Registrations: RequestRegistration, CompleteRegistration | IdentityDbContext, EF, SQL advisory lock, PersistenceErrors/erros do provedor | #75 |
| Identity/Application/PasswordResets: RequestPasswordReset, CompletePasswordReset | IdentityDbContext, EF, SQL e coordenação de locks/tokens/sessões | #76 |
| Identity/Application/Administrators: CreateFirstAdmin | IdentityDbContext, EF e SQL advisory lock | #77 |
| Identity/Application/Users: GetCurrentUser | IdentityDbContext e projeção EF | #78 |
| Settings/Application/Parameters: SystemParameters, ParameterWriter | SettingsDbContext e EF | #73 |
| Identity/Application: CreateFirstAdmin, AccountLockout, RequestRegistration, RequestPasswordReset | Settings.Contracts.ISystemParameters; CreateFirstAdmin também usa ParameterNotFoundException | #73, #81 |
| Identity/Infrastructure/Email: PickupDirectoryEmailSender, UnconfiguredEmailSender; DependencyInjection | Transporte/configuração de e-mail locais em Identity | #72, #85–#87 |
| Ouroboros.Api, Ouroboros.Backoffice.Api e Ouroboros.Hosting | Composição agregada e proteção HTTP acopladas aos hosts | #95, #96 |

BuildingBlocks não referencia módulos/hosts; Domain usa sua base técnica. Os testes atuais verificam parte dessas fronteiras, descoberta, rotas e schemas por DbContext. Não comprovam a matriz completa, proibição de dependências em corpos de método, isolamento por credencial/banco, mensageria confiável ou implantação independente; essas verificações pertencem às entregas indicadas.

## Preservação e próximos passos

O [plano de entregas](architecture/migration-plan.md) relaciona dependências e garantias de segurança, locks, atomicidade e recuperação. Cada issue conserva sua fatia: #79 define a política de entrega; #72 cria consumidor/adaptador; #85–#87 completam o fluxo confiável, sobre o broker e o contrato da #93. #94 organiza os projetos; #95 migra as APIs; #96 remove os agregadores depois disso; #92 e #82 provisionam e transferem o banco de Identity; #98 entrega imagens e Compose; #97 decide o destino do banco original; #83 aguarda #89.

Não excluir dados como efeito da reorganização. Comandos disponíveis no [guia de desenvolvimento](development.md). Acompanhar entregas no [Project](https://github.com/users/adnerscarpelini/projects/1).
