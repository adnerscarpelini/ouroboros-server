# Catálogo de erros

Catálogo das falhas de negócio da API. Cada falha tem um `type` estável, que o cliente usa para identificar o erro em vez do texto da resposta. A decisão e os formatos de erro estão em [Respostas de erro com ProblemDetails](../architecture/error-responses.md).

Erros genéricos (validação `400`, rota inexistente `404`, erro inesperado `500` e outros) usam o `type` padrão do ASP.NET Core e não entram neste catálogo.

## Páginas por módulo

- [Identity](identity.md)

## Formato

- Uma página por módulo: `docs/errors/<module>.md`, com o nome do módulo em minúsculas.
- Uma seção por código. O título da seção é o próprio código, em kebab-case, para que a âncora gerada pelo GitHub seja igual a ele.
- O `type` do erro é `https://github.com/adnerscarpelini/ouroboros-server/blob/main/docs/errors/<module>.md#<codigo>`, o mesmo informado em `Problem(type: ...)` no controller.
- Um código publicado não é renomeado, removido sem aviso nem reaproveitado para outro erro, porque clientes dependem dele.

Cada seção informa:

- **Status HTTP**: o status da resposta.
- **Quando ocorre**: a regra de negócio que produz o erro.
- **Como reagir**: o que o cliente deve fazer (corrigir a entrada, tentar depois, avisar o usuário etc.).

Modelo de seção:

```markdown
## <codigo>

- **Status HTTP**: `409 Conflict`
- **Quando ocorre**: ...
- **Como reagir**: ...
```

## Como adicionar um erro

1. Crie a seção na página do módulo, no formato acima, no mesmo PR que introduz a falha.
2. No controller, traduza a falha com `Problem(type: ..., title: ..., statusCode: ...)` usando o URI da seção.
3. Se o módulo ainda não tiver página, crie-a e ligue-a em [Páginas por módulo](#páginas-por-módulo).

[Voltar à documentação](../README.md)
