# APIs e workers dos serviços

## Responsabilidade dos executáveis

Cada serviço HTTP possui API própria. Identity executa em [`Ouroboros.Identity.Api`](../../src/Services/Identity/Ouroboros.Identity.Api), um processo com dois públicos (clientes e administradores) que delega aos casos de uso locais. Notifications executa em [`Ouroboros.Notifications.Worker`](../../src/Services/Notifications/Ouroboros.Notifications.Worker), Generic Host separado sem API de negócio, com configuração e imagem próprias. Seu [processamento durável](../notifications/durable-processing.md) usa banco próprio e habilitação explícita; o provedor local é restrito a Development. Saúde operacional não obriga criar API de negócio.

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

O roteador exclui os endpoints do outro público antes de avaliar o método HTTP ([`ListenerEndpointSelectorPolicy`](../../src/Services/Identity/Ouroboros.Identity.Api/Routing/ListenerEndpointSelectorPolicy.cs), política de nó de ordem menor que a do método). Por isso a rota administrativa chamada no listener público (e a externa no administrativo) se comporta como qualquer rota desconhecida, com qualquer método HTTP: sem `405`; `401` sem token; `404` com token válido para o listener. A resposta não revela que a rota existe. Um endpoint sem metadado de público (por exemplo, um futuro health check) vale nos dois listeners.

### Audiências e esquemas de autenticação

Os dois esquemas JWT ([`IdentityAuthenticationSchemes`](../../src/Services/Identity/Ouroboros.Identity/Contracts/IdentityAuthenticationSchemes.cs)) usam o mesmo emissor e a mesma chave; diferem na audiência aceita. Na API, um esquema de política escolhe o esquema pelo listener que recebeu a requisição. Detalhes em [Autenticação](authentication.md#audiências-por-canal).

### Política padrão

A configuração comum de `Ouroboros.Hosting` define como política de fallback "usuário autenticado", sem conhecer papéis ou públicos. O papel `Admin` dos endpoints administrativos vem da convenção de rota, não do controller. Requisição sem rota correspondente, que não tem metadado de público, só passa pela autenticação do listener: autenticada, recebe `404`; sem token, `401`.

### Rate limiting e cabeçalhos encaminhados

O rate limiting de autenticação ([`Identity:RateLimiting`](../identity/auth-rate-limiting.md)) mantém contadores separados por público: o escopo é o público do listener, então o mesmo IP tem um contador no externo e outro no administrativo.

`X-Forwarded-For` e `X-Forwarded-Proto` só são aplicados quando `Identity:ForwardedHeaders:KnownProxies` (IPs) ou `KnownNetworks` (CIDR) estão configurados, e apenas de origens nessas listas (um salto). Sem configuração, o middleware nem entra no pipeline: os cabeçalhos são ignorados e o rate limit por IP usa o endereço da conexão, de modo que um cabeçalho forjado não troca o contador. Configuração inválida (IP ou CIDR malformado) impede a subida. Isso prepara o uso de proxies sem depender dos [gateways](#gateways).

## Comandos de operação

create-admin pertence a Identity e roda na própria API, sem subir o servidor HTTP nem abrir listeners. Senha fica fora de argumentos e logs. Preservar unicidade concorrente do primeiro Admin, validação de domínio e códigos de saída. Execução: [desenvolvimento](../development.md#criar-o-primeiro-administrador).

## Configuração comum

[`Ouroboros.Hosting`](../../src/Ouroboros.Hosting/OuroborosHostExtensions.cs) conserva apenas mecanismos técnicos HTTP reutilizáveis pelas APIs dos serviços: controllers, versionamento por URL, ProblemDetails com `traceId`, a política padrão "usuário autenticado" e a ordem do pipeline (tratamento de exceções, páginas de status, rate limiter, autenticação, autorização e controllers). Não conhece serviços, banco, papéis, públicos nem regras de negócio: cada API registra o próprio serviço e define suas políticas, e o rate limiter e a autenticação são registrados pelo serviço. Seu consumidor é `Ouroboros.Identity.Api`. Não é agregador central.

[`Ouroboros.Observability`](../../src/Ouroboros.Observability/ObservabilityHostExtensions.cs) dá a configuração de diagnóstico e correlação comum às APIs e aos workers: `AddOuroborosObservability` é chamada pela API de Identity (serviço `identity`) e pelo worker de Notifications (serviço `notifications`). Remove os provedores de log padrão, inclusive o Windows Event Log, registra o OpenTelemetry como único provedor do `ILogger`, sanitiza cada entrada antes do console JSON e, quando `Observability:Otlp:Endpoint` está definido, do coletor OTLP, aceita só o contexto W3C (`traceparent`/`tracestate`) e permite continuar esse contexto no processamento de mensagens. Também não conhece serviços, bancos nem regras de negócio. Decisão, formato e limites em [Correlação por W3C Trace Context](trace-correlation.md) e [Pipeline de diagnósticos sanitizados](diagnostics-pipeline.md).

## Gateways

A #83 define gateways em etapa posterior. Não bloqueia acesso direto, testes ou implantação inicial dos serviços. Roteamento, timeouts, proxies confiáveis e limites não substituem a autorização do proprietário.

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

Comandos em [Testes](../development.md#testes).

## Origem

[#69](https://github.com/adnerscarpelini/ouroboros-server/issues/69) (decisão de arquitetura), [#95](https://github.com/adnerscarpelini/ouroboros-server/issues/95) (APIs externa e administrativa no processo de Identity) e [#96](https://github.com/adnerscarpelini/ouroboros-server/issues/96) (Ouroboros.Hosting reduzido a mecanismos técnicos; checklist de comportamentos no [estado de implementação](../implementation-status.md#retirada-dos-hosts-agregadores-e-do-scaffolding-96)) e [#28](https://github.com/adnerscarpelini/ouroboros-server/issues/28) (configuração comum de correlação e logs) e [#66](https://github.com/adnerscarpelini/ouroboros-server/issues/66) (pipeline de diagnósticos sanitizados).

[Voltar à arquitetura](README.md)
