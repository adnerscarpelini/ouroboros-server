# Estado de implementação

As notas de [arquitetura](architecture/README.md) definem a organização adotada. Esta página concentra a disponibilidade dos artefatos, sem repetir avisos nas notas.

| Entrega | Disponibilidade no checkout | Issue |
| --- | --- | --- |
| Login, sessões, cadastro e recuperação | Implementados; controllers ainda em Ouroboros.Api e Ouroboros.Backoffice.Api | #82 substitui executáveis |
| Serviços em src/Services | Organização definida; projetos ainda em src/Modules | #82 e #72 |
| Banco e parâmetros próprios de Identity | Ainda usa Settings e banco comum | #73, #81, #82 |
| Notifications e entrega confiável | Envio local em Identity, sem outbox | #72, #79, #85–#87 |
| Assinatura assimétrica | Checkout utiliza HS256 | #84 |
| Observabilidade e implantação independente | Pendentes | #66–#68, #88–#89 |
| Gateways | Etapa posterior, sem bloquear serviços | #83 |
| Customers e domínio financeiro | Sem funcionalidades | Escopo de negócio a definir |

Transferir controllers, políticas, configuração, create-admin e testes antes de remover os hosts. A remoção não depende de gateways. Ouroboros.Hosting será reduzido ao código técnico útil ou removido se não houver consumidores.

Não excluir dados como efeito da reorganização. Comandos disponíveis no [guia de desenvolvimento](development.md). Acompanhar entregas no [Project](https://github.com/users/adnerscarpelini/projects/1).
