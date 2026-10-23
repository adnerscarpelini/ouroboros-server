# Política de senha

## Contexto

O primeiro fluxo com senha escolhida por uma pessoa é a [criação do primeiro administrador](../identity/first-admin.md) (#11); cadastro, redefinição, troca de senha e conclusão de convite precisam da mesma regra. Desde a [#21](https://github.com/adnerscarpelini/ouroboros-server/issues/21), a [conclusão do cadastro de cliente](../identity/registration.md#concluir-o-cadastro) é o segundo fluxo a usá-la, e, desde a [#23](https://github.com/adnerscarpelini/ouroboros-server/issues/23), a [redefinição de senha](../identity/password-reset.md#concluir-a-redefinição) é o terceiro. Políticas tradicionais exigem maiúsculas, números e símbolos e limitam o tamanho, o que leva a senhas curtas e previsíveis (`Senha@2026`). O NIST SP 800-63B-4 e a OWASP recomendam o contrário: senhas longas, sem regras de composição, verificadas contra listas de senhas conhecidas.

## Decisão

A política fica em [`Application/Security/PasswordPolicy`](../../src/Modules/Ouroboros.Modules.Identity/Application/Security/PasswordPolicy.cs) (`internal`, no Identity) e segue o NIST SP 800-63B-4:

| Regra | Comportamento |
| --- | --- |
| Normalização | A senha é normalizada em NFKC antes de qualquer verificação. |
| Tamanho | De 15 a 128 caracteres, contados em code points (não em unidades UTF-16) depois da normalização: um emoji conta 1; a ligadura `ﬁ` vira `fi` e conta 2. |
| Acima do limite | Recusada, nunca truncada. |
| Composição | Nenhuma regra: espaços e qualquer caractere Unicode são aceitos, então frases como "café coração e pão de queijo" valem. |
| Senhas comuns | Recusada se estiver na lista local, comparada sem diferenciar maiúsculas (após NFKC). |
| Contexto | Recusada se contiver a palavra `ouroboros` ou a parte local do e-mail, quando ela tem 4 ou mais caracteres (sem diferenciar maiúsculas, após NFKC; caracteres fullwidth como `ＯＵＲＯＢＯＲＯＳ` também são encontrados). |

`PasswordPolicy.Validate(password, email)` devolve a mensagem da primeira regra violada, ou `null` quando a senha é aceita. As mensagens nunca repetem a senha.

### Lista de senhas comuns

A lista é o arquivo [`Application/Security/common-passwords.txt`](../../src/Modules/Ouroboros.Modules.Identity/Application/Security/common-passwords.txt), embutido no assembly como recurso e carregado uma vez, na primeira validação. Uma entrada por linha; linhas vazias e iniciadas por `#` são ignoradas. Só fazem sentido entradas com 15 ou mais caracteres, porque as menores já são recusadas pelo tamanho. Para incluir uma senha, acrescente uma linha ao arquivo; não é preciso alterar código.

### NFKC no hasher

O [`AspNetCorePasswordHasher`](../../src/Modules/Ouroboros.Modules.Identity/Infrastructure/Security/AspNetCorePasswordHasher.cs) aplica a mesma normalização (`PasswordPolicy.Normalize`) antes de gerar e de verificar o hash. Assim, a mesma senha digitada com acento composto (`ç` como um code point) ou decomposto (`c` mais cedilha combinante), como acontece entre teclados e sistemas operacionais diferentes, confere com o mesmo hash. Senhas ASCII não mudam com a normalização, então os hashes gravados antes desta decisão continuam válidos ([Domínio de identidade](identity.md#hash-e-rehash)).

## Alternativas descartadas

- **Regras de composição** (maiúscula, número, símbolo) e **troca periódica obrigatória**: o NIST as desaconselha, porque produzem senhas previsíveis sem aumentar a segurança.
- **Mínimo de 8 caracteres**, o mínimo do NIST para senhas usadas com segundo fator: ainda não há segundo fator, e o NIST pede 15 para senha como único fator.
- **Truncar senhas longas**: o usuário acharia que a senha inteira vale; a recusa explícita evita a surpresa.
- **Contar o tamanho em `string.Length`** (unidades UTF-16): emojis e outros caracteres fora do plano básico contariam 2.
- **Consultar a Pwned Passwords** (k-anonymity): fora do escopo da #11; exigiria chamada externa, tratamento de indisponibilidade e configuração.

## Consequências

- Todo fluxo que receber uma senha nova (cadastro, redefinição, troca, convite) chama `PasswordPolicy.Validate` antes de `IPasswordHasher.Hash`; nenhum deles define regras próprias.
- Hoje usam a política o [`create-admin`](../identity/first-admin.md#regras), que mostra a regra violada com o código de saída `1`, e a [conclusão do cadastro de cliente](../identity/registration.md#senha-recusada-400) (#21), que a devolve no `detail` do erro [`password-rejected`](../errors/identity.md#password-rejected), com o e-mail gravado no token de cadastro como contexto, e a [redefinição de senha](../identity/password-reset.md#senha-recusada-400) (#23), que a devolve da mesma forma, com o e-mail da conta como contexto. Na API, a regra só é mostrada a quem apresenta um token válido; com token inválido, a resposta é a de token inválido, qualquer que seja a senha.
- A redefinição de senha acrescenta uma regra fora da política: a senha nova não pode ser igual à atual (`A nova senha não pode ser igual à atual.`, no mesmo `password-rejected`). A regra fica no caso de uso, porque depende do hash gravado, e não na `PasswordPolicy`, que não conhece o usuário.
- A lista local é pequena e mantida à mão; não cobre senhas vazadas em geral.
- A política fica no Identity; se outro módulo precisar dela, a exposição exigirá um contrato em `Contracts`.

## Testes

Em [`tests/Ouroboros.IntegrationTests`](../../tests/Ouroboros.IntegrationTests/Ouroboros.IntegrationTests.csproj), sem banco, [`PasswordPolicyTests`](../../tests/Ouroboros.IntegrationTests/PasswordPolicyTests.cs): limites de 14, 15, 128 e 129 caracteres, inclusive com acentos e emojis; tamanho contado depois do NFKC; nome do sistema encontrado em caracteres fullwidth; senhas comuns em qualquer combinação de maiúsculas; frase com espaços e acentos aceita; parte local do e-mail recusada só com 4 ou mais caracteres; acento composto e decomposto conferem com o mesmo hash. Os testes do uso da política pelo comando estão em [Criar o primeiro administrador](../identity/first-admin.md#como-verificar); os da conclusão do cadastro (senha curta, comum, com a parte local do e-mail e ausente), em [Cadastrar cliente por e-mail](../identity/registration.md#como-verificar); e os da redefinição de senha (senha curta, comum, igual à atual e ausente), em [Redefinir a senha por e-mail](../identity/password-reset.md#como-verificar).

## Referências

- [NIST SP 800-63B-4: Authentication and Authenticator Management](https://pages.nist.gov/800-63-4/sp800-63b.html)
- [OWASP Authentication Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html)

## Origem

- [#11](https://github.com/adnerscarpelini/ouroboros-server/issues/11): política de senha introduzida com a criação do primeiro administrador.
- [#21](https://github.com/adnerscarpelini/ouroboros-server/issues/21): uso da política na conclusão do cadastro de cliente, com o erro `password-rejected`.
- [#23](https://github.com/adnerscarpelini/ouroboros-server/issues/23): uso da política na redefinição de senha, com a recusa de senha igual à atual.

[Voltar à arquitetura](README.md)
