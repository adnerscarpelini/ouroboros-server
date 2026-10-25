# Persistência do ciclo de sessões por portas

## Responsabilidades

Identity mantém credenciais, sessões e tokens na mesma unidade de trabalho local. `Login`, `AccountLockout`, `RefreshSession`, `Logout` e `LogoutAll` orquestram as regras em Application, sem depender de DbContext, EF, SQL ou tipos de Infrastructure.

| Tipo | Responsabilidade |
| --- | --- |
| [`ISessionStore`](../../src/Services/Identity/Ouroboros.Identity/Application/Sessions/ISessionStore.cs) | Porta interna de leitura, escrita e coordenação dos locks do ciclo de sessões. Retorna entidades e projeções internas, sem expor `IQueryable` ou contratos do provedor. |
| `ISessionTransaction` | Porta interna, no mesmo arquivo, para commit, rollback e descarte da transação completa. |
| [`SessionStore`](../../src/Services/Identity/Ouroboros.Identity/Infrastructure/Persistence/SessionStore.cs) | Adaptador scoped com o mesmo `IdentityDbContext` da requisição. Implementa consultas, SQL, escritas e transação EF; delega os locks aos métodos do contexto também usados pelo reset. |

`AddIdentityModule` registra a porta e o adaptador. Não há configuração adicional, alteração de schema ou migration nesta refatoração. Os contratos HTTP, as regras de autenticação e a emissão pela porta `IAccessTokenIssuer` permanecem os mesmos.

## Unidade de trabalho e locks

As operações entre `BeginTransactionAsync` e o commit ou descarte participam da mesma transação aberta pelo caso de uso. As leituras iniciais e o incremento atômico de falha de senha ocorrem fora dessa transação explícita. `SaveChangesAsync` grava dentro dela, e o commit confirma toda a unidade; descartar sem commit desfaz as escritas. Não há commit por operação SQL nem outro DbContext por método. Falhas inesperadas continuam chegando ao tratamento de erros HTTP existente.

| Fluxo | Garantia preservada |
| --- | --- |
| Login | Verifica a senha antes da transação; trava o usuário com `FOR NO KEY UPDATE` e relê hash, estado ativo e bloqueio do banco, sem reutilizar o estado rastreado antes do lock. Só então grava rehash, contador zerado, sessão e primeiro refresh token no mesmo commit. |
| Falha de senha | Um único `UPDATE ... RETURNING` incrementa o contador e aplica o bloqueio atomicamente. `AccountLockout` interpreta o resultado e registra o aviso, sem SQL em Application. |
| Refresh | Trava a sessão com `FOR UPDATE` antes de consumir o token por escrita condicional e inserir o sucessor na mesma transação. Erro na inserção desfaz também o consumo. |
| Reuso | Revoga os tokens ainda não revogados da sessão sob o mesmo lock. Quando a renovação perde a escrita condicional, desfaz a tentativa antes de verificar a revogação e abrir a transação própria de reuso; essa revogação não é desfeita pelo rollback anterior. |
| Logout | Trava a sessão antes de revogar o token ativo informado; token expirado ou já rotacionado mantém a resposta idempotente e não revoga seu sucessor. |
| Logout-all | Trava as sessões do usuário em ordem crescente de id antes de revogar seus tokens ativos. Não pede lock do usuário. |

Quem trava usuário e sessões segue a ordem usuário → sessões → tokens; quem trava apenas sessões nunca pede o lock do usuário depois. O SQL dos locks permanece em [`IdentityDbContext`](../../src/Services/Identity/Ouroboros.Identity/Infrastructure/Persistence/IdentityDbContext.cs), compartilhado com a [redefinição de senha](password-reset.md). A troca de senha e o login continuam serializados: um login com hash antigo não abre sessão após a troca nem restaura a senha antiga por rehash. A [ordem dos locks](../architecture/identity.md#ordem-dos-locks) explica a compatibilidade com as FKs.

Não há chamada HTTP, ao broker ou ao provedor de e-mail enquanto os locks estão mantidos. A emissão local do access token ocorre após o commit. Logout e logout-all preservam a validade dos access tokens já emitidos até sua expiração. Logout-all não promete encerrar um login concorrente cuja sessão ainda não esteja confirmada.

## Como verificar

Com SDK e Docker disponíveis, execute na raiz, conforme o [guia de desenvolvimento](../development.md#testes):

```powershell
dotnet build Ouroboros.slnx -c Release --no-restore
dotnet test --solution Ouroboros.slnx -c Release --no-restore
dotnet format Ouroboros.slnx --verify-no-changes --no-restore
```

[`SessionPersistenceTests`](../../tests/Ouroboros.IntegrationTests/SessionPersistenceTests.cs) injeta erros reais de escrita por triggers no PostgreSQL de teste e verifica rollback e nova tentativa de login, refresh, logout, logout-all e reuso. Também força espera no lock de sessão entre rotação/logout-all e logout/refresh, confere acesso com JWT após logout/logout-all e confirma que logout-all não espera o usuário nem encerra uma sessão ainda não confirmada.

As regressões de [`AccountLockoutTests`](../../tests/Ouroboros.IntegrationTests/AccountLockoutTests.cs), [`RefreshSessionTests`](../../tests/Ouroboros.IntegrationTests/RefreshSessionTests.cs), [`RefreshTokenReuseTests`](../../tests/Ouroboros.IntegrationTests/RefreshTokenReuseTests.cs) e [`PasswordResetRaceTests`](../../tests/Ouroboros.IntegrationTests/PasswordResetRaceTests.cs) cobrem contagem concorrente, rotação/reuso e corridas de login/rehash com reset. Os testes de arquitetura mantêm removidas as exceções de dependência desta fatia da #70.

## Referências

- [#74 — Isolar a persistência do ciclo de sessões](https://github.com/adnerscarpelini/ouroboros-server/issues/74).
- [Login](login.md), [bloqueio](account-lockout.md), [refresh](refresh-session.md), [reuso](refresh-token-reuse.md), [logout](logout.md) e [logout-all](logout-all.md).
- [Estado de implementação](../implementation-status.md) e [plano de entregas](../architecture/migration-plan.md).

[Voltar à documentação](../README.md)
