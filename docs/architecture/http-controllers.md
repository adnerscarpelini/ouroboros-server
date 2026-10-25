# Controllers HTTP nas APIs dos serviços

## Decisão

Controllers pertencem à API do proprietário. Traduzem HTTP para contratos e resultados para respostas, sem EF, SQL ou regras de domínio. O composition root registra apenas dependências locais.

## Organização na API de Identity

Os controllers ficam em [`Ouroboros.Identity.Api/Controllers`](../../src/Services/Identity/Ouroboros.Identity.Api/Controllers), públicos, em uma pasta (e namespace) por público:

| Pasta e namespace | Público | Rota |
| --- | --- | --- |
| `Controllers/External` | Clientes e integrações | `api/v{version:apiVersion}/<service>/...` |
| `Controllers/Admin` | Administradores | `api/v{version:apiVersion}/admin/<service>/...` |

O prefixo da rota, e não a pasta, decide o público do endpoint: a [convenção de rota](hosts.md#rota-do-outro-público-é-rota-inexistente) coloca o endpoint no listener correspondente e exige o papel `Admin` nos administrativos. Por isso um controller novo não precisa declarar política para ser protegido, mas precisa seguir o prefixo do seu público. A biblioteca do serviço (`Ouroboros.Identity`) expõe só casos de uso (`Contracts`) e não declara controllers.

## Como criar um endpoint

1. Definir comportamento e autorização na issue.
2. Criar contrato mínimo e caso de uso no proprietário.
3. Implementar portas externas em Infrastructure.
4. Declarar controller com versão, rota, política e tradução de resultados.
5. Testar contrato, falhas, autorização e persistência pertinente.

DTOs usam ExternalId, nunca entidades ou Id interno. Seguir [ProblemDetails](error-responses.md) e [versionamento](api-versioning.md). Integração com outro serviço usa HTTP ou mensagens, não DI da implementação alheia.

## Autorização

Negar por padrão; anonimato é explícito. Administração exige políticas e restrição de rede. Token de integração não é token de usuário. Proteger chamadas diretas.

Um endpoint de `Controllers/External` exige autenticação, salvo `[AllowAnonymous]`. Um endpoint de `Controllers/Admin` exige autenticação e papel `Admin`, também sem política declarada; `[AllowAnonymous]` explícito (login, renovação e logout) continua valendo. Detalhes em [APIs e workers](hosts.md#camadas-de-proteção-do-backoffice).

## Testes de arquitetura

Verificar descoberta e rotas por serviço, ausência de EF/Infrastructure nos controllers, identificadores públicos e ausência de referências de implementação entre serviços.

Sobre as actions que a API de Identity realmente registra:

- [`ControllerRouteTests`](../../tests/Ouroboros.ArchitectureTests/ControllerRouteTests.cs): cada action está em `Controllers.External` ou `Controllers.Admin`, o serviço existe em `src/Services`, o controller declara `[ApiVersion]` e a rota começa por `api/v{version:apiVersion}/<service>/` (externo) ou `api/v{version:apiVersion}/admin/<service>/` (administrativo).
- [`HostSeparationTests`](../../tests/Ouroboros.ArchitectureTests/HostSeparationTests.cs): todo endpoint de prefixo administrativo é só administrativo e exige `Admin`; os demais são só públicos; os controllers da API são públicos (a descoberta ignora `internal` sem avisar); as bibliotecas de serviço não declaram controllers nem são application parts; a rota do outro público responde como rota inexistente.

[Voltar à arquitetura](README.md)
