# Dependências entre camadas e serviços

## Matriz normativa de dependências

| Origem | Dependências permitidas | Restrições |
| --- | --- | --- |
| Domain | BCL e base técnica de domínio necessária | Sem EF, HTTP, mensageria ou outro serviço |
| Contratos | BCL, dados simples e identificadores públicos | Sem entidades, infraestrutura, transações ou contratos internos de outro serviço |
| Application | Domain, contratos próprios, portas e mecanismos técnicos necessários (relógio, logging) | Sem EF, SQL, HttpContext ou implementações de Infrastructure |
| Infrastructure | Application, Domain e bibliotecas dos adaptadores | Sem regras novas de negócio ou banco alheio |
| API / Worker | Contratos próprios, modelos HTTP/mensagem e composição | Controllers/consumidores chamam casos de uso, sem persistência ou regras de negócio |
| Composition root | Application e Infrastructure do próprio serviço, configuração e DI | Registro apenas; não carregar implementação de outro serviço |
| Código técnico compartilhado | Bibliotecas do mecanismo | Sem serviços ou regras de negócio |

Application declara portas; Infrastructure implementa persistência, HTTP, mensageria e emissão JWT. Portas expressam intenção, resultado e conflito; não reproduzem DbSet. Não criar repositório genérico ou Unit of Work adicional sem necessidade.

## Integração entre serviços

Nenhum serviço referencia a implementação de outro. Clientes HTTP e adaptadores de mensagens pertencem à infraestrutura do consumidor e usam contratos versionados. Não compartilhar entidades nem prometer atomicidade local em chamadas de rede.

Contratos de integração são schemas HTTP/mensagem independentes de assemblies de implementação. Uma interface de DI em Contracts é fronteira interna do proprietário, não uma chamada remota. BuildingBlocks não é atalho entre proprietários; Domain pode usar sua base de entidade, mas não seus tipos de persistência, mesmo no mesmo assembly.

Identity possui parâmetros e decisões de segurança. Notifications recebe destinatário e conteúdo sem consultar usuários. Gateways não coordenam transações de negócio.

## Transações e concorrência

Credenciais, sessões e tokens permanecem na transação local de Identity. Preservar locks de usuário, sessões ordenadas e tokens, protegendo contra senha antiga, rehash concorrente, reuso e redefinição. Ver [ordem dos locks](identity.md#ordem-dos-locks).

Estado e outbox são atômicos no proprietário. Publicação e entrega não participam da transação. Não manter locks durante chamadas de rede.

Parâmetro, revisão, histórico e intenção de aviso sensível pertencem à mesma escrita local. Falha no transporte não desfaz ou reaplica alteração confirmada.

## Verificação

A [#70](https://github.com/adnerscarpelini/ouroboros-server/issues/70) verifica referências de assemblies e usos de tipos, inclusive corpos de métodos, ciclos e visibilidade. Exceções são nominais, com tipo, dependência proibida e issue de remoção; não autorizar namespaces inteiros. Nova violação falha, e a remoção de uma dependência exige retirar sua exceção. O [inventário do checkout](../implementation-status.md#inventário-de-dependências-a-isolar) identifica as fatias a corrigir; o [plano de entregas](migration-plan.md) estabelece sua ordem.

[Voltar à arquitetura](README.md)
