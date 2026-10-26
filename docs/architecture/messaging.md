# RabbitMQ e contrato de mensagem

## Decisão e disponibilidade

A [#93](https://github.com/adnerscarpelini/ouroboros-server/issues/93) materializa o broker, a topologia e o envelope técnico da [política de entrega confiável](reliable-delivery.md). O Compose local executa RabbitMQ `4.4.0-management` em container próprio, com hostname estável `ouroboros-rabbitmq`, volume `rabbitmq-data` e virtual host `ouroboros`. AMQP e gestão são publicados somente em loopback, inicialmente nas portas 5672 e 15672. É um nó local para estudo, sem cluster ou alta disponibilidade.

O [provisionador](../../scripts/messaging/provision-rabbitmq.ps1) declara toda a topologia com a credencial do operador. Essa escolha substitui a recomendação inicial de cada runtime declarar seus objetos: os serviços ficam sem permissão `configure`, impedindo que permissões necessárias para declarar um exchange sejam usadas para administrar filas de mesmo nome. Reexecutar o provisionador converge objetos e permissões, sem apagar mensagens, filas ou volumes.

Identity publica intenções da outbox quando o serviço hospedado da #86 é explicitamente habilitado. O [worker de Notifications](../notifications/worker.md), da #72, persiste aceitação/deduplicação pela #87, decifra o payload no executor e usa adaptador local de e-mail em Development; possui imagem Linux própria. A #85 já implementa proteção/persistência da origem; a #86 implementa publicação confirmada, e processamento durável/deduplicação está implementado na #87. A infraestrutura da #93, por si só, não implementa essas garantias.

## Topologia e credenciais

Todos os objetos abaixo pertencem ao vhost `ouroboros`; exchanges são `direct` e duráveis, filas são `quorum` duráveis, sem auto-delete.

| Exchange | Routing key | Fila | Limites |
| --- | --- | --- | --- |
| `ouroboros.notifications` | `email.requested.v1` | `notifications.email.v1` | 64 MiB, `reject-publish`, `delivery-limit=-1`, sem TTL e sem DLX do envelope original |
| `ouroboros.notification-failures` | `email.failed.v1` | `notifications.failures.v1` | 8 MiB, `reject-publish`, TTL máximo de 30 dias |

O limite de bytes aplica pressão ao publicador confirmado, em vez de remover mensagens existentes. A fila principal não expira nem dead-lettera automaticamente o corpo: a #87 registra duravelmente aceitação/expiração/classificação antes de reconhecer a mensagem. `delivery-limit=-1` evita a remoção automática por excesso de reentregas do broker; não autoriza retries ilimitados no executor do provedor. As cinco tentativas e os estados terminais continuam definidos pela #79.

| Usuário | Escrita | Leitura | Configuração |
| --- | --- | --- | --- |
| `identity_publisher` | Apenas `ouroboros.notifications` | Nenhuma | Nenhuma |
| `notifications_consumer` | Nenhuma | Apenas `notifications.email.v1` | Nenhuma |
| `notifications_failures` | Apenas `ouroboros.notification-failures` | Nenhuma | Nenhuma |

A credencial separada de falhas foi aprovada na #93: permite o futuro relay de referências sanitizadas sem dar ao consumidor permissão para publicar e-mails. O usuário de operação fica fora dos serviços; os três usuários de runtime não têm tags administrativas. O bootstrap de volume novo usa um operador configurado, sem criar `guest`. Em volume preexistente, o operador deve conferir o inventário de usuários; o provisionador não remove automaticamente usuários alheios ou dados.

A fila de falhas recebe somente `notification.email.failed`, versão 1: ids opacos, código permitido, instante e `retainUntil`, conforme [schema](../../contracts/notifications/email-failed.v1.schema.json). Não recebe endereço, assunto, corpo, token nem ciphertext do envelope. `EncodeFailure` limita a retenção a `expiresAt` original + 30 dias; sem prazo original válido, a classificação usa seu instante como base. O relay da #87 publica com TTL por mensagem limitado ao tempo restante até `retainUntil`, além do teto de fila, para que uma publicação tardia não reinicie 30 dias. Não há dead-letter automático do original. [Retomada auditada do relay](../development.md#recuperar-processamento-e-referências-de-falha) não reprocessa o e-mail.

## Contrato e compatibilidade

O [schema de solicitação v1](../../contracts/notifications/email-requested.v1.schema.json) é a referência do wire contract. JSON UTF-8 sem BOM tem limite de **32 KiB**; o conteúdo cifrado, depois de decodificar Base64, tem limite de **16 KiB**. `messageId`, `correlationId` e `causationId` são UUIDs não vazios. Instantes são UTC e `expiresAt` é posterior a `occurredAt`. `supersessionKey` é identificador opaco, não endereço de e-mail. `traceparent`/`tracestate` são campos obrigatórios que podem ser nulos; quando presentes, seguem W3C, com limites de 55/512 caracteres. Sem `traceparent`, `tracestate` deve ser nulo.

O payload é opaco: `algorithm=RSA-OAEP-256+A256GCM`, `keyId`, `wrappedKey`, `nonce`, `tag` e `ciphertext`. Base64 deve ser canônico; os tamanhos decodificados são 256–1024 bytes para a chave envolvida, 12 para nonce, 16 para tag e 1–16384 para ciphertext. A escolha combina uma chave AES256 por mensagem com RSA-OAEP-SHA256 para envolvê-la. O receptor da #72 já decifra e autentica conteúdo/metadados com a privada exclusiva de Notifications; `EnvelopeCodec.AuthenticationData` fixa o AAD canônico v1. Chaves de proteção são distintas da assinatura JWT da #84. A origem já cifra/persiste na outbox da #85; Notifications preserva esse envelope cifrado na inbox da #87 até o estado terminal. Validar a estrutura isoladamente não comprova proteção criptográfica correta.

`EnvelopeCodec.TryDecode` recebe explicitamente o tipo, versão e produtor esperados; para este fluxo: `notification.email.requested`, `1`, `identity`. Nomes obrigatórios são case-sensitive; propriedades duplicadas, inclusive variantes de caixa, são recusadas em qualquer nível. Ausência de obrigatório, valor inválido, produtor inesperado ou excesso de tamanho retorna `invalid-contract`; tipo/versão inesperados retornam `unsupported-version`. O codec não faz ACK, descarte silencioso, requeue ou envio.

Campo opcional desconhecido é ignorado na v1 e não pode alterar significado existente. Remoção de campo, mudança de significado ou outra incompatibilidade exige nova versão e convivência explícita dos adaptadores/rotas durante a migração. Não inferir versão pelo conteúdo nem atualizar os dois serviços obrigatoriamente em conjunto.

### Compartilhamento sem referência entre serviços

`Ouroboros.Messaging` contém o DTO técnico imutável do envelope opaco, codec e fábrica de conexão, sem entidades, destinatários, regras de negócio ou referência a serviços. Os schemas permanecem independentes de assembly. Adotamos essa biblioteca neutra para não duplicar limites e validação do envelope entre adaptadores; ela não é uma referência Identity → Notifications ou Notifications → Identity. As regras de arquitetura verificam que bibliotecas compartilhadas não dependem de serviços e que não há referência interserviço.

A recomendação inicial de DTOs próprios continua aplicada ao **conteúdo de negócio decifrado**: Notifications já possui `EmailContent` na #72, sem dependência do produtor. A origem da #85 compõe sua própria projeção ao cifrar a intenção. A fonte do envelope pode ser compilada em versões compatíveis distintas de cada serviço; mudanças aditivas e testes de wire contract permitem implantação independente. A biblioteca não distribui regras de Identity nem obriga atualizar os executáveis juntos.

## Cliente, limites e TLS

Escolhemos o cliente oficial `RabbitMQ.Client` **7.2.2**, com versão centralizada. Precisamos de primitivas explícitas de publisher confirms, publicação obrigatória, ACK e recuperação de conexão, exercitáveis contra broker real. MassTransit foi a alternativa não adotada: uma abstração de transporte maior não é necessária para esta topologia e para o controle explícito de envelope/progresso; não houve decisão sobre sua licença.

Licença verificada em **25/10/2026** no `.nuspec` do pacote 7.2.2 e na [documentação oficial](https://www.rabbitmq.com/client-libraries/dotnet-api-guide#license): `Apache-2.0 OR MPL-2.0`. O projeto adota a opção Apache-2.0, cujo [texto acompanha a versão](https://github.com/rabbitmq/rabbitmq-dotnet-client/blob/v7.2.2/LICENSE-APACHE2).

`RabbitConnectionFactory` habilita TLS fora de `localDevelopment`, com nome do servidor e validação padrão de certificado, sem relaxar erros. O Compose local usa AMQP sem TLS e gestão HTTP somente em loopback; outros ambientes precisam de listeners e certificados TLS provisionados pelo operador. A fábrica limita handshake/conexão a 5 s, continuação a 10 s, heartbeat a 15 s, até oito canais e um despacho de consumidor por vez. Recuperação de rede é automática a cada 5 s; recuperação de topologia fica desabilitada porque o runtime não a declara. Falha inicial de conexão ainda deve ser tratada pelo proprietário; a fábrica não implementa o loop do publicador/worker.

Publisher confirms, `mandatory` e mensagens persistentes são usados pelo publicador real da #86 e nos testes do broker; seus lotes, lease, retries e retomada estão na [nota da outbox](../identity/email-outbox.md#publicação-e-recuperação). O consumidor da #87 usa a fábrica, desativa a recuperação automática do cliente e reconstrói explicitamente conexão/assinatura a cada cinco segundos; não declara topologia, usa prefetch 1 e faz ACK somente após o commit da aceitação/classificação. Recuperar conexão não substitui recuperar progresso persistido nem garante exactly-once no provedor de e-mail.

## Como verificar

[`MessageContractTests`](../../tests/Ouroboros.IntegrationTests/MessageContractTests.cs) verifica produtor e projeção independente do consumidor, acréscimo opcional, versão/produtor inesperados, nomes obrigatórios, duplicatas, limites e referências sanitizadas. O [worker tem cenários próprios](../notifications/worker.md#como-verificar), que incluem processo real e adulteração do AAD.

[`RabbitMqTopologyTests`](../../tests/Ouroboros.IntegrationTests/RabbitMqTopologyTests.cs), com [`RabbitMqFixture`](../../tests/Ouroboros.IntegrationTests/RabbitMqFixture.cs), usa broker real, volume exclusivo de teste, credenciais efêmeras e o provisionador PowerShell real. Cobre permissões e recusas, ausência de `guest` no bootstrap, persistência após reinício, reprovisionamento e leitura de `-EnvFile` sem imprimir senhas. A fixture remove somente os recursos que criou. Esses cenários foram executados em Linux limpo com SDK 10.0.402 e PowerShell 7.6.7; não dependem do Compose nem do `.env` local. Comandos de execução e provisionamento em [desenvolvimento](../development.md#rabbitmq-local).

[Voltar à arquitetura](README.md)
