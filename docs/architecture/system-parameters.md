# Parâmetros por serviço e histórico de alterações

## Propriedade e leitura

Cada serviço possui os parâmetros de suas regras. Identity mantém segurança, cadastro, recuperação e convites em seu banco, com leitura local. Não consultar configuração remota a cada login.

Connection strings e chaves privadas são configuração protegida de infraestrutura, não parâmetros administráveis de negócio.

## Gravação e concorrência

Manter chave, valor, descrição, tipo, padrão, sensibilidade e revisão. Validar e gravar valor e histórico com autor e horário na mesma transação. Chave ausente é recusada; revisão desatualizada produz conflito sem escrita parcial.

Reautenticação ocorre no próprio Identity. A entrada administrativa encaminha o comando sem coordenar bancos. Avisos sensíveis usam outbox com chave, autor e data, sem senha ou valores sensíveis. Falha de transporte não altera escrita confirmada.

## Regras de validação

Preservar limites das funcionalidades: domínio permitido, duração, bloqueio e aprovação. Não confiar em valor inválido vindo do banco. Não introduzir cache ou propagação eventual de parâmetros de segurança sem decisão explícita.

## Valores iniciais e novas chaves

Chaves são criadas pelas migrations da funcionalidade proprietária, com descrição e validação. Não há criação/exclusão arbitrária pelo backoffice. Transferência: #81; portas: #73; administração: #34 e #35.

## Testes

Verificar atomicidade, revisão, acesso administrativo, leitura posterior e avisos sem segredos. Banco alheio não participa da operação.

[Voltar à arquitetura](README.md)
