# Autenticação e autorização entre serviços

## Emissão do access token

Identity possui credenciais, sessões e emissão. A arquitetura usa assinatura assimétrica: chave privada exclusiva no emissor, chaves públicas identificadas nos consumidores. Rotação define janela e retirada de chaves sem implantação conjunta. A #84 define e testa a janela de compatibilidade e o encerramento da aceitação de HS256. Disponibilidade no [estado de implementação](../implementation-status.md).

## Validação do token

Cada serviço valida assinatura, issuer, audience, expiração e principal. Usuário, integrador e serviço não são intercambiáveis. Não distribuir chave privada aos consumidores.

Validação obrigatória indisponível não concede acesso por fallback. Chaves e tokens não aparecem em logs.

### Audiências por canal

A API de Identity atende dois públicos com audiências distintas, na seção `Authentication:Jwt`:

| Chave | Valor inicial | Papel | Esquema de autenticação |
| --- | --- | --- | --- |
| `Audience` | `ouroboros-api` | `Customer` | `Bearer` |
| `AdminAudience` | `ouroboros-backoffice` | `Admin` | `Bearer.Admin` |

- **Emissão.** O emissor escolhe a audiência pelo papel do usuário: `Customer` recebe `Audience`; `Admin`, `AdminAudience`.
- **Validação.** Os dois esquemas JWT compartilham emissor, chave e algoritmo e diferem na audiência aceita. Um esquema de política escolhe `Bearer.Admin` quando a requisição chegou ao listener administrativo e `Bearer` nos demais casos; o listener vem da conexão ([Listeners](hosts.md#listeners)), não de cabeçalho.
- **Recusa cruzada.** O token de um canal usado no outro é recusado com o `401` genérico (`WWW-Authenticate: Bearer`, sem motivo), mesmo com papel adequado ao destino.
- **Configuração.** Na API, `AdminAudience` é obrigatória e deve ser diferente de `Audience`; sem isso, os dois canais emitiriam e aceitariam a mesma audiência e a separação dependeria só dos papéis, e o processo não sobe. Na biblioteca de Identity ela é opcional: ausente, vale `Audience`; presente, não pode ser igual a ela.
- **Login e renovação.** Cada canal só aceita o papel do canal ([login](../identity/login.md)).

## Autorização

Negar por padrão; anonimato é explícito. Cada operação exige capacidade e público adequados no serviço, inclusive acesso direto. Administração exige papel e restrição de rede.

Na API de Identity, o endpoint exige autenticação salvo `[AllowAnonymous]`, e toda rota `api/v{version}/admin/...` exige também o papel `Admin`, aplicado por convenção de rota mesmo sem política declarada no controller ([Rota por público](hosts.md#rota-do-outro-público-é-rota-inexistente)). Customer ou chamada sem token em endpoint administrativo é recusado (`403` ou `401`).

### Políticas por capacidade

Preservar políticas das funcionalidades. Identificar usuário por ExternalId e sessão por sid. Revalidar estado autoritativo quando exigido. Rate limiting considera réplicas e headers somente de proxies confiáveis (`Identity:ForwardedHeaders`, em [Rate limiting e cabeçalhos encaminhados](hosts.md#rate-limiting-e-cabeçalhos-encaminhados)).

Access tokens carregam `sub`, `role`, `sid`, `jti`, `iat`, `nbf` e `exp`. O claim público é o ExternalId, nunca a chave sequencial do banco. Papéis de usuário são Customer ou Admin; políticas são nomeadas por capacidade no proprietário. Falha de autenticação responde 401 genérico com `WWW-Authenticate: Bearer`; falta de permissão responde 403. Ambos usam [ProblemDetails](error-responses.md), sem explicar conteúdo ou motivo de recusa de token. A autorização não revela rotas a chamadas sem autenticação.

## Sessões e revogação

Refresh token é opaco, rotacionado e armazenado por hash. Reuso, logout e redefinição preservam invariantes locais. Logout mantém access tokens válidos até expiração conforme contrato.

A #38 exige recusar integrador desativado na próxima validação: consultar estado autoritativo e recusar quando não for possível validar. Cache/eventos com atraso não garantem essa regra.

## Testes

Verificar rotação, issuer/audience, principais, públicos, concorrência e indisponibilidade. A porta da #71 não muda algoritmo por si só.

Em [`AuthorizationTests`](../../tests/Ouroboros.ArchitectureTests/AuthorizationTests.cs), sobre a API de Identity com controllers de teste: endpoint sem anotação exige token; tokens inválidos (`alg=none`, outra chave, expirado, outro emissor, audiência do outro canal, HS384, sem `exp`, `nbf` futuro, expirado além da tolerância de 30 s) recebem `401`; token vencido dentro da tolerância é aceito; a resposta de recusa não explica o motivo; Customer recebe `403` em qualquer endpoint administrativo, com ou sem política declarada; Admin é autorizado; o token de cada audiência é recusado no listener do outro canal; a API não sobe sem chave de assinatura válida, sem `AdminAudience` distinta ou com `Identity:ForwardedHeaders` inválido. A emissão por papel é verificada em `AccessTokenIssuerTests` e a recusa cruzada com Kestrel real em [`KestrelListenerTests`](../../tests/Ouroboros.IntegrationTests/KestrelListenerTests.cs).

[Voltar à arquitetura](README.md)
