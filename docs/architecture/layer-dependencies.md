# Dependências entre camadas e serviços

## Matriz normativa de dependências

| Origem | Dependências permitidas | Restrições |
| --- | --- | --- |
| Domain | BCL e base técnica de domínio necessária | Sem EF, HTTP, mensageria ou outro serviço |
| Contratos | Dados simples e identificadores públicos | Sem entidades, infraestrutura ou transações |
| Application | Domain, contratos próprios e portas | Sem EF, SQL, HttpContext ou implementações de Infrastructure |
| Infrastructure | Application, Domain e bibliotecas dos adaptadores | Sem regras novas de negócio ou banco alheio |
| API / Worker | Contratos próprios, modelos HTTP/mensagem e composição | Sem persistência ou regras nos controllers/consumidores |
| Composition root | Registro do próprio serviço | Não carregar implementação de outro serviço |
| Código técnico compartilhado | Bibliotecas do mecanismo | Sem serviços ou regras de negócio |

Application declara portas; Infrastructure implementa persistência, HTTP, mensageria e emissão JWT. Portas expressam intenção, resultado e conflito; não reproduzem DbSet. Não criar repositório genérico ou Unit of Work adicional sem necessidade.

## Integração entre serviços

Nenhum serviço referencia a implementação de outro. Clientes HTTP e adaptadores de mensagens pertencem à infraestrutura do consumidor e usam contratos versionados. Não compartilhar entidades nem prometer atomicidade local em chamadas de rede.

Identity possui parâmetros e decisões de segurança. Notifications recebe destinatário e conteúdo sem consultar usuários. Gateways não coordenam transações de negócio.

## Transações e concorrência

Credenciais, sessões e tokens permanecem na transação local de Identity. Preservar locks de usuário, sessões ordenadas e tokens, protegendo contra senha antiga, rehash concorrente, reuso e redefinição. Ver [ordem dos locks](identity.md#ordem-dos-locks).

Estado e outbox são atômicos no proprietário. Publicação e entrega não participam da transação. Não manter locks durante chamadas de rede.

Parâmetro, revisão, histórico e intenção de aviso sensível pertencem à mesma escrita local. Falha no transporte não desfaz ou reaplica alteração confirmada.

## Verificação

A #70 protege referências, usos de tipos, ciclos e visibilidade. Exceções ainda existentes são nominais e devem diminuir nas #71 e #73–#78. Inventários de trabalho ficam nas issues; não autorizar namespaces inteiros.

[Voltar à arquitetura](README.md)
