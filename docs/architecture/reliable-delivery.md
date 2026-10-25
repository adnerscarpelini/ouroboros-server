# Contrato e política de entrega confiável

Decisão da [#79](https://github.com/adnerscarpelini/ouroboros-server/issues/79), subordinada à [#69](microservices.md). Define o contrato lógico e os requisitos das próximas entregas; não implementa outbox, broker, worker, publicador ou deduplicação. O envio disponível no checkout está no [estado de implementação](../implementation-status.md).

## Fronteiras e contrato v1

Identity decide destinatário, conteúdo, finalidade, validade e substituição dos links. Notifications entrega uma comunicação já resolvida, sem consultar Identity ou seus dados. Integração por mensagem JSON versionada, sem referência ao assembly de outro serviço. RabbitMQ é o transporte escolhido; a #93 materializa [topologia, biblioteca, schema executável e testes de compatibilidade](messaging.md), preservando o restante desta política para as entregas de origem/consumidor.

O contrato lógico `notification.email.requested`, versão inteira `1`, exige:

| Campo | Semântica |
| --- | --- |
| `messageId` | UUID público da intenção, criado uma vez na transação de origem; permanece igual em republicações, retries e replay autorizado. |
| `type`, `version` | `notification.email.requested`, `1`; não inferir versão pelo conteúdo. |
| `occurredAt`, `expiresAt` | Instantes UTC; prazo absoluto obrigatório, imutável e posterior à criação. Para links, não ultrapassa a validade do token. |
| `producer` | Proprietário de origem, `identity`; sem nome de tabela, id interno ou credencial. |
| `correlationId`, `causationId` | Identificadores opacos de correlação e causa; nunca tokens ou dados pessoais. |
| `payload` | Destinatário único, assunto, texto e finalidade (`registration` ou `password-reset`); nenhuma senha/hash de senha ou id interno. Inclui o link secreto já composto por Identity. |

Campos desconhecidos opcionais são ignorados na v1; campos obrigatórios ausentes, valores inválidos, tipo ou versão desconhecidos não são processados como v1. Mudança incompatível exige nova versão e coexistência de consumidores durante a transição. Acrescentar campo não pode mudar o significado de campo existente. O [envelope da #93](messaging.md#contrato-e-compatibilidade) fixa e testa os limites de tamanho/serialização e encapsula o conteúdo lógico em payload protegido opaco; o receptor da #72 já autentica e decifra conteúdo, mas proteção/persistência da origem e inbox ainda dependem das #85/#87 antes de ativar o fluxo confiável.

## Estado local e publicação

Na #85, alteração de negócio, token e intenção protegida são gravados na mesma transação de Identity. Rollback remove a intenção junto do estado: não há envio nem mensagem publicável. Nenhuma chamada ao broker ou provedor participa da transação ou mantém locks de negócio abertos.

Na #86, o publicador reivindica lotes limitados com lease persistente e prazo, encerra a transação e só então publica. Mensagens e filas são duráveis, publicação persistente, roteamento obrigatório e publisher confirms. Só confirma localmente a publicação após confirmação positiva e ausência de retorno por falta de rota; nack, retorno, timeout ou conexão perdida deixam a intenção recuperável com o mesmo `messageId`. Uma falha entre confirmação do broker e confirmação local provoca republicação, nunca uma nova identidade. Lease vencido permite recuperação após reinício, sem lock durante a rede. Broker indisponível não desfaz negócio nem bloqueia login.

Publicação tem backoff exponencial limitado a cinco minutos, com jitter; indisponibilidade transitória não descarta uma intenção ainda válida por contagem de tentativas. Ao atingir `expiresAt`, não publica mais e registra `Expired`. Configuração corrigível suspende a publicação em estado recuperável, conserva o payload protegido até o prazo e alerta; após correção, retomada explícita conserva identidade e prazo. Contrato inválido é falha permanente: registra `Failed`, descarta o payload e exige nova intenção de negócio após corrigir a causa. Não há loop imediato de requeue nem promessa de disponibilidade ilimitada do armazenamento.

## Aceitação durável e processamento

Na #87, Notifications valida o envelope e persiste a aceitação e o payload protegido no próprio banco, com unicidade de `messageId`, antes de enviar ACK ao RabbitMQ. Falha antes do commit não recebe ACK; falha após commit e antes do ACK resulta em reentrega reconhecida como duplicata. Duplicatas com identidade e conteúdo iguais recebem ACK sem criar novo trabalho; mesma identidade com conteúdo diferente é conflito permanente, sem sobrescrever nem enviar, com diagnóstico sanitizado. Não existe transação distribuída entre banco e broker.

Aceitação durável significa responsabilidade local pelo processamento, não entrega ao provedor. Um executor reivindica o registro com lease e atualização condicional, libera a transação e chama o provedor com timeout inferior ao lease; conclusão usa a versão da reivindicação para impedir um executor antigo de concluir a de outro. Após reinício, lease vencido é recuperável. Execução concorrente não reivindica a mesma tentativa; a ambiguidade externa descrita abaixo permanece.

Tentativas no provedor: no máximo cinco chamadas por intenção, incluindo a primeira, com intervalos de 30 segundos, 2 minutos, 10 minutos e 30 minutos, com jitter de até 20%. Registrar o início da tentativa duravelmente antes da chamada; reinício não zera contagem. Timeout, conexão interrompida, 429 e falha transitória permitem retry até o limite e antes de `expiresAt`; `Retry-After` pode adiar, nunca ultrapassar o prazo. Recusa permanente (por exemplo, endereço recusado definitivamente) termina em `Failed` sem retry automático. Esgotamento também termina em `Failed`.

Contrato inválido e falhas permanentes vão para a fila de falhas, com registro durável, id e código sanitizado. Não repetir corpo secreto nessa fila: usar referência opaca ao registro protegido; para envelope inválido, guardar somente metadados seguros. Falha ao registrar duravelmente a classificação não recebe ACK e não inicia envio. A #93 define o roteamento seguro, e a #87 garante recuperação dessa classificação. Replay é manual, autorizado, auditado, conserva identidade e contagem e só retoma trabalho não entregue, não cancelado e ainda válido; não reabre automaticamente terminais nem cria novo prazo. Se cinco chamadas já ocorreram, uma nova comunicação requer nova intenção de negócio, não reset da contagem.

## Expiração e obsolescência

Verificar o prazo antes da publicação, na aceitação e imediatamente antes de cada chamada ao provedor. Expirado termina em `Expired`, sem envio ou retry. TTL no broker é defesa adicional, não substitui a verificação no consumidor; a #93 deve impedir que expiração no broker elimine a evidência terminal. Uma chamada iniciada dentro do prazo pode terminar depois; o link continua sujeito à validação autoritativa de Identity.

Na substituição de token, Identity revoga o anterior e cancela suas intenções ainda não publicadas na mesma transação. O publicador revalida cancelamento/prazo ao reivindicar e antes de publicar; um envio externo já em curso não pode ser desfeito atomicamente. Mensagem já publicada/aceita pode chegar fora de ordem ou depois da substituição. Não há consulta síncrona ao Identity para escondê-lo: links antigos continuam revogados por Identity, e o texto orienta a usar a solicitação mais recente. Não prometer que um e-mail obsoleto jamais chegará. A expiração absoluta limita essa janela.

## Proteção, acesso e retenção

O payload contém token bruto no link e deve ser tratado como segredo, mesmo que Identity guarde somente o hash no cadastro do token. Exigir TLS nas conexões e criptografia autenticada do payload em repouso em outbox, inbox/processamento, broker e backups. Chaves fora de bancos, imagens e Git, com identidade de chave e rotação; conservar capacidade de leitura somente durante a retenção necessária. A #93 deve escolher mecanismo compatível com payload opaco, e a #85/#87 devem comprovar proteção antes de ativar o fluxo.

Somente publicador de Identity e consumidor de Notifications têm acesso mínimo ao transporte; somente o executor autorizado descriptografa para envio. Operação/replay usa metadados, sem permissão padrão para ler links. Logs, traces, métricas, exceções e fila de falhas não contêm destinatário, assunto, corpo, token, connection string ou payload cifrado. Registrar id, estado, contagem, duração e código de erro permitido.

Descartar payload da outbox na transação que marca publicação confirmada; se cancelado, expirado ou falho definitivamente, descartar na transação terminal. Notifications descarta payload na transação terminal (`Delivered`, `Expired`, `Failed` ou `Cancelled`). Até isso ocorrer, não retê-lo além de `expiresAt`; limpeza recuperável elimina payload vencido mesmo sem worker de envio. Falhas terminais exigem nova solicitação para recompor conteúdo; replay sem payload não reconstrói segredos.

Conservar somente tombstone sanitizado (id, impressão autenticada para conflito, prazo, estado e tentativas) por 30 dias após o prazo absoluto. Nunca enviar mensagem expirada, inclusive após remover tombstone. Dead-letter e diagnósticos seguem o mesmo limite de 30 dias, sem conteúdo. Backups cifrados têm retenção máxima de 30 dias e acesso auditado; restauração reaplica expiração, terminais e limpeza antes de habilitar processamento. Não há retenção indefinida de link por falha permanente. A política de backup/limpeza precisa ser demonstrada pelas entregas de persistência, não presumida como implementada.

## Ambiguidade no provedor

Falha após o provedor aceitar o e-mail e antes do registro de `Delivered` deixa resultado desconhecido. O retry limitado pode enviar outro e-mail, inclusive com lease recuperado. Se o provedor suportar idempotência documentada, usar `messageId` como chave dentro de sua janela; isso não é garantia geral de exactly-once. Sem essa capacidade, aceitar possível duplicação e manter o mesmo link de uso único. Deduplicação de aceitação não resolve a fronteira entre banco e provedor.

## Verificação nas entregas

| Cenário | Resultado exigido | Entrega |
| --- | --- | --- |
| Rollback de negócio/token/intenção | Nenhuma intenção publicável | #85 |
| Commit e processo encerrado ou broker parado | Intenção recuperada com a mesma identidade | #85, #86 |
| Confirmação perdida ou publicador reiniciado | Republicação deduplicável; payload só descartado após confirmação local | #86, #87 |
| Commit da aceitação e ACK perdido; consumidores concorrentes | Um registro persistente e uma reivindicação válida por tentativa | #87 |
| Queda antes/depois do envio | Lease e contagem recuperados; duplicação externa explicitamente possível | #87 |
| Expiração, substituição e entrega fora de ordem | Não iniciar envio expirado; cancelar intenção local; link revogado não funciona | #85–#87 |
| Falha permanente ou cinco tentativas | Terminal durável, sem loop, falha sanitizada | #87 |
| Versão desconhecida ou identidade com conteúdo divergente | Recusa sem envio e sem sobrescrita | #93, #87 |
| Logs, filas, armazenamento e restauração | Ausência de segredo em diagnóstico; proteção e descarte verificáveis | #85–#87, #93 |

Escolhemos outbox em vez de envio direto após commit porque a intenção precisa sobreviver à queda nesse intervalo. Confirmação do broker e ACK do consumidor cobrem fronteiras distintas; nenhum deles confirma envio de e-mail. Referências: [outbox transacional (AWS)](https://docs.aws.amazon.com/prescriptive-guidance/latest/cloud-design-patterns/transactional-outbox.html), [confirmações e ACKs (RabbitMQ)](https://www.rabbitmq.com/docs/confirms).

[Voltar à arquitetura](README.md)
