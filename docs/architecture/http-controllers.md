# Controllers HTTP nas APIs dos serviços

## Decisão

Controllers pertencem à API do proprietário. Traduzem HTTP para contratos e resultados para respostas, sem EF, SQL ou regras de domínio. O composition root registra apenas dependências locais.

## Como criar um endpoint

1. Definir comportamento e autorização na issue.
2. Criar contrato mínimo e caso de uso no proprietário.
3. Implementar portas externas em Infrastructure.
4. Declarar controller com versão, rota, política e tradução de resultados.
5. Testar contrato, falhas, autorização e persistência pertinente.

DTOs usam ExternalId, nunca entidades ou Id interno. Seguir [ProblemDetails](error-responses.md) e [versionamento](api-versioning.md). Integração com outro serviço usa HTTP ou mensagens, não DI da implementação alheia.

## Autorização

Negar por padrão; anonimato é explícito. Administração exige políticas e restrição de rede. Token de integração não é token de usuário. Proteger chamadas diretas.

## Testes de arquitetura

Verificar descoberta e rotas por serviço, ausência de EF/Infrastructure nos controllers, identificadores públicos e ausência de referências de implementação entre serviços.

[Voltar à arquitetura](README.md)
