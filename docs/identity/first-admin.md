# Criar o primeiro administrador

## Objetivo

Administradores não se cadastram sozinhos: depois do primeiro, novos administradores entram por convite ([#25](https://github.com/adnerscarpelini/ouroboros-server/issues/25)). O primeiro `Admin` é criado por quem opera a aplicação, com o comando de linha `create-admin` do host de backoffice, no mesmo espírito do bootstrap admin do Keycloak e do `createsuperuser` do Django.

Módulo responsável: Identity. O comando fica no host [`Ouroboros.Backoffice.Api`](../../src/Ouroboros.Backoffice.Api/CommandLine/CreateAdminCommand.cs) e só traduz argumentos, variável de ambiente e resultado; a regra fica no caso de uso `ICreateFirstAdmin` do módulo.

## Pré-requisitos

- O banco local configurado, com a connection string `ConnectionStrings:Ouroboros` nos user-secrets ([Desenvolvimento](../development.md#banco-de-dados-local)).
- As migrations do Identity e do Settings aplicadas. A do Settings traz o parâmetro `Identity:AdminInvitations:AllowedEmailDomain` (valor inicial `adnerscarpelini.com`), que define o domínio aceito para o e-mail do administrador ([Parâmetros do sistema](../architecture/system-parameters.md#valores-iniciais-e-novas-chaves)).

## Como usar

Na raiz do repositório, com a senha na variável de ambiente `OUROBOROS_ADMIN_PASSWORD` e o e-mail no argumento `--email`. Os comandos estão no [guia de desenvolvimento](../development.md#criar-o-primeiro-administrador).

- A senha nunca é aceita como argumento: argumentos ficam no histórico do shell e na lista de processos. No bash, a forma `VAR=valor comando` define a variável só para aquele processo; no PowerShell, a variável fica na sessão até ser removida.
- O comando roda e encerra sem subir o servidor HTTP. Sem o argumento `create-admin`, o host sobe normalmente.
- A senha não aparece na saída nem nos logs, em nenhum caso.

### Códigos de saída

| Código | Quando | Saída |
| --- | --- | --- |
| `0` | Administrador criado. | `Administrador <e-mail> criado.` na saída padrão. |
| `1` | Recusado por uma regra (veja abaixo). | Na saída de erro, a mensagem da regra seguida de `Nada foi criado.` |
| `2` | Uso incorreto: falta `--email` ou a variável `OUROBOROS_ADMIN_PASSWORD` está ausente ou vazia. | Na saída de erro, o uso correto ou o pedido para definir a variável. Nada é criado. |

## Regras

O caso de uso cria um usuário ativo com exatamente um papel, `Admin`, com a senha transformada em hash pelo [hasher do Identity](../architecture/identity.md#hash-e-rehash). As verificações são feitas nesta ordem, e a primeira que falha encerra o comando sem criar nada:

1. **E-mail válido.** O e-mail é normalizado como no domínio (sem espaços nas pontas e em minúsculas) e precisa atender à [regra estrita de e-mail das contas](registration.md#regra-estrita-de-e-mail): parte local ASCII com letras, dígitos e `._%+-`, sem aspas, espaços, nome de exibição ou comentários, e domínio DNS sem ponto final nem literal de IP. E-mails com caracteres fora do ASCII são recusados. `MailAddress` isoladamente aceita e reescreve formas como `"ana"@example.com`, por isso não substitui a regra estrita.
2. **Parâmetro do domínio existe.** O domínio aceito é lido do Settings pelo `ISystemParameters` a cada execução. Se o parâmetro não existir, a mensagem pede para aplicar as migrations do Settings.
3. **Domínio exato.** O domínio do e-mail precisa ser igual ao do parâmetro; subdomínios não são aceitos (com o valor inicial, `@rh.adnerscarpelini.com` é recusado). A mensagem informa o domínio exigido.
4. **Política de senha.** A senha precisa atender à [política de senha](../architecture/password-policy.md); a mensagem informa a regra violada, sem repetir a senha.
5. **Nenhum Admin existente.** Se já existir algum `Admin`, nada é criado: novos administradores entram por convite.
6. **E-mail único.** Se o e-mail já pertencer a outro usuário (de qualquer papel), nada é criado.

As verificações 5 e 6 e a gravação acontecem numa transação que começa com `pg_advisory_xact_lock`: execuções simultâneas do comando são serializadas, e só uma cria o `Admin`; as outras terminam com código `1` e a mensagem de que já existe um administrador.

## Caso de uso exposto

| Tipo | Local | Papel |
| --- | --- | --- |
| [`ICreateFirstAdmin`](../../src/Modules/Ouroboros.Modules.Identity/Contracts/Administrators/ICreateFirstAdmin.cs) | `Contracts/Administrators` (`public`) | `ExecuteAsync(CreateFirstAdminRequest(Email, Password))`, que retorna `CreateFirstAdminResult` com `Outcome`, `Message` (nunca contém a senha) e `AdminId` (o `ExternalId` do Admin criado). |
| `CreateFirstAdminOutcome` | `Contracts/Administrators` | `Created`, `AdminAlreadyExists`, `InvalidEmail`, `EmailDomainNotAllowed`, `EmailAlreadyInUse`, `PasswordRejected` e `ConfigurationMissing` (parâmetro do domínio ausente). |
| [`CreateFirstAdmin`](../../src/Modules/Ouroboros.Modules.Identity/Application/Administrators/CreateFirstAdmin.cs) | `Application/Administrators` (`internal`) | Implementação, registrada como scoped em `AddIdentityModule`. |

O caso de uso é exposto em `Contracts`. A leitura de parâmetros e as dependências disponíveis estão descritas no [estado de implementação](../implementation-status.md#inventário-de-dependências-a-isolar). Não há endpoint HTTP para criar administradores.

## Limitações

- O comando só cria o primeiro administrador. Não há recuperação de acesso caso o único `Admin` perca a senha: a [solicitação de redefinição de senha](password-reset.md#contas-de-backoffice) da API externa (#22) só atende contas `Customer`, e o convite é acompanhado no [estado de implementação](../implementation-status.md).
- O e-mail não é confirmado: o comando confia em quem opera a aplicação.
- A lista de senhas comuns é local; a consulta a senhas vazadas (Pwned Passwords) está fora do escopo.

## Como verificar

Execute a suíte de integração pelo [guia de desenvolvimento](../development.md#testes) (exige Docker).

- [`CreateFirstAdminTests`](../../tests/Ouroboros.IntegrationTests/CreateFirstAdminTests.cs), contra o PostgreSQL real: comando com e-mail em maiúsculas e espaços cria `Admin` ativo e termina com `0`; a senha confere com o hasher e não aparece no hash; segundo Admin não é criado; e-mail já usado por um `Customer` não muda nada; e-mail de outro domínio ou de subdomínio é recusado com o domínio exigido na mensagem; parâmetro do domínio ausente é informado sem exceção; senha de 14 caracteres informa o mínimo de 15; senha acima do limite e senha comum em qualquer combinação de maiúsculas são recusadas; variável ausente termina com `2`; a senha não aparece na saída nem nos logs; duas execuções simultâneas criam um único `Admin`.
- Os testes da política de senha estão em [Política de senha](../architecture/password-policy.md#testes).

A regra estrita de e-mail não tem teste próprio no `create-admin`; os casos recusados estão nos testes da [solicitação de cadastro](registration.md#como-verificar), que usa a mesma regra. A execução concorrente deve criar um único `Admin`; o hash usa o formato V3 (HMAC-SHA512, 210.000 iterações).

## Referências

- [Keycloak: bootstrap admin and recovery](https://www.keycloak.org/server/bootstrap-admin-recovery)
- [Django: `createsuperuser`](https://docs.djangoproject.com/en/5.2/ref/django-admin/)
- [OWASP Authentication Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html)

## Issues relacionadas

- [#11](https://github.com/adnerscarpelini/ouroboros-server/issues/11): criar o primeiro administrador por comando de linha.
- [#20](https://github.com/adnerscarpelini/ouroboros-server/issues/20): regra estrita de e-mail também no `create-admin`.

[Voltar à documentação](../README.md)
