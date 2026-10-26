# Pipeline de diagnósticos sanitizados

## Contexto

A API de Identity e o worker de Notifications são processos separados, cada um com banco próprio. Para investigar uma falha é preciso guardar os diagnósticos dos dois fora do console, com retenção limitada e sem misturá-los aos dados de negócio: um diagnóstico não pode depender do banco do serviço, participar da transação que falhou, levar segredos ou dados pessoais para um destino, nem derrubar ou travar o processo quando o destino falha. O ambiente é Linux e containers, e o ambiente educacional precisa de pouca memória e disco.

## Decisão

- **ILogger → OpenTelemetry.** A biblioteca técnica [`Ouroboros.Observability`](../../src/Ouroboros.Observability/ObservabilityHostExtensions.cs), chamada por `AddOuroborosObservability` nos dois executáveis, remove os provedores padrão e registra o OpenTelemetry como único provedor do `ILogger`. Os casos de uso continuam usando `ILogger<T>`, sem classe estática nem dependência de destino.
- **Sanitização antes de qualquer destino.** O primeiro processador, `SanitizingLogProcessor`, atribui a [identidade da ocorrência](#identidade-da-ocorrência) e sanitiza o registro no thread de quem chamou o `ILogger`, sem E/S. Só depois o registro segue para os destinos ([regras](#sanitização)).
- **Logs, traces e métricas.** O console JSON (stdout) recebe logs, sempre ativo, com o [formato da correlação](trace-correlation.md#formato-do-console). Se `Observability:Otlp:Endpoint` estiver definido, os três sinais seguem por OTLP/HTTP protobuf para `<endpoint>v1/logs`, `<endpoint>v1/traces` e `<endpoint>v1/metrics`. Sem endpoint, não há exportação OTLP nem saída de spans/métricas no console. Os appsettings não definem endpoint.
- **Coletor e destino dedicado.** O coletor (`otel-collector`) recebe OTLP, limita memória, sanitiza de novo e encaminha ao destino dedicado (`diagnostics-store`), que grava arquivos JSON rotacionados em um volume próprio. Os dois são OpenTelemetry Collector Contrib e são provisionados no [Compose](../../compose.yaml) local ([topologia](#topologia-e-recursos)).
- **Independente dos bancos de negócio.** Nenhum processo grava diagnóstico em banco, e nem o coletor nem o destino acessam bancos. A exportação ocorre fora de qualquer transação: o rollback de uma operação de negócio não descarta o diagnóstico dela.
- **Não é auditoria.** Os diagnósticos têm retenção curta, podem ser descartados sob saturação e são redigidos a mais quando há dúvida. A auditoria de negócio continua no serviço proprietário, no próprio banco.

Os demais elementos da configuração comum, propagador W3C e listener das atividades `Ouroboros.*`, estão em [Correlação por W3C Trace Context](trace-correlation.md).

## Por que este destino

| Alternativa | Avaliação |
| --- | --- |
| Gravador próprio em PostgreSQL, com tabela de eventos de diagnóstico | Era o escopo anterior desta entrega, substituído pela decisão de microsserviços da [#69](https://github.com/adnerscarpelini/ouroboros-server/issues/69): acoplaria o diagnóstico a um banco central ou ao banco de negócio, com credencial, schema e migrations próprios, e a falha do banco seria justamente a que menos se conseguiria registrar |
| Backends completos, como Grafana Loki, Tempo e Grafana, ou OpenObserve e Jaeger | Oferecem consulta e painéis, mas cada um exige memória, disco, credenciais e operação de mais um serviço com interface própria, custo alto para o ambiente educacional |
| **Arquivos JSON rotacionados gravados por um coletor dedicado (escolhida)** | Atendem retenção limitada e leitura simples com ferramentas de texto, com memória e disco fixos e nenhuma credencial nova |

Os processos só conhecem OTLP. Trocar o destino por um backend completo exige mudar só o exportador do coletor, sem alterar os executáveis.

## Topologia e recursos

```text
API de Identity ─┐                         127.0.0.1:4318          rede interna do Compose
                 ├─ OTLP/HTTP ──► otel-collector ──► OTLP/HTTP ──► diagnostics-store ──► volume diagnostics-data
Worker ──────────┘  (opcional)      (2ª sanitização)                (arquivos rotacionados)
       └─ stdout: console JSON (sempre)
       └─ stderr: sinal de falha do pipeline
```

| Componente | Configuração | Recursos e limites |
| --- | --- | --- |
| Processo (cada executável) | Sanitização e um processador em lote por destino de logs e por exportador de traces; leitor periódico de métricas | Fila de 2048 itens por processador de logs/traces, lotes de até 512, envio a cada 1 s, timeout de exportação de 5 s. Métricas agregadas e exportadas a cada 1 s, com timeout de 5 s e até 256 séries por instrumento/view |
| `otel-collector` | [`collector.yaml`](../../config/observability/collector.yaml), imagem `otel/opentelemetry-collector-contrib:0.162.0` | OTLP/HTTP na porta 4318, publicada só em `127.0.0.1` (`OTEL_COLLECTOR_PORT` altera a porta da máquina); `memory_limiter` de 160 MiB (pico de 40 MiB) com `mem_limit: 256m` e `GOMEMLIMIT=200MiB`; pipelines de logs, traces e métricas com lotes de 512 a 1024 itens a cada 1 s; exportação ao destino com gzip, timeout de 5 s, fila em memória de 20000 itens por sinal e novas tentativas de 1 s a 30 s por até 300 s |
| `diagnostics-store` | [`diagnostics-store.yaml`](../../config/observability/diagnostics-store.yaml), mesma imagem | OTLP/HTTP só na rede interna do Compose, sem porta publicada; `memory_limiter` de 96 MiB com `mem_limit: 160m`; exportadores `file/logs`, `file/traces` e `file/metrics` em `/var/lib/ouroboros-diagnostics/{logs,traces,metrics}.jsonl` (OTLP/JSON, uma linha por lote recebido) |
| `diagnostics-volume-init` | `busybox:1.37.0`, roda e termina | Ajusta o volume `diagnostics-data` para o usuário sem privilégios 10001 dos coletores, modo `0750`; o destino só sobe depois que ele conclui com sucesso |

Os health checks dos coletores (porta 13133) não são publicados. O receptor OTLP não tem TLS nem autenticação: o coletor aceita qualquer produtor que alcance a porta, e por isso só escuta em loopback na máquina. Outros ambientes exigem TLS e controle de acesso antes de expô-lo.

## Identidade da ocorrência

Cada registro recebe, no `SanitizingLogProcessor`, um GUID v7 novo no atributo `log.record.uid`, antes de qualquer destino. O mesmo valor aparece no console como `occurrenceId` e no destino como o atributo `log.record.uid`, e um valor com esse nome vindo de quem chamou o `ILogger` é substituído.

| Identificador | Identifica | Onde aparece |
| --- | --- | --- |
| `log.record.uid` / `occurrenceId` | Uma entrada de log | Atributo do registro no destino; campo `occurrenceId` do console |
| TraceId, SpanId | A requisição ou o processamento de mensagem, compartilhados por várias entradas | Campos `traceId`/`spanId` do registro no destino e do console ([correlação](trace-correlation.md#identificadores)) |
| `service.name`, `service.version` | O executável | Resource do lote no destino; campos `service` e `serviceVersion` do console |
| `service.instance.id` | O processo (um GUID novo a cada subida) | Resource do lote no destino; não aparece no console |
| `deployment.environment.name` | O ambiente do host (`Development`, `Production`, `Testing`) | Resource do lote no destino; não aparece no console |

A identidade da ocorrência não deriva do trace e não é chave de negócio. Como o coletor repete o envio ao destino depois de falhas, um mesmo registro pode ser gravado mais de uma vez; o `log.record.uid` igual identifica a repetição.

## Sanitização

### No processo (primeira camada)

Aplicada a todo registro, antes do console e do OTLP, por [`SanitizingLogProcessor`](../../src/Ouroboros.Observability/SanitizingLogProcessor.cs) e [`DiagnosticRedaction`](../../src/Ouroboros.Observability/DiagnosticRedaction.cs):

- **Atributos.** Até 32 por registro; `{OriginalFormat}` é descartado. Uma chave que contém, sem diferenciar maiúsculas, `password`, `passwd`, `pwd`, `senha`, `secret`, `segredo`, `token`, `authorization`, `cookie`, `credential`, `credencial`, `connectionstring`, `apikey`, `api_key`, `privatekey`, `private_key`, `recipient`, `email`, `ciphertext` ou `payload` tem o valor trocado por `[REDACTED]`, qualquer que seja o tipo (inclusive números, como `TokenCount`). Textos passam pelos padrões abaixo e ficam limitados a 1024 caracteres. Números, booleanos, `Guid`, datas, `TimeSpan` e enums são preservados. Qualquer outro objeto vira `[NomeDoTipo]`, sem chamar `ToString`: objetos, corpos e headers nunca são serializados.
- **Mensagem.** O formatter do `ILogger` não é chamado. A mensagem é recomposta do template com os valores já sanitizados e limitada a 4096 caracteres; um valor redigido no atributo aparece redigido na mensagem. Estado sem template chega com o texto já formatado pelo SDK e passa só pelos padrões.
- **Exceções.** Tipo, mensagem e pilha saem como os atributos `exception.type`, `exception.message` e `exception.stacktrace`, com mensagens e pilha sanitizadas, cadeia de até 4 níveis (exceções internas e de `AggregateException`) e pilha limitada a 8192 caracteres. `Exception.Data` e propriedades não são lidas. O registro nunca carrega a exceção original adiante.
- **Padrões.** Blocos PEM; credenciais em URL (`esquema://usuário:senha@`); query de URL (vira `?[REDACTED]`); JWT; `Bearer` e `Basic`; atribuições `chave=valor` e `chave: valor`, inclusive em JSON com aspas, aspas escapadas e arrays, com chaves em inglês ou português (`password`, `senha`, `secret`, `segredo`, `token`, `client_secret`, `api_key`, `cookie`, `credencial` e variações); e-mails viram `[EMAIL]`; valores opacos de 32 caracteres ou mais que misturam letras e dígitos viram `[REDACTED]`. Hexadecimais, GUIDs e identificadores de código são preservados, para que traces, ids e pilhas continuem legíveis. As expressões são de tempo linear e a entrada é cortada no dobro do limite antes delas. Sanitizar duas vezes dá o mesmo resultado.
- **Falha da sanitização.** Se a própria sanitização lançar, o registro é substituído por `Diagnóstico descartado: sanitização indisponível.`, com nova identidade de ocorrência e sem o conteúdo original, e a falha não chega a quem chamou o `ILogger`.

### No coletor (segunda camada)

O processador `transform/sanitize` do coletor reaplica, ao corpo e aos valores de atributo, os padrões de JWT, `Bearer`/`Basic`, e-mail e atribuições de segredo, inclusive em JSON e com a chave `senha`. É defesa em profundidade e cobre também um produtor que não use `Ouroboros.Observability`. A regra de atribuição consome o valor até `,`, `;`, `&` ou `}`, com até 128 caracteres: o viés é redigir a mais. Por isso o destino pode ter menos texto que o console; por exemplo, um GUID logo depois de `token:` some no destino.

Nos traces, o coletor também remove `url.full`, `url.query`, `url.path`, `http.target` e atributos cujas chaves indiquem segredo, e-mail, payload ou usuário, e reaplica os padrões de JWT, autorização, e-mail e atribuição de segredo aos valores de atributos de span. Isso não é uma sanitização universal de recursos, eventos ou produtores externos: os executáveis continuam responsáveis por não capturar conteúdo sensível.

### Traces e métricas no processo

[`SanitizingSpanProcessor`](../../src/Ouroboros.Observability/SanitizingSpanProcessor.cs) roda antes do lote OTLP. Remove as chaves sensíveis usadas nos logs, `user.id`/`enduser.id`, URL completa, query, caminho bruto, `http.target` e atributos de headers. Valores textuais passam pelos mesmos padrões, até 1024 caracteres; só booleanos e números suportados são preservados sem conversão. O nome do span é sanitizado até 256 caracteres; `tracestate` e descrição do status são removidos. As fontes instrumentadas não criam eventos nem atributos de links; a captura automática de exceções HTTP está desligada (`RecordException=false`). Baggage não é exportado. Novas fontes ou eventos exigem rever essa fronteira.

As métricas não capturam payload ou entrada livre: as dimensões do fluxo têm valores de conjunto fechado. Uma view remove dimensões diferentes de `code`, `state`, `result`, `outcome` dos meters `Ouroboros.*`; nos meters `Microsoft.AspNetCore.Hosting` e `Microsoft.AspNetCore.Server.Kestrel`, só preserva `http.route`, `http.request.method` e `http.response.status_code`. Cada instrumento/view tem cardinalidade limitada a 256 séries. Identificadores de usuário, e-mail, token e mensagem não são dimensões.

### Métricas do fluxo

| Meter / instrumento | Tipo e dimensões | Significado |
| --- | --- | --- |
| `Ouroboros.Identity.Outbox` / `outbox.backlog` | Gauge, sem dimensões | Intenções pendentes ainda válidas |
| `outbox.oldest.age` | Gauge em segundos, sem dimensões | Idade da intenção pendente mais antiga |
| `outbox.publish.failures`, `outbox.publish.retries` | Contadores, `code` | Falhas e novas reivindicações após tentativa anterior; códigos `invalid-contract`, `configuration-suspended`, `broker-rejected`, `publish-unconfirmed`, `unknown` |
| `outbox.terminal` | Gauge, `state=Cancelled/Expired/Failed` | Intenções terminais ainda retidas no banco; expiração, substituição e falha |
| `Ouroboros.Notifications.Inbox` / `inbox.acceptances` | Contador, `result` | Aceitação após commit: `Accepted`, `Duplicate`, `Expired`, `Superseded`, `Rejected`, `Conflict` |
| `inbox.delivery.attempts` | Contador, `outcome` | Desfecho do processamento: `delivered`, `transient`, `permanent`, `exhausted`, `expired`, `suspended`; valor não reconhecido vira `unknown`. A taxa desse contador descreve o consumo/processamento, não entrega garantida ao destinatário |
| `inbox.failure.backlog` | Gauge, sem dimensões | Referências de falha ainda sem publicação confirmada |
| `inbox.terminal` | Gauge, `state=Expired/Cancelled/Failed` | Mensagens terminais ainda retidas na inbox |

Os gauges emitem somente depois do primeiro snapshot. Identity atualiza o snapshot ao concluir um ciclo do publicador; Notifications, na manutenção que roda na subida e a cada 30 s. Não há consulta ao banco no callback de coleta. Se o ciclo falhar, o valor anterior pode ficar desatualizado. Estados terminais diminuem quando a retenção remove linhas: são snapshots, não contadores históricos de descarte. Contadores são locais ao processo e reiniciam com ele, sem garantia de registrar cada commit em caso de queda. Atraso aparece no backlog/idade, rejeição na aceitação, e falha de entrega nos desfechos e na fila de referências.

### Limites

A sanitização reconhece chaves e padrões, não o significado do texto. Não são reconhecidos: segredo em texto livre sem padrão, XML como `<password>…</password>`, valor sem aspas com várias palavras (no console, só a primeira é redigida), string com aspa não fechada, array aninhado e segredos hexadecimais longos. Por isso valores entram nos logs só pelos parâmetros do template, nunca concatenados à mensagem, e objetos não são passados para serialização. Não registre corpos, headers, tokens ou conteúdo de mensagens, mesmo esperando que sejam redigidos.

## Saturação, indisponibilidade e encerramento

| Situação | Comportamento |
| --- | --- |
| Destino lento ou coletor indisponível | Quem chama o `ILogger` só sanitiza e enfileira; a exportação ocorre no thread do processador em lote, com timeout de 5 s. Com a fila de 2048 cheia, registros novos são descartados, sem bloquear o chamador nem crescer memória. Cada destino tem a própria fila: o console continua mesmo com o coletor fora |
| Erro original | A falha do diagnóstico nunca substitui o erro original: um `500` continua saindo como ProblemDetails, e nenhuma exceção do pipeline chega ao negócio |
| Recursão | Os eventos internos do OpenTelemetry não passam pelo `ILogger`; a falha é sinalizada só pela [saída independente](#sinalização-independente) |
| Coletor sob pressão de memória | O `memory_limiter` recusa dados novos antes de atingir o limite do container; o produtor recebe erro e o lote recusado não é persistido |
| Destino indisponível | O coletor guarda até 20000 itens por sinal na fila em memória e repete por até 300 s; com a fila cheia, recusa novos dados com `503` (`sending queue is full`) e conta o descarte nos próprios logs. Religado o destino, a fila é drenada |
| Encerramento gracioso | Cada processador exporta o que estiver na fila, com timeout de cerca de 5 s por destino |
| Encerramento abrupto (SIGKILL, queda do processo ou do container) | Perde o que está em memória: até 2048 registros por destino no processo, o lote do último segundo e a fila em memória do coletor |

O console também passa pela fila em lote: as linhas aparecem em até cerca de 1 s depois da chamada, e as do último segundo se perdem em encerramento abrupto. O processo não tem buffer em disco e o coletor não persiste a fila; buffer persistente e recuperação do coletor ficam com a [#68](https://github.com/adnerscarpelini/ouroboros-server/issues/68).

## Sinalização independente

[`TelemetryFailureSignal`](../../src/Ouroboros.Observability/TelemetryFailureSignal.cs) escuta, no processo inteiro, os eventos `Warning` ou mais graves das fontes `OpenTelemetry-Sdk` e `OpenTelemetry-Exporter-*` e escreve em **stderr**, nunca no `ILogger`, uma linha:

```text
ouroboros-observability <instante UTC> <nível> <fonte>#<evento>: <mensagem sanitizada, até 512 caracteres> (suprimidos desde o último aviso: N)
```

Cada evento sai no máximo uma vez a cada 30 s; as repetições no intervalo são contadas em `N` na linha seguinte do mesmo evento. Exemplo de descarte por fila cheia:

```text
ouroboros-observability 2026-10-26T13:45:02.1234567+00:00 Warning OpenTelemetry-Sdk#32: 'BatchLogRecordExportProcessor' exporting to 'OtlpLogExporter' dropped '27448' item(s) due to buffer full. (suprimidos desde o último aviso: 0)
```

O descarte é informado de forma agregada, quando o processador encerra ou a exportação falha, e não por registro. Falhas e timeouts do exportador OTLP também aparecem. A linha não vai para o console JSON (stdout) nem para o destino. Os erros do próprio coletor e do destino ficam nos logs dos seus containers.

A única exclusão é o evento `MetricInstrumentIgnored` do SDK cujo motivo é um meter não assinado pelo provider: não coletar esse meter faz parte da seleção intencional. Outros motivos do mesmo evento, instrumentos duplicados e falhas reais continuam sinalizados.

## Retenção e limpeza

| Onde | Retenção |
| --- | --- |
| Destino (`diagnostics-data`) | Cada arquivo ativo (`logs.jsonl`, `traces.jsonl`, `metrics.jsonl`) é rotacionado ao atingir 32 MiB, com o instante UTC da rotação no nome do arquivo rotacionado. Por sinal, ficam no máximo 8 rotacionados; os com mais de 7 dias e os além dos 8 são removidos. Cerca de 288 MiB por sinal (1 ativo e 8 rotacionados), aproximadamente 864 MiB para os três |
| Console (stdout) e stderr | Cabe ao ambiente de execução, como o terminal ou o driver de log do container |
| Coletor | Nada fica guardado depois da entrega ao destino; a fila só existe em memória |

Limites da limpeza:

- A limpeza não é agendada: os rotacionados vencidos ou excedentes só são removidos quando ocorre uma rotação. Com pouco volume, o arquivo ativo não chega a 32 MiB e pode reter registros por mais de 7 dias; os rotacionados vencidos também ficam até a próxima rotação.
- Com muito volume, o limite de 8 arquivos prevalece e remove registros antes de 7 dias.
- O volume sobrevive a `docker compose down` e a reinícios. Não há expurgo sob demanda nem exclusão seletiva; apagar o volume descarta todos os diagnósticos e não faz parte dos procedimentos documentados.
- A retenção não serve para auditoria nem para prova de que algo ocorreu: um registro pode ter sido descartado sob saturação ou redigido.

## Consequências

- Os diagnósticos dos dois executáveis ficam num único destino, filtráveis por serviço, instância, ambiente, trace e ocorrência, sem banco e sem credencial nova.
- O custo fixo do pipeline local é de até cerca de 416 MiB de memória nos dois coletores e aproximadamente 864 MiB de disco para os três sinais.
- Perder diagnósticos é aceito sob saturação, indisponibilidade e encerramento abrupto, desde que sinalizado e sem afetar o negócio.
- A sanitização em duas camadas reduz, mas não elimina, o risco de vazamento; a [disciplina de template](#limites) continua necessária.

## Limites e entregas seguintes

- HTTP e fontes próprias de mensagens são instrumentados, com [contexto e links](trace-correlation.md#propagação-por-mensagens). As fontes automáticas `RabbitMQ.Client.*` não são assinadas e não há injeção de headers AMQP.
- A [política de captura de falhas](error-responses.md#política-de-captura-de-falhas) registra respostas `4xx`/`5xx` com rota normalizada, status, duração, classificação e código, além de catches relevantes e comandos/workers. O resumo HTTP não repete a exceção técnica, e nenhum identificador de usuário é acrescentado. As categorias de comando e atualização do EF são suprimidas para não exportar SQL/parâmetros nem duplicar a exceção; a fronteira registra `exception.*` sanitizados.
- Buffer persistente no coletor, recuperação após queda e múltiplas instâncias são da [#68](https://github.com/adnerscarpelini/ouroboros-server/issues/68).
- Compose dos executáveis, imagens das APIs e probes são da [#98](https://github.com/adnerscarpelini/ouroboros-server/issues/98); hoje a API e o worker rodam fora do Compose e enviam ao coletor pela porta de loopback.
- O `eventId` aparece no console, mas não no registro OTLP do destino.

## Como verificar

| Garantia | Teste |
| --- | --- |
| Padrões redigidos e identificadores preservados; segredos, payload e `Exception.Data` fora de todos os destinos; JSON e chaves em português; objeto cujo `ToString` falha ou vaza; estado sem template; identidade da ocorrência única por registro, distinta do trace e não aceita do chamador; limites de mensagem, valor, atributos e cadeia; ausência de dependência de bancos e serviços | [`DiagnosticSanitizationTests`](../../tests/Ouroboros.ArchitectureTests/DiagnosticSanitizationTests.cs), sobre o [`DiagnosticsHost`](../../tests/Ouroboros.ArchitectureTests/DiagnosticsHost.cs) |
| OpenTelemetry como único provedor do `ILogger` na API e no worker; campos da linha do console, inclusive `occurrenceId` | [`TraceCorrelationTests`](../../tests/Ouroboros.ArchitectureTests/TraceCorrelationTests.cs) |
| Spans com links, gravação mesmo com flag remoto desligado, remoção de atributos sensíveis e `tracestate`, dimensões de métricas restritas e exclusão do aviso de meter intencionalmente não assinado | [`DistributedTelemetryTests`](../../tests/Ouroboros.ArchitectureTests/DistributedTelemetryTests.cs) |
| Cadastro com publicação posterior à resposta HTTP, RabbitMQ e worker em processo separado: HTTP, publicação, aceitação e entrega com o mesmo trace no destino real; links do ciclo, ausência de query/token/e-mail e dimensões OTLP permitidas | [`MicroservicesBatchEndToEndTests`](../../tests/Ouroboros.IntegrationTests/MicroservicesBatchEndToEndTests.cs) (`Deferred_publication_and_worker_export_same_trace_and_bounded_metric_labels_to_real_collector`) |
| Atraso sem chamada ao provedor, rejeição de contrato e falha permanente com sinais distintos; dimensões fechadas na outbox | [`NotificationInboxTests`](../../tests/Ouroboros.IntegrationTests/NotificationInboxTests.cs), [`EmailOutboxPublisherTests`](../../tests/Ouroboros.IntegrationTests/EmailOutboxPublisherTests.cs) |
| Coletor e destino reais, com os YAML do repositório e o volume preparado como no Compose: evento sensível chega ao destino sanitizado, com serviço, versão, instância, ambiente, trace, span e ocorrência; rollback de negócio não descarta o diagnóstico e nenhum banco o recebe; o coletor sanitiza de novo OTLP bruto de outro produtor; coletor que aceita a conexão e não responde, com 30000 logs e spans: o chamador não bloqueia, o `500` em ProblemDetails é preservado, o sinal de descarte sai em stderr e nenhum log do OpenTelemetry volta ao `ILogger` | [`DiagnosticsPipelineTests`](../../tests/Ouroboros.IntegrationTests/DiagnosticsPipelineTests.cs), com a [`DiagnosticsPipelineFixture`](../../tests/Ouroboros.IntegrationTests/DiagnosticsPipelineFixture.cs) (exige Docker) |

Subir o coletor, habilitar o envio, ler o destino e interpretar o stderr: [Diagnósticos no coletor local](../development.md#diagnósticos-no-coletor-local).

## Origem

- [#66](https://github.com/adnerscarpelini/ouroboros-server/issues/66): decisão e implementação do pipeline.
- [#88](https://github.com/adnerscarpelini/ouroboros-server/issues/88): traces e métricas distribuídos, links e dimensões restritas.
- [#67](https://github.com/adnerscarpelini/ouroboros-server/issues/67): captura segura nas entradas HTTP, comandos e catches relevantes.
- [#69](https://github.com/adnerscarpelini/ouroboros-server/issues/69): microsserviços, com diagnóstico independente dos bancos de negócio.
- [#28](https://github.com/adnerscarpelini/ouroboros-server/issues/28): correlação W3C e console JSON.

[Voltar à arquitetura](README.md)
