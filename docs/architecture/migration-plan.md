# Plano de entregas da arquitetura

## Ordem e responsabilidades

A [#69](https://github.com/adnerscarpelini/ouroboros-server/issues/69) define as fronteiras e dependências. Cada entrega implementa somente sua fatia, preservando contratos e garantias locais. A tabela registra pré-requisitos diretos das entregas arquiteturais; referências explicativas entre issues não acrescentam dependências.

| Entrega | Pré-requisitos diretos | Resultado exigido |
| --- | --- | --- |
| #70 | #69 | Regras automatizadas de dependência e exceções nominais |
| #71 | #69 | Porta local de emissão, sem trocar algoritmo |
| #73 | #69 | Portas de parâmetros com escrita/histórico atômicos |
| #74 | #69, #71 | Persistência de sessões e locks por portas |
| #75 | #69, #79 | Persistência de cadastro e porta de transporte |
| #76 | #69, #74, #79 | Persistência de reset sem quebrar a transação local |
| #77 | #69, #81 | Primeiro Admin com advisory lock e parâmetros locais |
| #78 | #69 | Projeção do usuário autenticado por porta |
| #79 | #69 | Contrato e política de entrega confiável |
| #72 | #69, #79, #93 | Worker de Notifications, adaptador e contrato versionado |
| #80 | #69 | Regressão Linux seguindo projetos da solution |
| #81 | #69, #73 | Parâmetros e histórico sob a propriedade de Identity, no mesmo banco e com validação |
| #92 | #69 | Bancos e roles por serviço, provisionados de forma idempotente na instância compartilhada |
| #93 | #69, #79 | Broker, topologia, envelope versionado do contrato e biblioteca cliente |
| #94 | #69, #80 | Identity em src/Services, sem mudança de comportamento |
| #95 | #69, #94, #80 | APIs externa e administrativa no processo de Identity, com listeners, audiências e política padrão preservados |
| #82 | #69, #81, #92, #80 | Transferência validada do banco de Identity, com rollback e reconciliação |
| #96 | #69, #95, #81 | Retirada dos agregadores e do projeto Settings, revisão do Hosting e remoção do scaffolding sem uso |
| #97 | #82, #96 | Decisão de retenção e desativação do banco original, sem exclusão automática |
| #84 | #69, #71 | Assinatura assimétrica e rotação |
| #85 | #79, #75, #76 | Estado e intenção de e-mail na mesma transação |
| #86 | #85, #93 | Publicador confirmado, recuperável e com recursos limitados |
| #87 | #72, #86, #92, #93 | Processamento persistente, deduplicação e recuperação |
| #28 | #4, #69 | Correlação W3C |
| #66 | #28, #69 | Diagnósticos sanitizados independentes do banco de negócio |
| #67, #68 | #28 e #66 para #67; #66 para #68 | Captura segura e recuperação do pipeline |
| #88 | #28, #66, #87 | Observabilidade de HTTP e mensagens |
| #98 | #69, #72, #82, #92, #93, #95 | Imagens versionadas, Compose, liveness e readiness |
| #89 | #82, #87, #88, #95, #98 | Atualização independente, recuperação e acesso direto protegidos |
| #83 | #95, #89 | Gateways após validação independente |

Uma ordem válida é #69 → (#70, #71, #73, #78, #79, #80, #28, #92) → (#74, #75, #81, #84, #66, #93, #94) → (#72, #76, #77, #82, #95, #67, #68) → (#85, #96, #98) → #86 → #87 → #88 → #89 → (#83, #97). Parênteses agrupam entregas que podem prosseguir depois dos seus próprios pré-requisitos, sem exigir trabalho simultâneo.

#79 antecede #72 e #93 e não depende do consumidor pronto. #93 define broker, topologia e contrato antes de #72, #86 e #87. #72 estabelece processo e adaptador; #87 completa aceitação durável e deduplicação. #75/#76 preservam transporte pela porta local até a integração da #85, sem exigir #87 antes. #81 move os parâmetros dentro do mesmo banco; #82 faz uma única transferência do banco inteiro de Identity, sem estado intermediário com dois bancos. #82, #95 e #98 não dependem de #83; #89 valida APIs diretas antes dos gateways. Assim, não há retorno a uma entrega que dependa da própria sucessora.

## Administração e segurança

- #34 depende de #69, #73, #81 e #87: Identity reautentica o Admin, verifica a política `settings:parameters.write`, resolve destinatários ativos e grava valor, revisão, histórico e intenção de aviso sensível na mesma transação. O comando preserva PUT, `value`/`description` e respostas 400/401/404/409; não há ciclo de dependência entre serviços. Notifications recebe destinatário e conteúdo sem senha nem valores sensíveis.
- #35 depende de #30, #69, #73 e #81: leitura e histórico por cursor no proprietário, com Admin e `settings:parameters.read`. Uma visão administrativa de outros proprietários usa APIs, sem DbContext central.
- #39 depende de #37, #9, #14, #69, #71 e #84: principal de integração distinto, audiência explícita e nenhum refresh token.
- #38 depende de #37, #39, #69, #84 e #87: desativação local e aviso recuperável não se desfazem em falha de transporte. A próxima validação do integrador consulta estado autoritativo; timeout ou indisponibilidade recusam acesso.

Rotas públicas são preservadas quando aplicável; mudança de endereço/versão precisa ser explícita. A #95 transfere controllers, políticas, audiências, rate limiting, configuração, create-admin e testes ao proprietário, separando os públicos por listener e por política padrão verificável; a #96 remove os agregadores e o projeto Settings depois disso, sem excluir dados. Ouroboros.Hosting conserva somente mecanismos técnicos com consumidores reais. A organização não cria serviços vazios de Customers, Accounts ou Transactions nem define regras financeiras por nomes de pastas; cada um só será criado, em `src/Services`, com funcionalidade e fronteira financeira aprovadas.

## Garantias durante cada entrega

Portas de persistência preservam a unidade de trabalho completa: abrir uma porta por operação SQL sem compartilhar a transação local não atende à decisão. EF, SQL e reconhecimento de erros do provedor ficam em Infrastructure. Não manter locks enquanto se chama HTTP, broker ou provedor de e-mail.

| Fluxo | Garantia que a refatoração e extração devem conservar |
| --- | --- |
| Login e reset | Usuário com `FOR NO KEY UPDATE`; revalidar hash e estado sob lock antes de abrir sessão ou trocar senha; rehash não restaura senha antiga |
| Reset, logout-all, refresh/reuso/logout | Reset trava usuário e depois sessões em id crescente; logout-all só sessões ordenadas; refresh/reuso/logout travam sua sessão com `FOR UPDATE` antes dos tokens; quem só trava sessão não pede usuário depois |
| Solicitação de cadastro/reset | Advisory lock por e-mail de cadastro ou conta de reset; substituir tokens pendentes e criar novo token atomicamente |
| Conclusão de cadastro | Consumo condicional de token e usuário único no mesmo commit; conflito desfaz consumo, senha recusada não consome |
| Conclusão de reset | Consumo condicional, senha nova, desbloqueio e revogação dos refresh tokens no mesmo commit |
| Primeiro Admin | Advisory lock transacional global de Identity; no máximo um primeiro Admin entre processos concorrentes |
| Parâmetros | Revisão, autor, valor e histórico atômicos; aviso sensível na mesma escrita, sem rede na transação |
| Outbox | Rollback não publica; após commit, intenção recuperável com identidade estável; payload protegido em repouso e descartado após a publicação confirmada; intenções substituídas são canceladas na mesma transação; duplicatas, expiração e retenção seguem #79 |

Logout mantém a validade do access token até expiração; logout-all não promete encerrar um login concorrente. A ordem detalhada está no [domínio Identity](identity.md#ordem-dos-locks). Saldo e lançamentos que exigem atomicidade pertencem à mesma fronteira financeira, sem transação distribuída.

## Transferência e validação

Antes de trocar persistência: backup recuperável, janela sem escrita, cópia e validação de contagens, identificadores públicos, hashes, sessões, tokens, parâmetros, revisões e histórico. Falha de validação impede a troca. Não excluir origem nem volumes como efeito da reorganização. Rollback após novas escritas exige preservação/reconciliação; voltar a imagem não recupera os dados por si só.

A #92 cria bancos e roles de forma idempotente, inclusive em volume existente, porque os scripts de inicialização da imagem só rodam com o volume vazio. A #82 é a única transferência do banco de Identity; o destino do banco original e a revogação da credencial antiga ficam na #97, sem exclusão automática.

Cada entrega mantém testes de comportamento e concorrência com PostgreSQL real, contratos HTTP/mensagem e proteção de acesso direto. A #89 verifica atualização/reversão de Notifications sem implantar Identity, login durante parada de Notifications e do RabbitMQ, recuperação de intenções, reentrega após falha do publicador ou do consumidor e impacto da parada da instância PostgreSQL compartilhada. Bancos, credenciais, connection strings e migrations são próprios, sem exigir instância por serviço.

Disponibilidade, decisões substituídas e inventário concreto ficam no [estado de implementação](../implementation-status.md); execução no [guia de desenvolvimento](../development.md). Este plano não implementa as entregas listadas.

[Voltar à arquitetura](README.md)
