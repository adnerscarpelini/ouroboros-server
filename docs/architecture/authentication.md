# Autenticação e autorização entre serviços

## Emissão do access token

Identity possui credenciais, sessões e emissão. A porta `IAccessTokenIssuer` em Application usa o adaptador JWT de Infrastructure, que emite RS256 com `kid` e formatos distintos: `principal_type=user` para usuários e `principal_type=client` para [integradores](../identity/api-clients.md) desde a [#39](https://github.com/adnerscarpelini/ouroboros-server/issues/39). Chave privada e manifesto ficam em arquivos externos, lidos somente pelo emissor; consumidores recebem chaves públicas. A configuração e a rotação estão em [desenvolvimento](../development.md#chave-de-assinatura-dos-tokens). Origem da assinatura assimétrica: [#84](https://github.com/adnerscarpelini/ouroboros-server/issues/84).

Escolhemos RS256 com RSA de pelo menos 2048 bits (3072 no procedimento local): há suporte direto em `Microsoft.IdentityModel` e a geração/importação PEM usa a BCL, sem pacote adicional. ES256 também seria adequado e produziria assinaturas menores; manter um único algoritmo assimétrico simplifica a operação e os testes. O custo aceito é o tamanho maior de chave/token e a leitura dos arquivos a cada operação. Não há provedor externo de identidade ou cofre de chaves implementado.

## Validação do token

Cada serviço valida assinatura, issuer, audience, expiração e principal. Usuário, integrador e serviço não são intercambiáveis. Não distribuir chave privada aos consumidores.

Para principal de usuário, `sub` e `sid` devem ser UUIDs não vazios, com exatamente um claim de cada, e `role` deve ocorrer uma vez, como `Customer` ou `Admin`. `principal_type` precisa ocorrer uma vez e valer `user`. A única exceção é a omissão desse claim em um token HS256 de usuário ainda dentro da transição. Claims contraditórios, inclusive `role=[Customer,Admin]` e `principal_type=[user,service]`, recebem `401`.

Para principal de integrador, `IdentityBearerEvents` exige esquema público, RS256, exatamente um `sub` UUID não vazio e um `principal_type=client`, sem `role` nem `sid`. O emissor acrescenta `jti` e os tempos de validade, usa `Audience` e não cria sessão ou refresh token. A cada autenticação, uma porta scoped consulta o estado atual do integrador em Identity, sem cache. `Authentication:Jwt:ClientValidationTimeout` tem padrão de dois segundos e deve ser positivo, no máximo cinco segundos. Estado inativo ou token inválido recebem `401`; falha ou timeout na consulta recebem `503` sanitizado, com `traceId` e `no-store`, inclusive com negociação `Accept` não atendida pelo escritor padrão. Não há fallback permissivo.

`ValidAlgorithms` restringe a validação a RS256 e HS256; o resolvedor associa cada algoritmo ao tipo correto de chave. RS256 exige `kid` conhecido e chave pública ainda aceita. HS256 só resolve a chave simétrica na janela legada absoluta; fora dela, a lista de chaves é vazia. Não há tentativa com todas as chaves, aceitação de `none` ou uso de chave RSA como segredo HMAC. A validação segue a [RFC 8725](https://datatracker.ietf.org/doc/html/rfc8725).

Validação obrigatória indisponível não concede acesso por fallback. Chaves, tokens, PEM e detalhes de leitura não aparecem nas mensagens de erro do adaptador. Respostas de autenticação continuam genéricas.

### Chaves públicas e cache

`GET /api/v1/identity/keys` é anônimo, versionado e existe somente no listener público. Retorna JWKS com `keys` e os campos públicos `kty`, `use`, `alg`, `kid`, `n` e `e`, sem caminho de arquivo, privada ou usuário. A resposta usa `Cache-Control: public, max-age=300, must-revalidate`; a política `identity-keys` permite inicialmente 60 requisições/minuto por IP e público, sem fila, com `429` acima do limite. O comportamento do listener administrativo é o de rota inexistente: `401` anônimo e `404` com token administrativo válido.

O contrato dos consumidores é cache de cinco minutos, atualização ao vencer ou ao encontrar `kid` desconhecido e intervalo mínimo de 30 segundos entre buscas, inclusive após falha. Buscas concorrentes devem ser serializadas. Sem chave válida em cache, uma falha de busca recusa o token; cache vencido não estende a confiança. O consumidor aceita somente RS256 e continua verificando issuer, audience, validade e principal. Fora de desenvolvimento local, buscar JWKS por HTTPS com validação do certificado. O host de prova demonstra esse comportamento; não há ainda outro serviço de negócio validador nem biblioteca de cache compartilhada.

### Rotação e transição

O manifesto admite de uma a 16 chaves públicas, com `kid` único, uma ativa e caminho absoluto da sua privada. A subida valida a correspondência do par. O arquivo é relido por emissão, validação e JWKS, permitindo trocar a chave sem implantar consumidores em conjunto. A substituição do manifesto deve ser atômica, no mesmo filesystem; arquivo parcial, indisponível ou inválido falha de forma explícita.

Publicar a próxima chave pública antes de ativá-la permite que os caches a conheçam. Ao interromper a emissão com a chave anterior, registrar `retiredAt` em UTC e conservar sua pública até `acceptUntil`, no mínimo `retiredAt + AccessTokenLifetime + 30 s + TTL do cache`. Com os valores atuais, a janela mínima é **20 minutos e 30 segundos**. Janelas menores são recusadas. Após o prazo, a chave deixa de aparecer no JWKS e de ser resolvida por Identity; a privada antiga já pode ter sido retirada após a troca de todos os emissores. O operador mantém os relógios sincronizados e usa o instante da última emissão antiga ao calcular a retirada.

A migração de HS256 usa `legacy.keyPath`, `legacy.startsAt` e `legacy.endsAt` no manifesto. A duração não pode exceder `AccessTokenLifetime` (15 minutos), não é renovada ao reiniciar e aceita a chave apenas em `startsAt <= agora < endsAt`. A emissão nova é sempre RS256. Depois do limite, Identity recusa HS256 e não abre mais o arquivo legado; o operador remove o arquivo, a entrada `legacy` e as cópias antigas em user-secrets/variáveis. A aplicação não altera ACLs nem apaga um segredo montado. O [procedimento operacional](../development.md#transição-de-hs256) é parte da implantação.

### Audiências por canal

A API de Identity atende dois públicos com audiências distintas, na seção `Authentication:Jwt`:

| Chave | Valor inicial | Papel | Esquema de autenticação |
| --- | --- | --- | --- |
| `Audience` | `ouroboros-api` | `Customer` ou principal `client` | `Bearer` |
| `AdminAudience` | `ouroboros-backoffice` | `Admin` | `Bearer.Admin` |

- **Emissão.** O emissor escolhe a audiência pelo papel do usuário: `Customer` recebe `Audience`; `Admin`, `AdminAudience`. Integradores recebem somente `Audience` por `IssueClient` e não carregam papel de usuário.
- **Validação.** Os dois esquemas JWT compartilham emissor, chave e algoritmo e diferem na audiência aceita. Um esquema de política escolhe `Bearer.Admin` quando a requisição chegou ao listener administrativo e `Bearer` nos demais casos; o listener vem da conexão ([Listeners](hosts.md#listeners)), não de cabeçalho.
- **Recusa cruzada.** O token de um canal usado no outro é recusado com o `401` genérico (`WWW-Authenticate: Bearer`, sem motivo), mesmo com papel adequado ao destino.
- **Configuração.** Na API, `AdminAudience` é obrigatória e deve ser diferente de `Audience`; sem isso, os dois canais emitiriam e aceitariam a mesma audiência e a separação dependeria só dos papéis, e o processo não sobe. Na biblioteca de Identity ela é opcional: ausente, vale `Audience`; presente, não pode ser igual a ela.
- **Login e renovação.** Cada canal só aceita o papel do canal ([login](../identity/login.md)).

## Autorização

Negar por padrão; anonimato é explícito. Cada operação exige capacidade e público adequados no serviço, inclusive acesso direto. Administração exige papel e restrição de rede.

Na API de Identity, o endpoint exige autenticação salvo `[AllowAnonymous]`, e toda rota `api/v{version}/admin/...` exige também o papel `Admin`, aplicado por convenção de rota mesmo sem política declarada no controller ([Rota por público](hosts.md#rota-do-outro-público-é-rota-inexistente)). Customer ou chamada sem token em endpoint administrativo é recusado (`403` ou `401`).

As políticas padrão e de fallback são de usuário e recusam principal `client`. Uma rota para integradores deve declarar explicitamente `identity:api-clients.access`; isso não concede acesso a endpoints de usuário ou backoffice. Não há endpoint de negócio para integradores. A rota pública `POST /api/v1/identity/oauth/validation`, acrescentada na #38, é um contrato técnico com essa política: recebe o Bearer original e responde `204` somente após autenticação e consulta autoritativa. A política de gerenciamento `identity:api-clients.manage` continua exigindo Admin.

### Políticas por capacidade

Preservar políticas das funcionalidades. Identificar usuário por ExternalId e sessão por sid. Revalidar estado autoritativo quando exigido. Rate limiting considera réplicas e headers somente de proxies confiáveis (`Identity:ForwardedHeaders`, em [Rate limiting e cabeçalhos encaminhados](hosts.md#rate-limiting-e-cabeçalhos-encaminhados)).

Access tokens de usuário carregam `principal_type=user`, `sub`, `role`, `sid`, `jti`, `iat`, `nbf` e `exp`, além do `kid` no cabeçalho. O claim público é o ExternalId, nunca a chave sequencial do banco. Papéis de usuário são Customer ou Admin; políticas são nomeadas por capacidade no proprietário. Token Bearer inválido ou inativo responde 401 genérico com `WWW-Authenticate: Bearer`; falta de permissão responde 403. Ambos usam [ProblemDetails](error-responses.md), sem explicar conteúdo ou motivo de recusa de token. O endpoint de emissão client credentials usa o desafio Basic e extensão `error` descritos em [integradores](../identity/api-clients.md#emissão-por-client-credentials). A autorização não revela rotas a chamadas sem autenticação.

## Sessões e revogação

Refresh token é opaco, rotacionado e armazenado por hash. Reuso, logout e redefinição preservam invariantes locais. Logout mantém access tokens válidos até expiração conforme contrato.

A [#38](https://github.com/adnerscarpelini/ouroboros-server/issues/38) entrega desativação administrativa auditada e idempotente, com aviso assíncrono na mesma transação local. Após commit, novas emissões e a próxima validação autoritativa recusam o client; não há invalidação retroativa de chamadas já validadas. Falha do publicador não reativa o cadastro. Detalhes em [integradores](../identity/api-clients.md#desativação).

Futuros serviços consumidores devem enviar o token original ao contrato HTTP de validação com timeout limitado, fora de transações e locks próprios. Somente `204` concede acesso; `401`, `403`, `429`, `503`, timeout, erro de rede ou qualquer outro resultado negam. Não cachear uma validação positiva nem autorizar por fallback em falha. A política `identity-client-validation` limita inicialmente esse contrato a 120 requisições/minuto/IP. Ainda não há outro serviço consumidor de tokens de integrador no checkout; isso não substitui a validação adequada de assinatura, audiência e principal no destino.

## Testes

Verificar rotação, issuer/audience, principais, públicos, concorrência e indisponibilidade. A porta da #71 continua isolando o adaptador; desde a #39 inclui `IssueClient` sem expor opções JWT ao caso de uso.

Em [`AuthorizationTests`](../../tests/Ouroboros.ArchitectureTests/AuthorizationTests.cs), sobre a API de Identity com controllers de teste: endpoint sem anotação exige token; tokens inválidos (`alg=none`, outra chave, expirado, outro emissor, audiência do outro canal, HS384, sem `exp`, `nbf` futuro, expirado além da tolerância de 30 s) recebem `401`; token vencido dentro da tolerância é aceito; a resposta de recusa não explica o motivo; Customer recebe `403` em qualquer endpoint administrativo, com ou sem política declarada; Admin é autorizado; o token de cada audiência é recusado no listener do outro canal; a API não sobe sem chave de assinatura válida, sem `AdminAudience` distinta ou com `Identity:ForwardedHeaders` inválido. A emissão por papel é verificada em `AccessTokenIssuerTests` e a recusa cruzada com Kestrel real em [`KestrelListenerTests`](../../tests/Ouroboros.IntegrationTests/KestrelListenerTests.cs).

[`TokenKeyRotationTests`](../../tests/Ouroboros.IntegrationTests/TokenKeyRotationTests.cs) exercita coexistência/retirada de pública, janela mínima, encerramento absoluto de HS256 e um consumidor TestServer com cache público, `kid` desconhecido, busca limitada e indisponibilidade do JWKS. Nesse consumidor, o HTTP de chaves é simulado para controlar falhas; não há privada nem acesso ao manifesto. [`AccessTokenIssuerTests`](../../tests/Ouroboros.IntegrationTests/AccessTokenIssuerTests.cs) verifica a emissão real RS256 e o endpoint JWKS da API. [`PrincipalAmbiguityTests`](../../tests/Ouroboros.ArchitectureTests/PrincipalAmbiguityTests.cs) prova a recusa de tipos de principal e papéis contraditórios. As fábricas geram pares RSA efêmeros fora do repositório, sem depender de segredos locais.

[`ClientCredentialsTests`](../../tests/Ouroboros.IntegrationTests/ClientCredentialsTests.cs) cobre formulário e Basic, erros, ausência de sessão/refresh, separação user/client, estado atual, corrida e timeout SQL com PostgreSQL real. O QA da #39 aprovou 95 casos selecionados de emissão e regressões de autenticação e 346 casos de arquitetura; HTTP usa TestServer. Não houve injeção de falha do coletor nessa seleção.

[`ApiClientDeactivationTests`](../../tests/Ouroboros.IntegrationTests/ApiClientDeactivationTests.cs) acrescenta desativação, auditoria, idempotência concorrente, rollback, falha de publicação, revogação na próxima validação e `503`/recuperação na indisponibilidade SQL. O lote final passou em 1030 testes: 677 de integração e 353 de arquitetura, sem falhas ou ignorados. HTTP de Identity usa TestServer; infraestrutura real é utilizada nos testes pertinentes de PostgreSQL, RabbitMQ, worker e arquivos `.eml`.

[Voltar à arquitetura](README.md)
