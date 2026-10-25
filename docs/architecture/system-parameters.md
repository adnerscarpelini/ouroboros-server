# Parâmetros por serviço e histórico de alterações

## Propriedade e leitura

Cada serviço possui os parâmetros de suas regras. Identity mantém segurança, cadastro, recuperação e convites em seu banco, com leitura local. Não consultar configuração remota a cada login.

Connection strings e chaves privadas são configuração protegida de infraestrutura, não parâmetros administráveis de negócio.

A porta de leitura local recebe chave e cancelamento e retorna o valor textual para interpretação pelo caso de uso. Chave nula/em branco é inválida; chave inexistente produz erro explícito, sem assumir um padrão silencioso. Alterações valem na próxima leitura, sem reinício.

## Gravação e concorrência

Manter chave, valor, descrição, tipo, padrão, sensibilidade e revisão. Validar e gravar valor e histórico com autor e horário na mesma transação. Chave ausente é recusada; revisão desatualizada produz conflito sem escrita parcial.

Reautenticação ocorre no próprio Identity. A entrada administrativa encaminha o comando sem coordenar bancos. Avisos sensíveis usam outbox com chave, autor e data, sem senha ou valores sensíveis. Falha de transporte não altera escrita confirmada.

Valor e descrição podem ser alterados juntos ou separados; nulo mantém o campo vigente. Alteração sem diferença é recusada. Tipo, padrão e sensibilidade são definidos pela funcionalidade, sem edição arbitrária. A revisão começa em 0 e cresce por alteração; `UpdatedAt` não a substitui como controle de concorrência.

O histórico guarda chave, revisão, valores e descrições anteriores/novos, tipo, padrão, sensibilidade, autor (ExternalId) e instante UTC. Na inclusão, anteriores são nulos e autor pode ser nulo quando vier de migration. Cada par (chave, revisão) é único; uma colisão desfaz toda a escrita concorrente. O histórico é cópia ligada pela chave, sem depender de FK para o parâmetro. Valores sensíveis de negócio podem constar do histórico sob autorização; senhas, tokens e chaves privadas nunca são parâmetros.

O fluxo administrativo da #34 exige Admin, `settings:parameters.write` e senha atual. Identity resolve os administradores ativos e grava a intenção de aviso na transação local, sem ciclo com Settings ou consulta de Notifications aos usuários. A #35 exige `settings:parameters.read` e histórico por cursor. Preservar contratos de erro e revisão descritos no [plano](migration-plan.md#administração-e-segurança).

## Regras de validação

Preservar limites das funcionalidades: domínio permitido, duração, bloqueio e aprovação. Não confiar em valor inválido vindo do banco. Não introduzir cache ou propagação eventual de parâmetros de segurança sem decisão explícita.

A chave é obrigatória, com até 200 caracteres, e a descrição não pode ser vazia ou só espaços. Valores recusam caracteres de controle, formato invisível e separadores Unicode de linha/parágrafo.

| Tipo | Valor aceito | Exemplo fictício |
| --- | --- | --- |
| String | Texto respeitando a regra de caracteres | `Bem-vindo` |
| Integer | Inteiro de 64 bits, sinal opcional, sem espaços/separadores | `-10` |
| Boolean | Exatamente true ou false | `true` |
| Email | Minúsculas, sem espaços, parte local ASCII de 1–64 caracteres e domínio DNS válido | `ana@example.com` |
| EmailList | E-mails separados por vírgula, espaços nas pontas de cada item ignorados, sem vazios/duplicatas | `ana@example.com,bia@example.com` |
| Domain | Domínio em minúsculas, sem @, rótulos de letras/dígitos/hífen e terminação alfabética | `example.com` |

O padrão opcional casa com o valor inteiro, envolvido em `\A(?:<padrão>)\z`, com timeout de 1 segundo. A âncora `$` não basta, pois aceita posição anterior a uma quebra de linha final em regex .NET.

## Valores iniciais e novas chaves

Chaves são criadas pelas migrations da funcionalidade proprietária, com descrição e validação. Não há criação/exclusão arbitrária pelo backoffice. Transferência: #81; portas: #73; administração: #34 e #35.

| Chave de Identity | Valor inicial | Limite ou efeito |
| --- | --- | --- |
| Identity:AdminInvitations:AllowedEmailDomain | `adnerscarpelini.com` | Domain sensível; sem subdomínios no create-admin |
| Identity:AdminInvitations:ApproverEmails | `oi@adnerscarpelini.com` | EmailList sensível |
| Identity:Lockout:MaxFailedAttempts | `5` | 1–99 |
| Identity:Lockout:DurationMinutes | `15` | 1–1440 minutos |
| Identity:Registration:TokenLifetimeHours | `24` | 1–168 horas |
| Identity:Registration:CompletionUrl | `http://localhost:3000/cadastro/concluir` | URL http/https sem espaços, query ou fragmento |
| Identity:PasswordReset:TokenLifetimeMinutes | `60` | 1–1440 minutos |
| Identity:PasswordReset:CompletionUrl | `http://localhost:3000/senha/redefinir` | Mesma regra de URL |

O link acrescenta `?token=<token>` à URL. Alterar a validade afeta os próximos tokens; os já emitidos preservam sua expiração. Leituras numéricas conferem os limites mesmo para valores escritos fora da aplicação. Os detalhes estão em [bloqueio](../identity/account-lockout.md#configuração), [cadastro](../identity/registration.md#configuração) e [recuperação](../identity/password-reset.md#configuração).

## Testes

Verificar atomicidade, revisão, acesso administrativo, leitura posterior e avisos sem segredos. Banco alheio não participa da operação.

Referências de cenários: [SystemParametersTests](../../tests/Ouroboros.IntegrationTests/SystemParametersTests.cs) cobre leitura, sementes, histórico, rollback e concorrência; [ParameterRulesTests](../../tests/Ouroboros.IntegrationTests/ParameterRulesTests.cs) cobre tipos, padrão, revisão e alteração sem diferença. Cobertura disponível e funcionalidades pendentes: [estado de implementação](../implementation-status.md).

Contratos internos e unidade de trabalho da persistência: [portas de leitura e escrita de parâmetros](../settings/parameter-ports.md).

[Voltar à arquitetura](README.md)
