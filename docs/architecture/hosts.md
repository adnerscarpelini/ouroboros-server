# APIs e workers dos serviços

## Responsabilidade dos executáveis

Cada serviço HTTP possui API própria. Identity hospeda endpoints de clientes e administradores e delega aos casos de uso locais. Notifications pode executar como worker; saúde operacional não obriga criar API de negócio.

Executáveis compõem apenas seu proprietário, sem carregar outros serviços como bibliotecas ou acessar seus bancos. A entrega das APIs de Identity é a [#95](https://github.com/adnerscarpelini/ouroboros-server/issues/95); a retirada dos hosts agregadores, a [#96](https://github.com/adnerscarpelini/ouroboros-server/issues/96).

## Camadas de proteção do backoffice

Separar rotas, políticas e audiências. Autorizar no serviço, inclusive acesso direto. Administração exige restrição de rede além do papel Admin.

Com os dois públicos no mesmo processo, a separação deixa de ser ausência de código e passa a ser configuração; por isso cada camada é verificada por teste:

- **Listeners**: o público atende as rotas externas; o administrativo atende somente `api/v{version}/admin/...` e, por padrão, escuta apenas em loopback ou rede interna. Rota administrativa chamada no listener público responde como inexistente.
- **Audiências**: cada canal aceita somente a própria audiência, e o token de um canal é recusado no outro. O login de cada canal aceita somente o papel do canal.
- **Política padrão**: todo endpoint administrativo exige autenticação e papel Admin, mesmo sem política declarada no controller. A regra vem de convenção por prefixo de rota e listener, não da lembrança de cada controller.
- **Cabeçalhos encaminhados**: `X-Forwarded-*` só vale de proxies e redes conhecidos e configurados.

Usar bindings/listeners e controles de rede apropriados; não expor administração publicamente por conveniência. O isolamento deve ser verificado sem gateway.

## Comandos de operação

create-admin pertence a Identity. Senha fica fora de argumentos e logs. Preservar unicidade concorrente do primeiro Admin, validação de domínio e códigos de saída. Execução: [desenvolvimento](../development.md#criar-o-primeiro-administrador).

## Configuração comum

Ouroboros.Hosting conserva apenas mecanismos técnicos reutilizados. Não conhece serviços, banco ou regras de negócio. Removê-lo se não houver consumidores; não é agregador central.

## Gateways

A #83 define gateways em etapa posterior. Não bloqueia acesso direto, testes ou implantação inicial dos serviços. Roteamento, timeouts, proxies confiáveis e limites não substituem a autorização do proprietário.

[Voltar à arquitetura](README.md)
