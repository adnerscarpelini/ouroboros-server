# Worker de entrega de e-mail

## Responsabilidade e disponibilidade

Notifications executa como processo separado em `src/Services/Notifications/Ouroboros.Notifications.Worker`, com biblioteca própria `Ouroboros.Notifications`. É um Generic Host, sem API de negócio, referência ou carregamento de Identity/Settings e sem consulta aos seus bancos. Identity continua responsável por destinatário, texto, tokens e regras. Origem: [#72](https://github.com/adnerscarpelini/ouroboros-server/issues/72).

O consumidor está **desabilitado por padrão**. A #87 acrescenta banco, migrations e [aceitação/deduplicação persistente](durable-processing.md), usando somente a conexão própria de Notifications. Habilitar exige banco, chaves, credenciais e provedor explícito; o único provedor disponível, `PickupDirectory`, continua restrito a Development. Identity grava intenções pela #85 e publica pela #86 quando habilitado, sem envio direto pela solicitação HTTP.

## Contrato e proteção no receptor

O consumidor espera `notification.email.requested`, versão `1`, produtor `identity`, no [envelope da #93](../architecture/messaging.md#contrato-e-compatibilidade). O contrato incompatível é recusado de forma controlada; não se infere versão pelo conteúdo. O DTO decifrado é próprio de Notifications: `recipient`, `subject`, `text` e `purpose` (`registration` ou `password-reset`). Destinatário deve ser endereço único com até 254 caracteres, assunto não vazio com até 256, texto não vazio; remetente, destinatário e assunto não admitem CR/LF de injeção em cabeçalho. O ciphertext continua limitado a 16 KiB.

`ProtectedEmailPayloadReader` recebe a privada **de proteção de Notifications**, independente da assinatura JWT de Identity. RSA-OAEP-SHA256 decifra uma chave AES de 32 bytes; AES-GCM, com nonce de 12 e tag de 16 bytes, autentica o conteúdo e os metadados canônicos produzidos por `EnvelopeCodec.AuthenticationData`. AAD v1 inclui ids, tipo/versão, instantes, produtor, correlação/causa, substituição, contexto W3C e algoritmo/`keyId`; não inclui o próprio ciphertext/tag. Modificar esses metadados sem uma nova tag válida impede o envio. Chave AES e buffer de plaintext são zerados após a leitura; erros não propagam conteúdo ou detalhes criptográficos.

O manifesto privado admite de uma a 16 entradas únicas `keyId`/`privateKeyPath`, com caminho absoluto e privada RSA de pelo menos 2048 bits; o gerador operacional usa 3072. A leitura usa o `keyId` da mensagem e falha explicitamente se a chave não estiver disponível. A chave é aberta apenas pelo receptor. Identity recebe somente o manifesto público, cifra intenções desde a #85 e pode publicá-las pela #86; os testes também publicam mensagens protegidas para exercitar o receptor. O envelope protegido é persistido na inbox da #87 sem consulta à origem.

A aceitação e o executor verificam `expiresAt` sem enviar conteúdo vencido. O cursor persistente da #87 descarta mensagens anteriores e cancela predecessoras ativas, inclusive quando a sucessora já chegou expirada. Não consulta Identity para validar token: Identity continua sendo a autoridade que recusa links revogados. [Estados, ordem e limites](durable-processing.md#aceitação-antes-do-ack).

## Aceitação, execução e falhas

O consumidor usa `notifications_consumer`, sem configurar topologia, prefetch 1 e reconexão a cada cinco segundos. O ACK ocorre **após o commit da aceitação/classificação no banco**, sem esperar o provedor. Falha de banco ou commit fecha o canal sem ACK e permite reentrega; contrato incompatível vira classificação durável e referência sanitizada, sem guardar o corpo original.

Outro executor controla leases, até cinco chamadas persistidas, expiração, terminais e retomada após reinício. O relay usa a credencial separada `notifications_failures`, com confirms, para publicar somente referências de falha. Nenhuma chamada externa mantém transação ou lock de negócio. Os detalhes ficam na [nota de processamento durável](durable-processing.md); configuração e recuperação manual ficam em [desenvolvimento](../development.md#executar-o-worker-de-notifications).

## Provedor local e ambiguidade

`PickupDirectory` existe somente em Development e grava `.eml` UTF-8 sem BOM, com cabeçalhos MIME e CRLF, preservando destinatário e texto. Assunto não ASCII usa codificação MIME. A escrita vai primeiro para um arquivo temporário e depois para `<messageId sem hífens>.eml`, por move sem sobrescrita. Repetir a mesma identidade com o arquivo já presente não cria outro arquivo nem substitui o anterior; o `Message-ID` também deriva da identidade.

Essa idempotência do arquivo complementa a deduplicação persistente da #87. Remover/perder a pasta permite nova gravação se uma tentativa ainda recuperável voltar ao adaptador. Não há garantia geral de exactly-once: falha após o efeito externo e antes do registro local de `Delivered` pode provocar outra chamada ao provedor, mesmo com a aceitação já reconhecida ao broker.

O `.eml` é um artefato de entrega de desenvolvimento e contém o link secreto em claro, como o e-mail entregue ao destinatário. Use pasta restrita, fora do Git e de compartilhamentos; não imprima o conteúdo em logs ou issues. O adaptador não gerencia a retenção desses arquivos. `Provider=None` falha explicitamente ao tentar enviar; não representa entrega concluída.

## Configuração, execução e imagem

O worker usa seção `Notifications` e `UserSecretsId` próprio (`ouroboros-notifications-development`). `Identity:Email` é recusada, mesmo que o consumo esteja desabilitado; provedor desconhecido também impede a configuração. `PickupDirectory` fora de Development e consumidor habilitado sem provedor são recusados. Configuração, chaves e comandos em [executar Notifications](../development.md#executar-o-worker-de-notifications).

O [Dockerfile](../../src/Services/Notifications/Ouroboros.Notifications.Worker/Dockerfile) usa SDK `10.0.402` no build e runtime `aspnet:10.0.13-alpine3.24`, necessário ao FrameworkReference do host. Copia Notifications e as bibliotecas técnicas Messaging/BuildingBlocks, sem projetos ou modelos de Identity/Settings. O [dockerignore específico](../../src/Services/Notifications/Ouroboros.Notifications.Worker/Dockerfile.dockerignore) exclui segredos, PEM, `.env`, `.local`, `bin` e `obj`. O runtime executa como UID **1654**, com `/app/.local/notifications-emails` gravável. Chaves são montadas externamente, nunca copiadas à imagem.

A imagem local foi construída e iniciada nos testes, sem assemblies de Identity/Settings ou PEM. O consumidor permanece desligado por padrão também na imagem. Não há publicação da imagem, Compose de serviços, probes/readiness ou observabilidade implementados por esta entrega; essas fatias continuam nas #98/#88.

## Como verificar

[`NotificationsWorkerTests`](../../tests/Ouroboros.IntegrationTests/NotificationsWorkerTests.cs) cobre conteúdo `.eml`, repetição com arquivo estável, adulteração de AAD, injeção de cabeçalho, expiração, provedor/configuração antiga ou desconhecida, além de processo filho real com PostgreSQL/RabbitMQ, aceitação durável e classificação segura de versão incompatível. A análise de dependências inclui a biblioteca e o worker, sem relaxar regras ou criar exceções para Notifications.

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class '*NotificationsWorkerTests'
```

A família de testes inclui os [cenários duráveis e o fluxo integrado do lote](durable-processing.md#como-verificar), com bancos separados, broker e processos reais. A imagem foi validada com pickup gravável sob UID 1654; o gerador de chaves foi verificado em Linux com diretório `0700` e arquivos `0600`. Testes isolados não dependem de `.env`, user-secrets ou provedor externo.

[Voltar à documentação](../README.md)
