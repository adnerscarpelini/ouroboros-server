# Worker de entrega de e-mail

## Responsabilidade e disponibilidade

Notifications executa como processo separado em `src/Services/Notifications/Ouroboros.Notifications.Worker`, com biblioteca própria `Ouroboros.Notifications`. É um Generic Host, sem API de negócio, referência ou carregamento de Identity/Settings e sem consulta aos seus bancos. Identity continua responsável por destinatário, texto, tokens e regras. Origem: [#72](https://github.com/adnerscarpelini/ouroboros-server/issues/72).

O consumidor está **desabilitado por padrão**. Nesta etapa, habilitá-lo exige `Development` e provedor explícito, porque ainda não há aceitação durável ou deduplicação persistente. O worker não possui banco, DbContext ou migrations; a persistência própria de Notifications pertence à #87, usando o banco/roles já provisionáveis pela #92. Identity já grava token e intenção protegida na outbox da #85; o publicador da #86 pode encaminhá-las quando habilitado, sem envio direto pela solicitação HTTP. A responsabilidade durável do receptor ainda não está completa.

## Contrato e proteção no receptor

O consumidor espera `notification.email.requested`, versão `1`, produtor `identity`, no [envelope da #93](../architecture/messaging.md#contrato-e-compatibilidade). O contrato incompatível é recusado de forma controlada; não se infere versão pelo conteúdo. O DTO decifrado é próprio de Notifications: `recipient`, `subject`, `text` e `purpose` (`registration` ou `password-reset`). Destinatário deve ser endereço único com até 254 caracteres, assunto não vazio com até 256, texto não vazio; remetente, destinatário e assunto não admitem CR/LF de injeção em cabeçalho. O ciphertext continua limitado a 16 KiB.

`ProtectedEmailPayloadReader` recebe a privada **de proteção de Notifications**, independente da assinatura JWT de Identity. RSA-OAEP-SHA256 decifra uma chave AES de 32 bytes; AES-GCM, com nonce de 12 e tag de 16 bytes, autentica o conteúdo e os metadados canônicos produzidos por `EnvelopeCodec.AuthenticationData`. AAD v1 inclui ids, tipo/versão, instantes, produtor, correlação/causa, substituição, contexto W3C e algoritmo/`keyId`; não inclui o próprio ciphertext/tag. Modificar esses metadados sem uma nova tag válida impede o envio. Chave AES e buffer de plaintext são zerados após a leitura; erros não propagam conteúdo ou detalhes criptográficos.

O manifesto privado admite de uma a 16 entradas únicas `keyId`/`privateKeyPath`, com caminho absoluto e privada RSA de pelo menos 2048 bits; o gerador operacional usa 3072. A leitura usa o `keyId` da mensagem e falha explicitamente se a chave não estiver disponível. A chave é aberta apenas pelo receptor. Identity recebe somente o manifesto público, cifra intenções desde a #85 e pode publicá-las pela #86; os testes também publicam mensagens protegidas para exercitar o receptor. A origem já tem outbox protegida; a inbox permanece pendente da #87.

Antes de decifrar e imediatamente antes de chamar o provedor, o caso de uso verifica `expiresAt`. Uma mensagem vencida retorna `Expired`, sem envio. Não consulta Identity para verificar substituição ou token: a política de obsolescência persistente será completada na #87; Identity continua sendo a autoridade que recusa links revogados.

## Consumo inicial e falhas

O worker usa a credencial `notifications_consumer` da #93, sem permissão para declarar topologia ou publicar. Abre a fila existente por declaração passiva, prefetch de **uma mensagem** e timeout de entrega de **10 segundos**. Em indisponibilidade de rede/broker, tenta reconstruir conexão e assinatura a cada cinco segundos. A recuperação automática do cliente é desativada nesse adaptador para manter um único ciclo explícito de reconexão.

Neste modo de Development, o ACK acontece depois de o provedor concluir ou de o caso de uso classificar a mensagem como expirada. Falha de contrato, proteção ou provedor suspende o processo com código de saída `1`, fecha o canal sem ACK e permite reentrega do original. Não há DLX do corpo secreto, requeue imediato no mesmo processo ou sucesso fictício. Após corrigir a configuração/causa, reiniciar o worker permite nova tentativa. Uma mensagem incompatível pode continuar bloqueando o consumo; a classificação durável e a recuperação operacional são a #87, não este mecanismo inicial de suspensão.

Não há inbox, contagem persistente de tentativas, lease, classificação durável de falha ou deduplicação em banco. Também não há transação/lock de negócio durante chamadas externas, pois esta entrega não acessa persistência de negócio. Logs do adaptador contêm somente ids, estado e códigos permitidos, sem destinatário, assunto, corpo, link ou ciphertext.

## Provedor local e ambiguidade

`PickupDirectory` existe somente em Development e grava `.eml` UTF-8 sem BOM, com cabeçalhos MIME e CRLF, preservando destinatário e texto. Assunto não ASCII usa codificação MIME. A escrita vai primeiro para um arquivo temporário e depois para `<messageId sem hífens>.eml`, por move sem sobrescrita. Repetir a mesma identidade com o arquivo já presente não cria outro arquivo nem substitui o anterior; o `Message-ID` também deriva da identidade.

Essa idempotência do adaptador local não equivale à deduplicação persistente da #87: não há impressão de conteúdo para identificar a mesma identidade com conteúdo divergente, e remover/perder a pasta permite nova gravação. Não há garantia geral de exactly-once no provedor de e-mail. Falha depois do efeito externo e antes do ACK pode provocar reentrega.

O `.eml` é um artefato de entrega de desenvolvimento e contém o link secreto em claro, como o e-mail entregue ao destinatário. Use pasta restrita, fora do Git e de compartilhamentos; não imprima o conteúdo em logs ou issues. O adaptador não gerencia a retenção desses arquivos. `Provider=None` falha explicitamente ao tentar enviar; não representa entrega concluída.

## Configuração, execução e imagem

O worker usa seção `Notifications` e `UserSecretsId` próprio (`ouroboros-notifications-development`). `Identity:Email` é recusada, mesmo que o consumo esteja desabilitado; provedor desconhecido também impede a configuração. `PickupDirectory` fora de Development e consumidor habilitado sem provedor são recusados. Configuração, chaves e comandos em [executar Notifications](../development.md#executar-o-worker-de-notifications).

O [Dockerfile](../../src/Services/Notifications/Ouroboros.Notifications.Worker/Dockerfile) usa SDK `10.0.402` no build e runtime `aspnet:10.0.13-alpine3.24`, necessário ao FrameworkReference do host. Copia apenas os projetos de Notifications e a biblioteca técnica de mensageria. O [dockerignore específico](../../src/Services/Notifications/Ouroboros.Notifications.Worker/Dockerfile.dockerignore) exclui segredos, PEM, `.env`, `.local`, `bin` e `obj`. O runtime executa como UID **1654**, com `/app/.local/notifications-emails` gravável. Chaves são montadas externamente, nunca copiadas à imagem.

A imagem local foi construída e iniciada nos testes, sem assemblies de Identity/Settings ou PEM. O consumidor permanece desligado por padrão também na imagem. Não há publicação da imagem, Compose de serviços, probes/readiness ou observabilidade implementados por esta entrega; essas fatias continuam nas #98/#88.

## Como verificar

[`NotificationsWorkerTests`](../../tests/Ouroboros.IntegrationTests/NotificationsWorkerTests.cs) cobre conteúdo `.eml`, repetição com arquivo estável, adulteração de AAD, injeção de cabeçalho, expiração, provedor/configuração antiga ou desconhecida, além de processo filho real com RabbitMQ e recusa de versão incompatível seguida de reentrega. A análise de dependências inclui a biblioteca e o worker, sem relaxar regras ou criar exceções para Notifications.

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class '*NotificationsWorkerTests'
```

Os seis cenários foram executados em Windows e Linux com broker/processo reais; não comprovam aceitação durável, deduplicação persistente ou o fluxo HTTP → outbox → broker. A imagem foi validada com pickup gravável sob UID 1654; o gerador de chaves foi verificado em Linux com diretório `0700` e arquivos `0600`. Testes isolados não dependem de `.env`, user-secrets ou provedor externo.

[Voltar à documentação](../README.md)
