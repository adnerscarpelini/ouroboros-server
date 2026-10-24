# APIs e workers dos serviços

## Responsabilidade dos executáveis

Cada serviço HTTP possui API própria. Identity hospeda endpoints de clientes e administradores e delega aos casos de uso locais. Notifications pode executar como worker; saúde operacional não obriga criar API de negócio.

Executáveis compõem apenas seu proprietário, sem carregar outros serviços como bibliotecas ou acessar seus bancos.

## Camadas de proteção do backoffice

Separar rotas, políticas e audiências. Autorizar no serviço, inclusive acesso direto. Administração exige restrição de rede além do papel Admin.

Usar bindings/listeners e controles de rede apropriados; não expor administração publicamente por conveniência. O isolamento deve ser verificado sem gateway.

## Comandos de operação

create-admin pertence a Identity. Senha fica fora de argumentos e logs. Preservar unicidade concorrente do primeiro Admin, validação de domínio e códigos de saída. Execução: [desenvolvimento](../development.md#criar-o-primeiro-administrador).

## Configuração comum

Ouroboros.Hosting conserva apenas mecanismos técnicos reutilizados. Não conhece serviços, banco ou regras de negócio. Removê-lo se não houver consumidores; não é agregador central.

## Gateways

A #83 define gateways em etapa posterior. Não bloqueia acesso direto, testes ou implantação inicial dos serviços. Roteamento, timeouts, proxies confiáveis e limites não substituem a autorização do proprietário.

[Voltar à arquitetura](README.md)
