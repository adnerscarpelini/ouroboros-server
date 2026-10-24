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

A [#70](https://github.com/adnerscarpelini/ouroboros-server/issues/70) automatiza a matriz acima em [`tests/Ouroboros.ArchitectureTests/Dependencies`](../../tests/Ouroboros.ArchitectureTests/Dependencies). A suíte roda sem Docker; os comandos ficam no [guia de desenvolvimento](../development.md#testes).

### Descoberta

Os projetos vêm de `src/**/*.csproj`, sem lista de hosts ou de módulos. Cada um é classificado como serviço (`src/Services/<Serviço>/…` ou, no legado, `src/Modules/Ouroboros.Modules.<Serviço>`), executável (SDK Web/Worker ou `OutputType` Exe) ou compartilhado (os demais, como `Ouroboros.BuildingBlocks` e `Ouroboros.Hosting`). O projeto de testes compila todo projeto de `src` e registra seus assemblies em `architecture-assemblies.txt`, ao lado dos testes; por isso um serviço, API ou worker novo, inclusive Notifications, é analisado sem editar os testes. Projeto de `src` fora de `Ouroboros.slnx`, entrada da solution sem projeto e `ProjectReference` para projeto inexistente falham, o que cobre a ausência de referências aos executáveis retirados pela [#96](https://github.com/adnerscarpelini/ouroboros-server/issues/96).

### Regras de projeto

Aplicadas às `ProjectReference`: `project.cycle` e `project.service-cycle` (ciclos entre projetos e entre serviços), `project.building-blocks-leaf` (BuildingBlocks não referencia projeto algum de `src`), `project.shared-depends-on-service` (compartilhado não referencia serviço nem executável), `project.service-depends-on-executable`, `project.executable-depends-on-executable`, `project.cross-service` (serviço não referencia projeto de outro serviço) e `project.executable-foreign-service` (executável só referencia projetos do próprio serviço; um agregador sem serviço próprio viola a regra para cada serviço que referencia).

### Regras de tipo

O scanner lê metadados e IL com `System.Reflection.Metadata`. Para cada tipo de nível mais externo (tipos aninhados, closures e state machines contam como o tipo que os contém), registra todo tipo usado em base, interfaces, atributos, campos, propriedades, eventos, assinaturas, variáveis locais, cláusulas `catch` e operandos de tipo, método e campo dos corpos de métodos. A camada do tipo vem do namespace `<assembly>.<Camada>` (`Domain`, `Contracts`, `Application`, `Infrastructure`; no código compartilhado, `Persistence` conta como Infrastructure). As regras são:

- `layer.domain`: Domain usa apenas o próprio Domain, a base técnica de domínio compartilhada (`Ouroboros.BuildingBlocks.Domain`) e a BCL, exceto `System.Net.Http` e `System.Data`.
- `layer.contracts`: Contracts usa apenas os próprios Contracts, contratos compartilhados e a BCL.
- `layer.application`: Application não usa tipos de Infrastructure, próprios ou da persistência compartilhada, nem EF Core, Npgsql, `System.Data`, `System.Net.Http` ou ASP.NET Core Http/Mvc.
- `type.cross-service`, `type.depends-on-executable`, `type.shared-depends-on-service` e `type.service-cycle`: nenhum tipo de serviço usa tipos de outro serviço (inclusive seus Contracts) ou de um executável, e código compartilhado não usa tipos de serviço nem de executável. Contratos de integração só podem ser compartilhados em projeto compartilhado, sujeito à regra `layer.contracts`.

### Visibilidade

`ModuleVisibilityTests` descobre os serviços pela árvore de `src`. A fronteira pública de cada serviço é `Contracts` e `DependencyInjection`, com a exceção das migrations geradas pelo EF, e nenhum serviço concede `InternalsVisibleTo` a outro projeto de `src`.

### Lista de transição

As violações legadas ficam em [`KnownViolations.cs`](../../tests/Ouroboros.ArchitectureTests/Dependencies/KnownViolations.cs). Cada linha nomeia a regra, o tipo ou projeto, a dependência proibida e a issue que a remove; não há exceção por namespace ou por tipo inteiro. Para bibliotecas externas, a dependência nomeada é o assembly (por exemplo, `Microsoft.EntityFrameworkCore`); para tipos de `src`, o nome completo.

- Nova violação falha, inclusive em tipo já listado, se a dependência não estiver nomeada.
- Dependência removida exige apagar a linha: a suíte acusa a exceção obsoleta.
- A lista só diminui, nas issues que nela constam. Ampliá-la para fazer a suíte passar contraria a regra; corrija o código.

O [inventário do checkout](../implementation-status.md#inventário-de-dependências-a-isolar) identifica as fatias a corrigir; o [plano de entregas](migration-plan.md) estabelece sua ordem.

### Limitações conhecidas

- Para uma biblioteca externa, a exceção é por assembly: o uso de outro tipo do mesmo assembly já nomeado, em um tipo listado, não é detectado até a exceção sair.
- `typeof(...)` em argumento de atributo não é resolvido. `nameof`, constantes incorporadas pelo compilador e `default(T)` não geram referência no IL.
- Top-level statements (`<Program>$`) são tipo gerado; os executáveis são cobertos pelas regras de projeto, não pelas de tipo.
- A suíte não verifica isolamento por credencial ou banco, mensageria confiável, implantação independente nem regras de API ou worker em nível de tipo (por exemplo, controllers sem acesso a persistência).

[Voltar à arquitetura](README.md)
