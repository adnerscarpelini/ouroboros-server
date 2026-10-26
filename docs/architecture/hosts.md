# APIs e workers dos serviços

## Responsabilidade dos executáveis

Cada serviço HTTP possui API própria. Identity executa em [`Ouroboros.Identity.Api`](../../src/Services/Identity/Ouroboros.Identity.Api), um processo com dois públicos (clientes e administradores) que delega aos casos de uso locais. Os pontos de entrada opcionais de borda são separados por público em [`src/Gateways`](../../src/Gateways): [`Ouroboros.ExternalGateway.Api`](../../src/Gateways/Ouroboros.ExternalGateway.Api) para `/api/v1/identity/*` e [`Ouroboros.AdminGateway.Api`](../../src/Gateways/Ouroboros.AdminGateway.Api) para `/api/v1/admin/identity/*` ([detalhes](#gateways)). Notifications executa em [`Ouroboros.Notifications.Worker`](../../src/Services/Notifications/Ouroboros.Notifications.Worker), host separado sem API de negócio, com configuração, imagem e listener técnico de saúde (`Notifications:Health:Urls`, padrão `http://127.0.0.1:5143`) próprios. Seu [processamento durável](../notifications/durable-processing.md) usa banco próprio e habilitação explícita; o provedor local é restrito a Development. Saúde operacional usa apenas os endpoints técnicos de liveness e readiness, sem criar API de negócio no worker.

Executáveis compõem apenas seu proprietário, sem carregar outros serviços como bibliotecas ou acessar seus bancos. Disponibilidade dos executáveis no [estado de implementação](../implementation-status.md); comandos e URLs no [guia de desenvolvimento](../development.md#executar-a-api-de-identity).

## API de Identity: um processo, dois públicos

O público externo (app do cliente, anônimos e integrações) e o administrativo (backoffice) compartilham processo, banco e casos de uso. Como as rotas administrativas existem no mesmo executável das externas, a separação não é ausência de código: é configuração, e cada camada de proteção é verificada por teste.

| Público | Rotas | Listener | Audiência do token | Papel aceito no login |
| --- | --- | --- | --- | --- |
| Externo | `api/v{version}/<service>/...` | `Identity:Listeners:Public` | `Authentication:Jwt:Audience` (`ouroboros-api`) | `Customer` |
| Administrativo | `api/v{version}/admin/<service>/...` | `Identity:Listeners:Admin` | `Authentication:Jwt:AdminAudience` (`ouroboros-backoffice`) | `Admin` |

## Camadas de proteção do backoffice

Autorizar no serviço, inclusive acesso direto. Administração exige restrição de rede além do papel Admin. Nenhuma camada substitui as outras:

1. **Listeners e rede.** O listener público atende as rotas externas; o administrativo atende somente `api/v{version}/admin/...` e, por padrão, só escuta em loopback. Expor o administrativo fora de loopback (rede interna) exige `AllowNonLoopback=true`.
2. **Rota por público.** O prefixo da rota decide o público do endpoint. Uma rota do outro público responde como rota inexistente.
3. **Audiências por canal.** Cada listener valida somente a audiência do próprio canal; o token de um canal é recusado no outro com o `401` genérico.
4. **Papel do canal.** O login e a renovação de cada canal aceitam somente o papel do canal (`Customer` no externo, `Admin` no administrativo), com a mesma resposta `401` de credencial inválida ([login](../identity/login.md#regras)).
5. **Política padrão.** Todo endpoint exige autenticação, salvo `[AllowAnonymous]` explícito; todo endpoint administrativo exige também o papel `Admin`, mesmo sem política declarada no controller.
6. **Cabeçalhos encaminhados.** `X-Forwarded-*` só vale de proxies e redes conhecidos e configurados.

Não expor administração publicamente por conveniência. O isolamento é verificado sem gateway.

### Listeners

Cada URL de `Identity:Listeners:Public:Urls` e `Identity:Listeners:Admin:Urls` vira um endpoint do Kestrel. A conexão aceita por um endpoint carrega o público desse listener, gravado em `IConnectionItemsFeature` ([`KestrelListeners`](../../src/Services/Identity/Ouroboros.Identity.Api/Listeners/KestrelListeners.cs)); o público de uma requisição vem sempre dessa conexão, nunca de cabeçalho, `Host`, caminho ou corpo, que o cliente controla.

- Endpoints explícitos substituem `ASPNETCORE_URLS` e `--urls`, que não conseguem expor o listener administrativo; os listeners só mudam pelas chaves `Identity:Listeners`.
- Um endpoint do Kestrel não configurado por este serviço (por exemplo, `Kestrel:Endpoints`) não carrega público e vale como público externo: o administrativo nunca é o padrão.
- URLs `https` usam o certificado do Kestrel (em desenvolvimento, o de desenvolvimento do .NET).
- O servidor de testes (`TestServer`) não tem sockets e ignora essa configuração.

A configuração é validada na subida ([`ListenerOptions`](../../src/Services/Identity/Ouroboros.Identity.Api/Listeners/ListenerOptions.cs)); o processo não sobe, com `OptionsValidationException` que cita a chave, quando:

- `Public:Urls` ou `Admin:Urls` não tem nenhuma URL;
- uma URL não é absoluta `http(s)://host[:porta]` (esquema diferente de http ou https, caminho diferente de `/`); sem porta explícita vale a padrão do esquema;
- o host não é `localhost` nem um endereço IP (nomes de host são recusados);
- o listener administrativo usa um host que não é loopback e `Admin:AllowNonLoopback` não é `true`;
- uma porta do listener administrativo coincide com a de um listener público.

| Chave | Padrão em `appsettings.json` |
| --- | --- |
| `Identity:Listeners:Public:Urls` | `http://localhost:5141` |
| `Identity:Listeners:Admin:Urls` | `http://localhost:5142` |
| `Identity:Listeners:Admin:AllowNonLoopback` | `false` |

`AllowNonLoopback=true` autoriza escutar em um endereço de rede interna (por exemplo, `http://10.0.0.5:5142`); não restringe quem acessa essa rede. Controles de rede apropriados continuam obrigatórios.

### Rota do outro público é rota inexistente

A [`ListenerRouteConvention`](../../src/Services/Identity/Ouroboros.Identity.Api/Routing/ListenerRouteConvention.cs) grava em cada endpoint o público a que pertence ([`ListenerMetadata`](../../src/Services/Identity/Ouroboros.Identity.Api/Routing/ListenerMetadata.cs)):

- rota `api/v{version}/admin/...`: existe só no listener administrativo e recebe a exigência do papel `Admin`; um `[AllowAnonymous]` explícito (por exemplo, no login) continua valendo;
- qualquer outra rota: existe só no listener público.

A convenção resolve os tokens `[controller]` e `[action]` e avalia todas as rotas do controller; se qualquer uma for administrativa, o endpoint é administrativo (na dúvida, nunca público).

O roteador exclui os endpoints do outro público antes de avaliar o método HTTP ([`ListenerEndpointSelectorPolicy`](../../src/Services/Identity/Ouroboros.Identity.Api/Routing/ListenerEndpointSelectorPolicy.cs), política de nó de ordem menor que a do método). Por isso a rota administrativa chamada no listener público (e a externa no administrativo) se comporta como qualquer rota desconhecida, com qualquer método HTTP: sem `405`; `401` sem token; `404` com token válido para o listener. A resposta não revela que a rota existe. Um endpoint sem metadado de público — como os endpoints técnicos de saúde (`/health/live`, `/health/ready` e `/health`) mapeados por [`OuroborosHealthExtensions`](../../src/Ouroboros.Hosting/OuroborosHealthExtensions.cs) — responde nos dois listeners.

### Audiências e esquemas de autenticação

Os dois esquemas JWT ([`IdentityAuthenticationSchemes`](../../src/Services/Identity/Ouroboros.Identity/Contracts/IdentityAuthenticationSchemes.cs)) usam o mesmo emissor e a mesma chave; diferem na audiência aceita. Na API, um esquema de política escolhe o esquema pelo listener que recebeu a requisição. Detalhes em [Autenticação](authentication.md#audiências-por-canal).

### Política padrão

A configuração comum de `Ouroboros.Hosting` define como política de fallback "usuário autenticado", sem conhecer papéis ou públicos. O papel `Admin` dos endpoints administrativos vem da convenção de rota, não do controller. Requisição sem rota correspondente, que não tem metadado de público, só passa pela autenticação do listener: autenticada, recebe `404`; sem token, `401`.

### Rate limiting e cabeçalhos encaminhados

O rate limiting de autenticação ([`Identity:RateLimiting`](../identity/auth-rate-limiting.md)) mantém contadores separados por público: o escopo é o público do listener, então o mesmo IP tem um contador no externo e outro no administrativo.

`X-Forwarded-For` e `X-Forwarded-Proto` só são aplicados quando `Identity:ForwardedHeaders:KnownProxies` (IPs) ou `KnownNetworks` (CIDR) estão configurados, e apenas de origens nessas listas (um salto). Sem configuração, o middleware nem entra no pipeline: os cabeçalhos são ignorados e o rate limit por IP usa o endereço da conexão, de modo que um cabeçalho forjado não troca o contador. Configuração inválida (IP ou CIDR malformado) impede a subida. Isso prepara o uso de proxies sem depender dos [gateways](#gateways).

## Comandos de operação

- `migrate` roda explicitamente no executável de cada serviço persistente — [`MigrateDatabaseCommand`](../../src/Services/Identity/Ouroboros.Identity.Api/CommandLine/MigrateDatabaseCommand.cs) em `Ouroboros.Identity.Api` e o comando `migrate` em [`Ouroboros.Notifications.Worker/Program.cs`](../../src/Services/Notifications/Ouroboros.Notifications.Worker/Program.cs) — e encerra sem subir o servidor HTTP, abrir listeners ou iniciar os workers de consumo/publicação. Exige a connection string do serviço com a respectiva role de migration (`identity_migrator` em `ConnectionStrings__Identity` ou `notifications_migrator` em `ConnectionStrings__Notifications`) e recusa a role de runtime; erros saem com mensagem sanitizada sem vazar a connection string. No Compose local, os jobs `migrate-identity` e `migrate-notifications` (profile `migrate`) executam esse comando antes da subida dos serviços ([desenvolvimento](../development.md#3-aplicar-migrations-explicitamente-por-serviço)).
- `create-admin` pertence a Identity e roda na própria API ([`CreateAdminCommand`](../../src/Services/Identity/Ouroboros.Identity.Api/CommandLine/CreateAdminCommand.cs)), sem subir o servidor HTTP nem abrir listeners. Senha fica fora de argumentos e logs. Preserva unicidade concorrente do primeiro Admin, validação de domínio e códigos de saída. Execução: [desenvolvimento](../development.md#criar-o-primeiro-administrador).

## Configuração comum, saúde e imagens

[`Ouroboros.Hosting`](../../src/Ouroboros.Hosting/OuroborosHostExtensions.cs) conserva apenas mecanismos técnicos HTTP reutilizáveis pelos executáveis dos serviços e pelos gateways finos: controllers, versionamento por URL, ProblemDetails com `traceId`, a política padrão "usuário autenticado", os endpoints técnicos de saúde ([`OuroborosHealthExtensions`](../../src/Ouroboros.Hosting/OuroborosHealthExtensions.cs)), o encaminhamento HTTP de borda ([`Ouroboros.Hosting.Gateway`](../../src/Ouroboros.Hosting/Gateway/OuroborosGatewayHostExtensions.cs)) e a ordem do pipeline (tratamento de exceções, páginas de status, rate limiter, autenticação, autorização, health checks e controllers). Não conhece serviços, banco, papéis nem regras de negócio: cada API registra o próprio serviço e define suas políticas, e o rate limiter e a autenticação são registrados pelo serviço. Não é agregador central.

### Endpoints de liveness e readiness

[`OuroborosHealthExtensions`](../../src/Ouroboros.Hosting/OuroborosHealthExtensions.cs) expõe `GET /health/live`, `GET /health/ready` e `GET /health` com `[AllowAnonymous]` e `DisableRateLimiting()`, escrevendo exclusivamente o payload fixo `{"status":"Healthy"}` (`200 OK`) ou `{"status":"Unhealthy"}` (`503 Service Unavailable`), com `Cache-Control: no-store, no-cache`. Descrições, mensagens de exceção, nomes de checks, usuários e connection strings nunca são serializados na resposta.

- **Liveness (`/health/live`):** `Predicate = _ => false`; confirma que o processo e o servidor HTTP do executável estão respondendo.
- **Readiness de Identity (`/health/ready` e `/health`):** [`IdentityReadinessHealthCheck`](../../src/Services/Identity/Ouroboros.Identity/Infrastructure/Persistence/IdentityReadinessHealthCheck.cs) abre conexão com o banco configurado de Identity e verifica a presença das tabelas migradas (`SELECT 1 FROM identity.parameters LIMIT 1; SELECT 1 FROM identity.email_intents LIMIT 1;`). Não consulta o RabbitMQ: a indisponibilidade do broker não torna Identity não pronto nem interrompe login, renovação, cadastro ou redefinição de senha.
- **Readiness de Notifications (`/health/ready` e `/health`):** [`NotificationsReadinessHealthCheck`](../../src/Services/Notifications/Ouroboros.Notifications/Infrastructure/NotificationsReadinessHealthCheck.cs), exposto no listener técnico `Notifications:Health:Urls` (padrão `http://127.0.0.1:5143`), verifica o banco próprio (`SELECT 1 FROM notifications.inbox LIMIT 1;`), os arquivos de chaves quando o consumidor está habilitado e a conectividade com o RabbitMQ usando `notifications_consumer` (`QueueDeclarePassiveAsync("notifications.email.v1")`). Com o broker parado, Notifications continua vivo em `/health/live` (`200`), mas reporta `503` em `/health/ready`.
- **Readiness dos gateways (`/health/ready` e `/health`):** [`GatewayReadinessHealthCheck`](../../src/Ouroboros.Hosting/Gateway/GatewayReadinessHealthCheck.cs) consulta `GET /health/ready` em `Gateway:Downstream:IdentityBaseUrl` com timeout de 2 segundos e reporta `200 OK` (`{"status":"Healthy"}`) somente quando o `Identity` responde `200 OK`, ou `503 Service Unavailable` (`{"status":"Unhealthy"}`) caso contrário, sem expor URLs internas ou detalhes de falha.

### Imagens Linux e Compose local

[`src/Services/Identity/Ouroboros.Identity.Api/Dockerfile`](../../src/Services/Identity/Ouroboros.Identity.Api/Dockerfile), [`src/Services/Notifications/Ouroboros.Notifications.Worker/Dockerfile`](../../src/Services/Notifications/Ouroboros.Notifications.Worker/Dockerfile), [`src/Gateways/Ouroboros.ExternalGateway.Api/Dockerfile`](../../src/Gateways/Ouroboros.ExternalGateway.Api/Dockerfile) e [`src/Gateways/Ouroboros.AdminGateway.Api/Dockerfile`](../../src/Gateways/Ouroboros.AdminGateway.Api/Dockerfile) constroem imagens baseadas em `mcr.microsoft.com/dotnet/sdk:10.0.402` e `mcr.microsoft.com/dotnet/aspnet:10.0.13-alpine3.24`, recebendo `APP_VERSION` e `SOURCE_REVISION` para compor a versão `<APP_VERSION>-<SOURCE_REVISION>` no assembly e nos rótulos OCI (`org.opencontainers.image.version` e `org.opencontainers.image.revision`). Cada imagem copia apenas o executável correspondente e as bibliotecas técnicas necessárias, exclui segredos, `.env`, `*.pem`, `*.key`, `.local` e `backups` pelo `.dockerignore` próprio e executa como usuário não-root (`USER $APP_UID`, UID `1654`).

No [`compose.yaml`](../../compose.yaml), `identity-api` publica no host apenas o listener público em loopback (`127.0.0.1:${IDENTITY_PUBLIC_PORT:-5141}:5141`), mantendo o listener administrativo restrito ao loopback interno do container (`http://127.0.0.1:5142`, `AllowNonLoopback=false`), sem publicação de porta no host. No profile opcional `gateways`, `external-gateway` publica `127.0.0.1:${EXTERNAL_GATEWAY_PORT:-5151}:5151` e `admin-gateway` publica `127.0.0.1:${ADMIN_GATEWAY_PORT:-5152}:5152` restrito a loopback (`Gateway__AllowNonLoopback=false`). As chaves criptográficas são geradas fora do repositório por [`scripts/messaging/new-compose-keys.ps1`](../../scripts/messaging/new-compose-keys.ps1) e montadas em somente leitura apenas no serviço proprietário correspondente (`/run/ouroboros-keys/identity` e `/run/ouroboros-keys/notifications`).

[`Ouroboros.Observability`](../../src/Ouroboros.Observability/ObservabilityHostExtensions.cs) dá a configuração de diagnóstico e correlação comum às APIs, aos gateways e aos workers: `AddOuroborosObservability` é chamada pela API de Identity (serviço `identity`), pelo worker de Notifications (serviço `notifications`) e pelos gateways (`external-gateway` e `admin-gateway`). Remove os provedores de log padrão, inclusive o Windows Event Log, registra o OpenTelemetry como único provedor do `ILogger`, sanitiza cada entrada antes do console JSON e, quando `Observability:Otlp:Endpoint` está definido, do coletor OTLP, aceita só o contexto W3C (`traceparent`/`tracestate`) e permite continuar esse contexto no encaminhamento HTTP e no processamento de mensagens. Também não conhece serviços, bancos nem regras de negócio. Decisão, formato e limites em [Correlação por W3C Trace Context](trace-correlation.md) e [Pipeline de diagnósticos sanitizados](diagnostics-pipeline.md).

## Gateways

A [#83](https://github.com/adnerscarpelini/ouroboros-server/issues/83) entrega dois gateways HTTP finos em [`src/Gateways`](../../src/Gateways), separados por público e construídos sobre [`Ouroboros.Hosting/Gateway`](../../src/Ouroboros.Hosting/Gateway/OuroborosGatewayHostExtensions.cs) ([`GatewayOptions`](../../src/Ouroboros.Hosting/Gateway/GatewayOptions.cs), [`GatewayForwarder`](../../src/Ouroboros.Hosting/Gateway/GatewayForwarder.cs) e [`GatewayReadinessHealthCheck`](../../src/Ouroboros.Hosting/Gateway/GatewayReadinessHealthCheck.cs)):

| Executável | Público (`GatewayAudience`) | Prefixo encaminhado | Escuta padrão (`Gateway:Urls`) | `Gateway:AllowNonLoopback` | Destino padrão (`Gateway:Downstream:IdentityBaseUrl`) |
| --- | --- | --- | --- | --- | --- |
| [`Ouroboros.ExternalGateway.Api`](../../src/Gateways/Ouroboros.ExternalGateway.Api) | `External` | `/api/v1/identity` e `/api/v1/identity/{**remainder}` | `http://localhost:5151` (`http://0.0.0.0:5151` no container) | `true` | `http://localhost:5141` (`http://identity-api:5141` no Compose) |
| [`Ouroboros.AdminGateway.Api`](../../src/Gateways/Ouroboros.AdminGateway.Api) | `Admin` | `/api/v1/admin/identity` e `/api/v1/admin/identity/{**remainder}` | `http://127.0.0.1:5152` | `false` | `http://127.0.0.1:5142` |

### Fronteira fina sem banco, regras de domínio ou orquestração síncrona

- **Sem acoplamento a serviços ou persistência:** Os projetos `Ouroboros.ExternalGateway.Api` e `Ouroboros.AdminGateway.Api` são executáveis fora de `src/Services`. Não possuem `DbContext`, não referenciam `EntityFrameworkCore`, `Npgsql`, `Ouroboros.BuildingBlocks`, `Ouroboros.Identity` nem `Ouroboros.Notifications`, não declaram `ControllerBase` e não executam regras de domínio nem orquestração/agregação síncrona entre serviços.
- **Isolamento de rotas na borda:** Cada gateway registra apenas o prefixo do próprio público (`UseOuroborosGateway`). Uma rota administrativa (`/api/v1/admin/identity/*`) chamada no `ExternalGateway` ou uma rota externa (`/api/v1/identity/*`) chamada no `AdminGateway` não corresponde a nenhum endpoint mapeado e responde `404 Not Found` na borda, sem encaminhar requisição ao `Identity`.
- **Restrição de rede do gateway administrativo:** Por padrão, `Ouroboros.AdminGateway.Api` define `Gateway:AllowNonLoopback = false` e escuta apenas em `http://127.0.0.1:5152`. Se `Gateway:Urls` contiver qualquer endereço fora de loopback (`localhost`, `127.0.0.1` ou `::1`) enquanto `AllowNonLoopback` for `false`, `GatewayOptionsValidator` impede a subida com `OptionsValidationException`.

### Preservação da autenticação e autorização no proprietário (`Identity`)

- Os endpoints de proxy nos gateways usam `.AllowAnonymous()` e `.DisableRateLimiting()` apenas para não duplicar autenticação na borda e encaminhar o cabeçalho `Authorization` intacto ao destino.
- O serviço `Identity` continua validando assinatura JWT, audiência por listener (`ouroboros-api` no público, `ouroboros-backoffice` no administrativo), papel exigido (`Customer` ou `Admin`) e políticas de autorização em todas as chamadas — tanto quando acessado através dos gateways quanto em chamadas diretas fora deles. Token de audiência externa em rota administrativa continua recusado com `401 Unauthorized`, e token com audiência administrativa mas papel `Customer` continua recusado com `403 Forbidden`.

### Cabeçalhos encaminhados (`X-Forwarded-For` / `X-Forwarded-Proto`) e limite do rate limiter em memória

- **Higienização no gateway:** Ao montar a requisição para o downstream, [`GatewayForwarder`](../../src/Ouroboros.Hosting/Gateway/GatewayForwarder.cs) descarta cabeçalhos hop-by-hop e cabeçalhos falsificáveis enviados pelo cliente (`Forwarded`, `X-Forwarded-For`, `X-Forwarded-Host`, `X-Forwarded-Proto`, `X-Forwarded-Prefix`, `X-Original-For`, `X-Real-IP`, `X-Test-Listener`, `Host`) e grava `X-Forwarded-For` exclusivamente com o `RemoteIpAddress` real da conexão recebida pelo gateway (normalizando IPv4 mapeado em IPv6), além de `X-Forwarded-Proto` com o esquema (`http`/`https`) e `traceparent`/`tracestate` da `Activity.Current` (formato W3C). Se o próprio gateway estiver atrás de um balanceador confiável, `Gateway:ForwardedHeaders:KnownProxies` e `KnownNetworks` habilitam `UseForwardedHeaders` (`ForwardLimit = 1`) no gateway.
- **Confiança explícita no `Identity`:** O `Identity` só aceita `X-Forwarded-For` e `X-Forwarded-Proto` de conexões originadas em IPs ou sub-redes listados em `Identity:ForwardedHeaders:KnownProxies` ou `Identity:ForwardedHeaders:KnownNetworks` (no Compose, `127.0.0.1` e `${IDENTITY_KNOWN_PROXY_NETWORK:-172.16.0.0/12}`). Quando um cliente passa por múltiplas réplicas confiáveis do gateway, o `Identity` identifica o mesmo IP real do cliente e aplica o rate limit por IP normalmente; se um chamador direto não confiável enviar `X-Forwarded-For` forjado ao `Identity`, o cabeçalho é ignorado e o limite incide sobre o IP real da conexão.
- **Limite do rate limiter nativo em memória:** Os contadores de `Identity:RateLimiting` residem em memória no processo de `Identity`. Múltiplas réplicas dos gateways apontando para uma instância de `Identity` compartilham o mesmo contador por IP de cliente e público; contudo, se houver múltiplas réplicas do próprio `Identity`, cada instância manterá contadores independentes em memória até que um store distribuído seja adotado.

### Timeout e respostas sanitizadas de indisponibilidade (`502` e `504`)

- `Gateway:Downstream:IdentityBaseUrl` exige URL HTTP/HTTPS absoluta sem credenciais, caminho, query ou fragmento; `Gateway:Downstream:Timeout` (padrão `00:00:05`) deve ser maior que zero e de no máximo 30 segundos, validados na subida (`ValidateOnStart`).
- Se o `Identity` não responder dentro de `Gateway:Downstream:Timeout`, o gateway cancela a chamada downstream e retorna `504 Gateway Timeout` em `application/problem+json` (`title`: `"Tempo limite excedido ao contatar o serviço."`, com `traceId` W3C).
- Se a conexão ao `Identity` falhar (`HttpRequestException` ou `IOException`, como conexão recusada ou DNS indisponível), o gateway retorna `502 Bad Gateway` em `application/problem+json` (`title`: `"Serviço temporariamente indisponível."`, com `traceId` W3C).
- As respostas `502` e `504` são sempre sanitizadas: nunca expõem `IdentityBaseUrl`, nomes de hosts internos, endereços IP de downstream, mensagens de socket ou stack traces.

## Alternativa avaliada e risco aceito

**Contexto.** Com um executável por público, a ausência dos controllers administrativos no processo externo seria uma garantia estrutural: o código não existiria nele. Com os dois públicos em uma API, essa garantia depende de configuração (listeners, rede, rota, audiência e política).

**Alternativa.** Dois executáveis do mesmo serviço, sobre a mesma biblioteca e o mesmo banco: um só com as rotas externas e outro só com as administrativas. Preservaria a ausência estrutural das rotas administrativas no processo público.

**Decisão.** Foi implementado o desenho aprovado na [#69](https://github.com/adnerscarpelini/ouroboros-server/issues/69): uma API por serviço. O risco é aceito e compensado pelas camadas acima, cada uma com teste: listeners, rota do outro público como rota inexistente, convenção verificável por prefixo, audiências por canal e testes de arquitetura que enumeram os endpoints reais.

**Consequências.**

- Um defeito em configuração (por exemplo, o administrativo exposto sem querer) é o risco residual; `AllowNonLoopback` explícito e a validação na subida o reduzem, mas não substituem controles de rede.
- Uma rota desconhecida no listener administrativo, com um token forjado de audiência administrativa e papel `Customer`, responde `404`; um executável exclusivo do backoffice, cuja política padrão exigisse `Admin` também para rotas desconhecidas, responderia `403`. Aceito: a política padrão da API exige apenas autenticação para o que não é endpoint administrativo.
- O `TestServer` não tem sockets, então os testes com `WebApplicationFactory` escolhem o listener por um resolvedor só de teste (cabeçalho `X-Test-Listener`, registrado no projeto de testes; a produção ignora o cabeçalho). A separação por portas é verificada com Kestrel real.

## Como verificar

| Garantia | Teste |
| --- | --- |
| Todo endpoint de prefixo administrativo é só administrativo e exige `Admin`; os demais são só públicos | [`HostSeparationTests`](../../tests/Ouroboros.ArchitectureTests/HostSeparationTests.cs) |
| Cada rota do outro público responde como rota inexistente, para os métodos GET, POST, PUT, DELETE, PATCH, OPTIONS e HEAD, sem token e com token válido (status, `WWW-Authenticate` e tipo de conteúdo iguais) | `HostSeparationTests` (rotas enumeradas do próprio roteador) |
| Controllers sob `Controllers/Admin` ou `Controllers/External`, `[ApiVersion]` e prefixo de rota do público; controllers públicos; nenhuma biblioteca de serviço declara controllers | `HostSeparationTests` e [`ControllerRouteTests`](../../tests/Ouroboros.ArchitectureTests/ControllerRouteTests.cs) |
| Política padrão, audiências, chave de assinatura, `AdminAudience` distinta e configuração de cabeçalhos encaminhados | [`AuthorizationTests`](../../tests/Ouroboros.ArchitectureTests/AuthorizationTests.cs) |
| Listeners em Kestrel real: cada porta serve só o seu público e a sua audiência; administrativo inacessível por endereço da máquina; `AllowNonLoopback`; URL inválida e porta repetida impedem a subida | [`KestrelListenerTests`](../../tests/Ouroboros.IntegrationTests/KestrelListenerTests.cs) |
| `X-Forwarded-For` ignorado sem proxy configurado, aceito de proxy configurado, contadores por público | [`ForwardedHeadersTests`](../../tests/Ouroboros.IntegrationTests/ForwardedHeadersTests.cs) |
| OpenTelemetry como único provedor de log na API e no worker, propagador W3C e correlação dos logs com o ProblemDetails | [`TraceCorrelationTests`](../../tests/Ouroboros.ArchitectureTests/TraceCorrelationTests.cs) |
| Sanitização antes de qualquer destino, coletor e destino dedicado, erro original preservado com o coletor indisponível | [`DiagnosticSanitizationTests`](../../tests/Ouroboros.ArchitectureTests/DiagnosticSanitizationTests.cs) e [`DiagnosticsPipelineTests`](../../tests/Ouroboros.IntegrationTests/DiagnosticsPipelineTests.cs) ([cenários](diagnostics-pipeline.md#como-verificar)) |
| Liveness/readiness anônimos e sanitizados, Dockerfiles não-root versionados (`<version>-<commit>`), isolamento do listener administrativo (`5142`) no Compose, migrations explícitas por serviço e prontidão de Identity independente do broker | [`HealthAndContainerArchitectureTests`](../../tests/Ouroboros.ArchitectureTests/HealthAndContainerArchitectureTests.cs) e [`ServiceReadinessAndContainerTests`](../../tests/Ouroboros.IntegrationTests/ServiceReadinessAndContainerTests.cs) |
| Gateways finos fora de `src/Services` sem `DbContext`, `ControllerBase` ou referência aos serviços, restrição de loopback em `Ouroboros.AdminGateway.Api`, isolamento dos prefixos `/api/v1/identity/*` e `/api/v1/admin/identity/*`, e respostas ProblemDetails `502`/`504` sanitizadas dentro do timeout | [`GatewayArchitectureTests`](../../tests/Ouroboros.ArchitectureTests/GatewayArchitectureTests.cs) |
| Encaminhamento de rotas compatíveis pelos dois gateways, preservação da autenticação/audiência/autorização no `Identity` via gateway e diretamente, repasse de `X-Forwarded-For` aceito apenas de proxies confiáveis entre réplicas de gateway e bloqueio de cabeçalhos forjados | [`GatewayIntegrationTests`](../../tests/Ouroboros.IntegrationTests/GatewayIntegrationTests.cs) |

Comandos em [Testes](../development.md#testes).

## Origem

[#69](https://github.com/adnerscarpelini/ouroboros-server/issues/69) (decisão de arquitetura), [#95](https://github.com/adnerscarpelini/ouroboros-server/issues/95) (APIs externa e administrativa no processo de Identity), [#96](https://github.com/adnerscarpelini/ouroboros-server/issues/96) (Ouroboros.Hosting reduzido a mecanismos técnicos; checklist de comportamentos no [estado de implementação](../implementation-status.md#retirada-dos-hosts-agregadores-e-do-scaffolding-96)), [#98](https://github.com/adnerscarpelini/ouroboros-server/issues/98) (imagens Linux versionadas, Compose dos serviços, comando `migrate` e endpoints sanitizados de health e readiness), [#83](https://github.com/adnerscarpelini/ouroboros-server/issues/83) (gateways finos por público externo e administrativo), [#28](https://github.com/adnerscarpelini/ouroboros-server/issues/28) (configuração comum de correlação e logs) e [#66](https://github.com/adnerscarpelini/ouroboros-server/issues/66) (pipeline de diagnósticos sanitizados).

[Voltar à arquitetura](README.md)
