# Criar o primeiro administrador

## Objetivo

Administradores não se cadastram sozinhos: depois do primeiro, novos administradores entram por convite ([#25](https://github.com/adnerscarpelini/ouroboros-server/issues/25)). O primeiro `Admin` é criado por quem opera a aplicação, com o comando de linha `create-admin` da API de Identity, no mesmo espírito do bootstrap admin do Keycloak e do `createsuperuser` do Django.

Serviço responsável: Identity. O comando fica na API [`Ouroboros.Identity.Api`](../../src/Services/Identity/Ouroboros.Identity.Api/CommandLine/CreateAdminCommand.cs) e só traduz argumentos, variável de ambiente e resultado; a regra fica no caso de uso `ICreateFirstAdmin` do serviço.

## Pré-requisitos

- O banco local configurado, com a connection string `ConnectionStrings:Identity` (ou `ConnectionStrings:Ouroboros`, quando a anterior não existe) nos user-secrets ([Desenvolvimento](../development.md#banco-de-dados-local)).
- As migrations de Identity aplicadas, incluindo `OwnIdentityParameters`. O parâmetro local `Identity:AdminInvitations:AllowedEmailDomain` (valor inicial `adnerscarpelini.com`) define o domínio aceito para o e-mail. Para banco legado, execute antes a [transferência de parâmetros](parameters.md#upgrade-de-banco-existente).

## Como usar

Na raiz do repositório, com a senha na variável de ambiente `OUROBOROS_ADMIN_PASSWORD` e o e-mail no argumento `--email`. Os comandos estão no [guia de desenvolvimento](../development.md#criar-o-primeiro-administrador).

- A senha nunca é aceita como argumento: argumentos ficam no histórico do shell e na lista de processos. No bash, a forma `VAR=valor comando` define a variável só para aquele processo; no PowerShell, a variável fica na sessão até ser removida.
- O comando roda e encerra sem subir o servidor HTTP nem abrir os listeners. Sem o argumento `create-admin`, a API sobe normalmente.
- A senha não aparece na saída nem nos logs, em nenhum caso.

### Códigos de saída

| Código | Quando | Saída |
| --- | --- | --- |
| `0` | Administrador criado. | `Administrador <e-mail> criado.` na saída padrão. |
| `1` | Recusado por uma regra (veja abaixo). | Na saída de erro, a mensagem da regra seguida de `Nada foi criado.` |
| `1` | Exceção inesperada ou cancelamento solicitado durante a execução. | Erro inesperado: `Não foi possível concluir o comando. Consulte os diagnósticos correlacionados.`; cancelamento: `Comando cancelado.` |
| `2` | Uso incorreto: falta `--email` ou a variável `OUROBOROS_ADMIN_PASSWORD` está ausente ou vazia. | Na saída de erro, o uso correto ou o pedido para definir a variável. Nada é criado. |

Falha técnica registra `command-failed` com a exceção sanitizada e uma atividade W3C própria, sem senha, conteúdo SQL ou e-mail no diagnóstico. Cancelamento solicitado registra `expected-cancellation` em `Information`. O host é descartado de forma assíncrona antes da saída do processo, para drenar as filas de diagnóstico dentro dos limites do pipeline. A saída funcional de sucesso continua mostrando o e-mail ao operador; ela não é um log de diagnóstico ([política de captura](../architecture/error-responses.md#política-de-captura-de-falhas)).

## Regras

O caso de uso cria um usuário ativo com exatamente um papel, `Admin`, com a senha transformada em hash pelo [hasher do Identity](../architecture/identity.md#hash-e-rehash). As verificações são feitas nesta ordem, e a primeira que falha encerra o comando sem criar nada:

1. **E-mail válido.** O e-mail é normalizado como no domínio (sem espaços nas pontas e em minúsculas) e precisa atender à [regra estrita de e-mail das contas](registration.md#regra-estrita-de-e-mail): parte local ASCII com letras, dígitos e `._%+-`, sem aspas, espaços, nome de exibição ou comentários, e domínio DNS sem ponto final nem literal de IP. E-mails com caracteres fora do ASCII são recusados. `MailAddress` isoladamente aceita e reescreve formas como `"ana"@example.com`, por isso não substitui a regra estrita.
2. **Parâmetro do domínio existe.** O domínio aceito é lido localmente de Identity pelo seu `ISystemParameters` a cada execução. Se o parâmetro não existir, a mensagem pede para aplicar as migrations de Identity.
3. **Domínio exato.** O domínio do e-mail precisa ser igual ao do parâmetro; subdomínios não são aceitos (com o valor inicial, `@rh.adnerscarpelini.com` é recusado). A mensagem informa o domínio exigido.
4. **Política de senha.** A senha precisa atender à [política de senha](../architecture/password-policy.md); a mensagem informa a regra violada, sem repetir a senha.
5. **Nenhum Admin existente.** Se já existir algum `Admin`, nada é criado: novos administradores entram por convite.
6. **E-mail único.** Se o e-mail já pertencer a outro usuário (de qualquer papel), nada é criado.

Após as verificações 1 a 4, a Application calcula o hash e cria o objeto `User`, antes de abrir a transação. A porta de persistência executa as verificações 5 e 6 e a gravação numa transação que começa com o advisory lock global `pg_advisory_xact_lock(hashtext('identity:create-first-admin'))`. A chave é a mesma em todos os processos: execuções simultâneas são serializadas, só uma cria o `Admin` e as outras terminam com código `1` e a mensagem de que já existe um administrador.

## Caso de uso exposto

| Tipo | Local | Papel |
| --- | --- | --- |
| [`ICreateFirstAdmin`](../../src/Services/Identity/Ouroboros.Identity/Contracts/Administrators/ICreateFirstAdmin.cs) | `Contracts/Administrators` (`public`) | `ExecuteAsync(CreateFirstAdminRequest(Email, Password))`, que retorna `CreateFirstAdminResult` com `Outcome`, `Message` (nunca contém a senha) e `AdminId` (o `ExternalId` do Admin criado). |
| `CreateFirstAdminOutcome` | `Contracts/Administrators` | `Created`, `AdminAlreadyExists`, `InvalidEmail`, `EmailDomainNotAllowed`, `EmailAlreadyInUse`, `PasswordRejected` e `ConfigurationMissing` (parâmetro do domínio ausente). |
| [`CreateFirstAdmin`](../../src/Services/Identity/Ouroboros.Identity/Application/Administrators/CreateFirstAdmin.cs) | `Application/Administrators` (`internal`) | Implementação, registrada como scoped em `AddIdentityModule`. |
| [`IFirstAdminStore`](../../src/Services/Identity/Ouroboros.Identity/Application/Administrators/IFirstAdminStore.cs) | `Application/Administrators` (`internal`) | Porta scoped `TryCreateAsync`, que recebe o usuário com hash e devolve `Created`, `AdminAlreadyExists` ou `EmailAlreadyInUse`. |
| [`FirstAdminStore`](../../src/Services/Identity/Ouroboros.Identity/Infrastructure/Persistence/FirstAdminStore.cs) | `Infrastructure/Persistence` (`internal`) | Adaptador scoped que mantém EF, SQL, lock global, consultas e criação na mesma unidade de trabalho de `IdentityDbContext`. |

O caso de uso é exposto em `Contracts`. A leitura de parâmetros e as dependências disponíveis estão descritas no [estado de implementação](../implementation-status.md#inventário-de-dependências-a-isolar). Não há endpoint HTTP para criar administradores.

## Persistência

A [#77](https://github.com/adnerscarpelini/ouroboros-server/issues/77) isolou a persistência sem mudar comando, respostas ou política de senha. `CreateFirstAdmin` valida parâmetros locais e regras, calcula o hash e chama `IFirstAdminStore`; não usa DbContext, EF, SQL ou tipos de Infrastructure. `AddIdentityModule` registra a porta e o adaptador scoped.

`FirstAdminStore.TryCreateAsync` abre a transação, obtém o advisory lock transacional global e só então verifica a ausência de Admin e a disponibilidade do e-mail. Inserção e verificações pertencem ao mesmo commit. Saída de recusa ou falha na escrita não confirma a transação; o descarte desfaz a operação e libera o lock. Não há chamadas externas nessa unidade de trabalho. O domínio permitido vem dos [parâmetros locais de Identity](parameters.md), sem consultar Settings.

## Limitações

- O comando só cria o primeiro administrador. Não há recuperação de acesso caso o único `Admin` perca a senha: a [solicitação de redefinição de senha](password-reset.md#contas-de-backoffice) do listener público (#22) só atende contas `Customer`, e o convite é acompanhado no [estado de implementação](../implementation-status.md).
- O e-mail não é confirmado: o comando confia em quem opera a aplicação.
- A lista de senhas comuns é local; a consulta a senhas vazadas (Pwned Passwords) está fora do escopo.

## Como verificar

Execute a suíte de integração pelo [guia de desenvolvimento](../development.md#testes) (exige Docker).

- [`CreateFirstAdminTests`](../../tests/Ouroboros.IntegrationTests/CreateFirstAdminTests.cs), contra o PostgreSQL real: comando com e-mail em maiúsculas e espaços cria `Admin` ativo e termina com `0`; a senha confere com o hasher e não aparece no hash; segundo Admin não é criado; e-mail já usado por um `Customer` não muda nada; e-mail de outro domínio ou de subdomínio é recusado com o domínio exigido na mensagem; parâmetro do domínio ausente é informado sem exceção; senha de 14 caracteres informa o mínimo de 15; senha acima do limite e senha comum em qualquer combinação de maiúsculas são recusadas; variável ausente termina com `2`; a senha não aparece na saída nem nos logs; duas execuções simultâneas criam um único `Admin`.
- Os testes da política de senha estão em [Política de senha](../architecture/password-policy.md#testes).

Os testes de persistência da #77 no mesmo arquivo também verificam:

- dois processos reais do comando (o executável da API de Identity com `create-admin`) aguardam o advisory lock global, observados no PostgreSQL antes de liberá-lo; depois, saem com códigos `0` e `1` e deixam exatamente um Admin;
- falha por trigger na inserção desfaz a criação, e uma nova execução cria normalmente após remover a falha;
- valor divergente no schema `settings` retido não altera o domínio permitido lido localmente de Identity.

A regra estrita de e-mail não tem teste próprio no `create-admin`; os casos recusados estão nos testes da [solicitação de cadastro](registration.md#como-verificar), que usa a mesma regra. A execução concorrente deve criar um único `Admin`; o hash usa o formato V3 (HMAC-SHA512, 210.000 iterações).

## Referências

- [Keycloak: bootstrap admin and recovery](https://www.keycloak.org/server/bootstrap-admin-recovery)
- [Django: `createsuperuser`](https://docs.djangoproject.com/en/5.2/ref/django-admin/)
- [OWASP Authentication Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html)

## Issues relacionadas

- [#11](https://github.com/adnerscarpelini/ouroboros-server/issues/11): criar o primeiro administrador por comando de linha.
- [#20](https://github.com/adnerscarpelini/ouroboros-server/issues/20): regra estrita de e-mail também no `create-admin`.
- [#77](https://github.com/adnerscarpelini/ouroboros-server/issues/77): porta de persistência do primeiro administrador e serialização entre processos.
- [#95](https://github.com/adnerscarpelini/ouroboros-server/issues/95): o comando passa a ser executado pela API de Identity, com os mesmos argumentos, variável de ambiente e códigos de saída.

[Voltar à documentação](../README.md)
