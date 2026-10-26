# Correlação por W3C Trace Context e logs estruturados

## Contexto

Para investigar um erro relatado por um cliente, é preciso achar os logs daquela requisição e do processamento que ela causou em outros serviços. A API de Identity e o worker de Notifications são processos separados, ligados por HTTP e RabbitMQ, e a resposta de erro HTTP já traz o `traceId` do [ProblemDetails](error-responses.md). Os executáveis rodam em Linux e containers, onde a saída esperada é o console, e podem enviar os diagnósticos a um coletor ([pipeline de diagnósticos](diagnostics-pipeline.md)).

## Decisão

- O [W3C Trace Context](https://www.w3.org/TR/trace-context/) (`traceparent` e `tracestate`) é o único formato de correlação, em HTTP e em mensagens. O ASP.NET Core lê o `traceparent` e cria a `Activity` da requisição; não há header próprio.
- A biblioteca técnica [`Ouroboros.Observability`](../../src/Ouroboros.Observability/ObservabilityHostExtensions.cs) aplica a mesma configuração aos dois executáveis, com `AddOuroborosObservability(builder, serviceName, serviceAssembly)`: a API de Identity chama com `identity` e o worker de Notifications com `notifications`. A biblioteca não referencia serviços nem conhece bancos ou regras de negócio.
- A configuração:
  - remove todos os provedores de log padrão, inclusive Debug e, no Windows, o Event Log, e registra o OpenTelemetry como único provedor do `ILogger`. Cada entrada é sanitizada e sai no console, uma linha JSON por entrada ([formato](#formato-do-console)), e, quando configurado, no coletor OTLP ([pipeline de diagnósticos](diagnostics-pipeline.md));
  - registra o propagador W3C (`DistributedContextPropagator.CreateW3CPropagator()`): só `traceparent`/`tracestate` iniciam contexto, e o `Request-Id` legado é ignorado;
  - inclui `TraceId` e `SpanId` da `Activity` corrente em cada registro (`ActivityTrackingOptions`);
  - registra um `ActivityListener` das fontes `Ouroboros.*` que só pede propagação (`PropagationData`): cria as atividades do processamento de mensagens para correlacionar os logs, sem gravar dados de span. Um SDK de tracing que peça mais dados prevalece;
  - registra `ServiceIdentity` (nome e versão do executável). O nome aceita só letras minúsculas e hífen, até 64 caracteres; outro valor impede a subida.
- O processamento de mensagens continua o contexto gravado no envelope pela origem ([mensagens](#propagação-por-mensagens)), sem headers AMQP.
- O trace só correlaciona diagnósticos: não é identidade, autorização nem chave de deduplicação ([limites de uso](#o-trace-não-é-identidade)).

## Identificadores

| Identificador | Formato | Onde aparece |
| --- | --- | --- |
| TraceId | 32 caracteres hexadecimais minúsculos | Campo `traceId` de cada linha de log; segundo segmento do `traceId` do ProblemDetails |
| SpanId | 16 caracteres hexadecimais minúsculos | Campo `spanId` de cada linha de log; terceiro segmento do `traceId` do ProblemDetails |
| Identificador W3C completo | `00-<trace-id>-<span-id>-<flags>` (55 caracteres) | Extensão `traceId` do ProblemDetails e campo `traceparent` do envelope |
| Serviço e versão | `identity` ou `notifications`; versão informacional do assembly do executável | Campos `service` e `serviceVersion` de cada linha de log |
| Ocorrência | GUID v7, um por entrada de log | Campo `occurrenceId` de cada linha de log; atributo `log.record.uid` no destino de diagnósticos |

A versão é o `AssemblyInformationalVersion` do executável e inclui `+<sha do commit>` quando o build ocorre em um checkout git (o contexto da imagem do worker exclui `.git`). A ocorrência identifica uma única entrada e não deriva do trace: várias entradas de uma requisição têm o mesmo `traceId` e `occurrenceId` distintos ([identidade da ocorrência](diagnostics-pipeline.md#identidade-da-ocorrência)).

## Correspondência entre ProblemDetails e logs

O contrato HTTP não muda: a extensão `traceId` do ProblemDetails continua sendo o identificador W3C completo da requisição, `Activity.Current?.Id`, ou `HttpContext.TraceIdentifier` quando não há `Activity`. Nos logs, o mesmo contexto aparece separado em `traceId` e `spanId`:

| ProblemDetails | Linha de log |
| --- | --- |
| `"traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-b7ad6b7169203331-01"` | `"traceId":"4bf92f3577b34da6a3ce929d0e0e4736","spanId":"b7ad6b7169203331"` |

Para achar os logs de uma resposta de erro, extraia o segundo segmento do `traceId` do corpo e filtre as linhas pelo campo `traceId` ([comando](../development.md#logs-dos-executáveis)). Todas as entradas de uma requisição têm o mesmo `traceId` e o mesmo `spanId`, o span da requisição; o processamento posterior das mensagens dessa requisição tem o mesmo `traceId` e spans próprios.

## Contexto recebido em HTTP

| Requisição | Resultado |
| --- | --- |
| Sem `traceparent` | O host gera um trace novo; todos os logs da requisição e o ProblemDetails usam esse trace |
| `traceparent` válido | A requisição continua o trace recebido, com span novo: o `traceId` dos logs é o do header, e o `spanId` é o da requisição, não o recebido |
| `traceparent` inválido: texto qualquer, trace ou span zerados, hexadecimal maiúsculo, versão `ff`, formato incompleto | O valor recebido é descartado e o host gera um trace novo e válido |
| `Request-Id` legado | Ignorado; o host gera um trace novo |

O `tracestate` acompanha o `traceparent` válido. O baggage W3C é aceito pelo propagador, mas não é escrito nos logs nem persistido. Um contexto recebido não autentica: uma requisição sem token com `traceparent` válido continua respondendo `401`, com o trace recebido no ProblemDetails.

## Propagação por mensagens

O transporte do contexto entre serviços são os campos `traceparent`/`tracestate` do [envelope v1](messaging.md#contrato-e-compatibilidade), já autenticados pelo AAD do payload cifrado e persistidos na outbox e na inbox. Por isso o contexto sobrevive a publicação tardia, reentrega e novas tentativas. Envelope, schemas, propriedades e headers AMQP não mudam: nenhum `traceparent` é acrescentado ao transporte.

[`MessageTracing.Start`](../../src/Ouroboros.Messaging/MessageTracing.cs), em `Ouroboros.Messaging`, inicia uma atividade que continua o contexto do envelope como contexto remoto. Sem contexto válido (ausente, inválido, acima dos limites do envelope, ou envelope que não decodifica), a atividade não herda nada da mensagem e começa um trace novo.

| Etapa | Fonte e atividade | Contexto continuado |
| --- | --- | --- |
| Publicação de cada intenção pela outbox de Identity (`EmailOutboxPublisher`) | `Ouroboros.Identity.Outbox`, `outbox.publish` (Producer) | O da requisição que gravou a intenção, salvo na outbox pela [#85](https://github.com/adnerscarpelini/ouroboros-server/issues/85); os logs da publicação continuam esse trace mesmo quando ela ocorre depois, com um span por intenção |
| Aceitação no worker (`EmailConsumer`) | `Ouroboros.Notifications.Inbox`, `inbox.accept` (Consumer) | O do envelope recebido |
| Cada tentativa de entrega (`InboxExecutor`) | `Ouroboros.Notifications.Inbox`, `inbox.deliver` (Consumer) | O do envelope guardado na inbox |

Assim, uma solicitação de cadastro com `traceparent` produz logs com o mesmo `traceId` na API, na publicação e no worker, cada etapa com seu span. Sem contexto válido no envelope, a aceitação e cada tentativa de entrega começam traces próprios. O relay de referências de falha e as manutenções periódicas não iniciam atividade: as referências sanitizadas não carregam contexto, e esses logs saem sem `traceId`/`spanId`.

## O trace não é identidade

- `traceparent`, TraceId, SpanId e o `correlationId` do envelope (derivado do trace pela #85) não são usados para autenticação, autorização, deduplicação nem como chave única de evento.
- A deduplicação continua pelo `MessageId` e pelo fingerprint ([processamento durável](../notifications/durable-processing.md)). Duas intenções criadas com o mesmo `traceparent` têm `MessageId` distintos, e duas mensagens com o mesmo trace são processadas separadamente.
- O cliente controla o `traceparent` que envia. Um trace reaproveitado ou forjado só mistura linhas de log na busca; não altera status, permissões nem processamento. Quem investiga considera o serviço, o horário e o `spanId`, e não trata o trace como prova de origem.

## Formato do console

Cada entrada é uma linha JSON, sem quebra interna, com os campos na ordem abaixo. Exemplo com valores fictícios, de uma publicação não confirmada pela outbox:

```json
{"timestamp":"2026-10-26T13:45:02.1234567+00:00","level":"Warning","category":"Ouroboros.Identity.Infrastructure.Messaging.EmailOutboxPublisher","eventId":3,"service":"identity","serviceVersion":"1.0.0+<sha>","traceId":"4bf92f3577b34da6a3ce929d0e0e4736","spanId":"b7ad6b7169203331","occurrenceId":"019a2033-5f8e-7c41-9b2d-6e0a4c1f3d58","message":"Publicação de 0196b2a0-0000-7000-8000-000000000001 não confirmada: publish-unconfirmed; intenção recuperável.","attributes":{"MessageId":"0196b2a0-0000-7000-8000-000000000001","Code":"publish-unconfirmed"}}
```

| Campo | Conteúdo |
| --- | --- |
| `timestamp` | Instante do registro (chamada ao `ILogger`) em UTC, ISO 8601 |
| `level`, `category`, `eventId` | Nível, categoria do logger e número do evento |
| `service`, `serviceVersion` | Identidade do executável |
| `traceId`, `spanId` | Contexto W3C da `Activity` corrente no momento do log; ausentes fora de uma atividade (subida, manutenções, relay), nunca inventados |
| `occurrenceId` | Identidade da ocorrência, a mesma enviada ao destino como `log.record.uid` |
| `message` | Mensagem recomposta do template com os valores já sanitizados ([sanitização](diagnostics-pipeline.md#sanitização)) |
| `attributes` | Valores estruturados sanitizados, sem `{OriginalFormat}`; números e booleanos como JSON, demais valores como texto em cultura invariante. Com exceção, inclui `exception.type`, `exception.message` e `exception.stacktrace`, sanitizados e limitados. Ausente quando não há valores |

Não há campo `exception`: a exceção original nunca chega ao console. Os escopos de log (`BeginScope`) não são exportados; `traceId` e `spanId` vêm da `Activity` corrente no momento do log. Os níveis continuam em `Logging:LogLevel` de cada `appsettings.json`. Caracteres não ASCII, como acentos, saem sem escape. O console é exportado em lotes: uma linha aparece em até cerca de 1 s depois da chamada ([saturação e encerramento](diagnostics-pipeline.md#saturação-indisponibilidade-e-encerramento)).

## Alternativas descartadas

- **Header próprio, como `X-Correlation-ID`.** Duplicaria o que o W3C Trace Context já transporta e que o ASP.NET Core e o .NET propagam por `Activity`, e exigiria regras próprias para gerar, validar e propagar o id.
- **Trocar o `traceId` do ProblemDetails pelos 32 caracteres do TraceId.** Alteraria silenciosamente o contrato HTTP publicado pela [#4](https://github.com/adnerscarpelini/ouroboros-server/issues/4). O identificador completo foi mantido, e a correspondência com os campos do log está documentada.
- **Manter os provedores padrão de log.** O Event Log/Event Viewer só existe no Windows e o console de texto não é estruturado; os executáveis rodam em Linux e containers, onde o console JSON é coletável. As alternativas de destino dos diagnósticos estão no [pipeline de diagnósticos](diagnostics-pipeline.md#por-que-este-destino).

## Limites e entregas seguintes

- Sanitização, coletor, destino, retenção e comportamento sob saturação estão no [pipeline de diagnósticos](diagnostics-pipeline.md). A captura padronizada de respostas de erro fica com a [#67](https://github.com/adnerscarpelini/ouroboros-server/issues/67); buffer persistente e recuperação do pipeline, com a [#68](https://github.com/adnerscarpelini/ouroboros-server/issues/68).
- O OpenTelemetry exporta só logs. Spans exportados, links entre spans, instrumentação do cliente RabbitMQ e exporter de métricas pertencem à [#88](https://github.com/adnerscarpelini/ouroboros-server/issues/88). As atividades `outbox.publish`, `inbox.accept` e `inbox.deliver` só existem para correlacionar logs.

## Como verificar

| Garantia | Teste |
| --- | --- |
| Requisição sem `traceparent`: um trace para todos os logs e para o ProblemDetails; `traceparent` válido continuado com span novo; sete variantes inválidas ou legadas substituídas por trace novo; `401` sem token com o trace recebido | [`TraceCorrelationTests`](../../tests/Ouroboros.ArchitectureTests/TraceCorrelationTests.cs) |
| OpenTelemetry como único provedor de log na API e em um Generic Host de worker, propagador W3C, nome de serviço validado, campos da linha (inclusive `occurrenceId`) e ausência de trace fora de atividade | `TraceCorrelationTests` |
| Logs da publicação continuam o trace da requisição, com span por intenção e `MessageId` distintos | [`EmailOutboxPublisherTests`](../../tests/Ouroboros.IntegrationTests/EmailOutboxPublisherTests.cs) (`Publication_logs_continue_the_request_trace_without_using_it_as_message_identity`) |
| Processo real do worker: linhas JSON com `service=notifications`, trace do envelope continuado, trace novo sem contexto e mensagens com o mesmo trace processadas separadamente | [`NotificationsWorkerTests`](../../tests/Ouroboros.IntegrationTests/NotificationsWorkerTests.cs) (`Worker_process_logs_json_lines_that_continue_each_message_trace_without_deduplicating_by_trace`) |

Comandos em [Logs dos executáveis](../development.md#logs-dos-executáveis) e [Testes](../development.md#testes).

## Origem

- [#28](https://github.com/adnerscarpelini/ouroboros-server/issues/28): decisão e implementação da correlação.
- [#4](https://github.com/adnerscarpelini/ouroboros-server/issues/4): `traceId` do ProblemDetails.
- [#69](https://github.com/adnerscarpelini/ouroboros-server/issues/69): microsserviços, com correlação por HTTP e mensagens.
- [#85](https://github.com/adnerscarpelini/ouroboros-server/issues/85): contexto W3C gravado na outbox e no envelope.
- [#66](https://github.com/adnerscarpelini/ouroboros-server/issues/66): console como destino do pipeline de diagnósticos, com `occurrenceId` e sem `exception`.

[Voltar à arquitetura](README.md)
