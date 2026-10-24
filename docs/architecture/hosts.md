# APIs e workers dos serviços

## Responsabilidade dos executáveis

Cada serviço HTTP possui API própria. Identity hospeda endpoints de clientes e administradores e delega aos casos de uso locais. Notifications é worker; saúde operacional não obriga criar API de negócio.

Executáveis compõem apenas seu proprietário, sem carregar outros serviços como bibliotecas ou acessar seus bancos.

## Camadas de proteção do backoffice

Separar rotas, políticas e audiências. Autorizar no serviço, inclusive acesso direto. Administração exige restrição de rede além do papel Admin.

A #82 preserva isolamento administrativo antes de remover os hosts existentes. Usar bindings/listeners e controles de rede apropriados; não expor administração publicamente por conveniência.

## Comandos de operação

create-admin pertence a Identity. Senha fica fora de argumentos e logs. Transferir comando, configurações e códigos de saída antes de remover projetos que os hospedam.

## Configuração comum

Ouroboros.Hosting conserva apenas mecanismos técnicos reutilizados. Não conhece serviços, banco ou regras de negócio. Removê-lo se não houver consumidores; não é agregador central.

## Gateways

A #83 define gateways em etapa posterior. Não bloqueia acesso direto, testes ou implantação inicial dos serviços. Roteamento, timeouts, proxies confiáveis e limites não substituem a autorização do proprietário.

[Voltar à arquitetura](README.md)
