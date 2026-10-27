# Trocar a própria senha

## Contrato

Identity expõe `PUT /api/v1/identity/me/password` somente no listener público, para o cliente autenticado. Envie um access token Bearer válido da audiência pública, com `sub` e `sid` válidos. O usuário deve existir, estar ativo, ter papel `Customer` no banco e não estar bloqueado; a sessão indicada pelo `sid` deve pertencer a esse usuário. Não existe rota administrativa equivalente nesta entrega.

```http
PUT /api/v1/identity/me/password
Authorization: Bearer <access-token>
Content-Type: application/json

{
  "currentPassword": "uma frase antiga longa e exclusiva",
  "newPassword": "outra frase nova longa e exclusiva"
}
```

Ambos os campos são obrigatórios e aceitam no máximo 1024 caracteres no corpo HTTP. A senha nova também deve satisfazer a [política de senha](../architecture/password-policy.md), usando o e-mail da conta como contexto, e ser diferente da atual, inclusive após a normalização aplicada pelo hasher. A conta é identificada pelo `sub`; o corpo não recebe identificador de usuário.

| Resposta | Significado |
| --- | --- |
| `204 No Content` | Senha trocada, sem corpo e sem novos tokens. |
| `400 Bad Request` | Corpo inválido ou senha nova recusada. O erro `password-rejected` informa a regra no `detail`, incluindo `A nova senha não pode ser igual à atual.`; a senha só é avaliada após conferir a credencial atual. |
| `401 Unauthorized` | Autenticação inválida, usuário inexistente/inativo/bloqueado, papel incompatível, sessão de outro usuário ou senha atual incorreta. As recusas do caso de uso usam `invalid-credentials`, sem revelar o motivo. |
| `429 Too Many Requests` | Limite por IP excedido, com `Retry-After`, antes do processamento de credenciais. |

A senha atual incorreta incrementa a contagem do [bloqueio de conta](account-lockout.md), usando os parâmetros de Identity. Essa gravação ocorre fora da transação da troca e sobrevive à recusa. Uma senha nova recusada não conta como falha de autenticação e não altera senha nem sessões.

## Sessões e atomicidade

A troca confirma em uma única transação local a senha nova, o contador de falhas zerado, a remoção do bloqueio e a revogação dos refresh tokens ainda não revogados das outras sessões do usuário. Preserva os refresh tokens da sessão do `sid` atual e as credenciais e sessões de terceiros. Nenhum e-mail é enviado.

Os access tokens já emitidos continuam válidos até expirar, conforme o [contrato de logout](logout.md). Revogar refresh tokens impede novas renovações das outras sessões; não invalida imediatamente esses access tokens. O vínculo do `sid` é conferido pela existência da sessão do usuário, sem exigir refresh token ativo nela.

A implementação de [`ChangePassword`](../../src/Services/Identity/Ouroboros.Identity/Application/Users/ChangePassword.cs), exposta por [`IChangePassword`](../../src/Services/Identity/Ouroboros.Identity/Contracts/Users/IChangePassword.cs), usa a porta `ISessionStore` e o adaptador [`SessionStore`](../../src/Services/Identity/Ouroboros.Identity/Infrastructure/Persistence/SessionStore.cs). Verificação e geração de hashes precedem a transação. Dentro dela, trava primeiro o usuário, relê hash, atividade e bloqueio, trava as sessões em ordem de id e confirma a propriedade do `sid` antes da escrita. Uma mudança concorrente no hash impede confirmar uma verificação antiga; entre duas trocas concorrentes com a mesma senha anterior, somente uma confirma. Falha ao revogar tokens reverte também a senha e o reset do bloqueio.

## Configuração e verificação

A política obrigatória `identity-password-change` em `Identity:RateLimiting:Policies` aceita inicialmente 10 requisições por minuto por IP, com janela fixa e sem fila. Requisições recusadas também contam. Veja [rate limiting](auth-rate-limiting.md) para configuração, validação na subida e resposta `429`.

Os cenários estão em [`ChangePasswordTests`](../../tests/Ouroboros.IntegrationTests/ChangePasswordTests.cs). Com os pré-requisitos de [desenvolvimento](../development.md), execute:

```powershell
dotnet test --project tests/Ouroboros.IntegrationTests -- --filter-class "*ChangePasswordTests"
```

O QA independente desta entrega aprovou 11/11 casos em PostgreSQL real: troca e preservação da sessão atual, falha de senha e bloqueio, senha nova curta/igual, ausência de token, rollback por falha de revogação, concorrência, isolamento de terceiros, audiência/papel e recusa da 11ª requisição. Essa evidência é do filtro indicado, não da suíte completa de integração.

Origem: [#24](https://github.com/adnerscarpelini/ouroboros-server/issues/24).

[Voltar ao índice](../README.md)
