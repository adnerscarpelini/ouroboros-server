# Estrutura dos módulos

## Escopo atual

O Ouroboros é uma fintech fictícia e educacional organizada como monólito modular: um host ASP.NET Core compõe os módulos e será a entrada HTTP da aplicação. Nesta etapa existe somente a estrutura dos projetos; os modelos de domínio e casos de uso ainda serão definidos.

A [solution Ouroboros.slnx](../../Ouroboros.slnx) reúne seis projetos C# independentes:

| Projeto | Papel |
| --- | --- |
| `Ouroboros.Api` | Host e composition root: referencia e registra os quatro módulos. |
| `Ouroboros.Modules.Identity` | Estrutura reservada para identidade. |
| `Ouroboros.Modules.Customers` | Estrutura reservada para clientes. |
| `Ouroboros.Modules.Accounts` | Estrutura reservada para contas. |
| `Ouroboros.Modules.Transactions` | Estrutura reservada para transações. |
| `Ouroboros.ArchitectureTests` | Testes de arquitetura da visibilidade dos módulos e da descoberta, das rotas e do versionamento dos controllers, na pasta `tests`. |

Cada módulo tem seu próprio `.csproj` e pode ser compilado individualmente. As pastas `src`, `Modules` e `tests` apenas organizam os projetos na solution. Dentro de cada módulo, as pastas abaixo são divisões lógicas de uma única class library, sem isolamento de compilação entre elas.

## Padrão para novos módulos

Um pedido de criação de módulo produz apenas esta estrutura, seguindo a nomenclatura existente:

```text
Ouroboros.Modules.<Module>/
├── Application/
├── Contracts/
├── Domain/
├── Infrastructure/
├── DependencyInjection.cs
└── Ouroboros.Modules.<Module>.csproj
```

| Pasta ou arquivo | Responsabilidade reservada |
| --- | --- |
| `Application` | Casos de uso, organizados por funcionalidade quando solicitados. |
| `Contracts` | Contratos públicos mínimos para comunicação com outros módulos, quando necessários. |
| `Domain` | Entidades, objetos de valor e regras do domínio, após sua definição. |
| `Infrastructure` | Persistência e outras implementações de infraestrutura, quando solicitadas; o DbContext e as migrations ficam em `Infrastructure/Persistence`, conforme [Persistência](persistence.md#padrão-para-um-módulo-novo). |
| `Controllers` | Controllers HTTP `internal` do módulo, em `Customer` e `Admin`; criada quando o módulo ganha endpoints. |
| `DependencyInjection.cs` | Ponto de registro das dependências do módulo. |

As pastas vazias são preservadas no Git por arquivos `.gitkeep`, e itens `Folder` nos projetos explicitam sua organização no Visual Studio. Hoje só a `Infrastructure` do Identity tem conteúdo: o `IdentityDbContext`, suas options e a migration inicial.

Os métodos `AddIdentityModule`, `AddCustomersModule`, `AddAccountsModule` e `AddTransactionsModule` retornam `IServiceCollection`, e o [host](../../src/Ouroboros.Api/Program.cs) os invoca. Só o do Identity registra algo: sua application part de controllers e o `IdentityDbContext`, com a connection string validada na subida (veja [Persistência](persistence.md)). Nenhum módulo cria o banco nem aplica migrations ao subir.

Um módulo com endpoints ganha a pasta `Controllers`, registra o próprio assembly como application part em `Add<Module>Module` e passa a referenciar `Microsoft.AspNetCore.App`, conforme [Controllers HTTP nos módulos](http-controllers.md). Os controllers dependem de `Application` e não acessam `Infrastructure` diretamente. Hoje só o Identity está preparado, com a `AdminControllerBase` e sem controllers concretos.

Entidades, DTOs, handlers, repositórios, DbContexts, migrations, endpoints e dados de exemplo só serão adicionados mediante solicitação de funcionalidades; o DbContext e a migration inicial vazia do Identity vieram da configuração da persistência (#5). Criar um módulo não autoriza inferir seus casos de uso. Não há `BuildingBlocks`: nenhum mecanismo transversal é necessário nesta estrutura.

## Visibilidade dos tipos

Todo tipo de um módulo é `internal` por padrão. Somente dois pontos são `public`:

- a classe `DependencyInjection` e seu método `Add<Module>Module`, usados pelo host;
- os tipos em `Contracts`, que formam a API pública do módulo para os demais.

Entidades, objetos de valor, casos de uso, repositórios, DbContexts e controllers das pastas `Domain`, `Application`, `Infrastructure` e `Controllers` nunca são `public`. A única exceção são as classes de migration geradas pelo `dotnet ef`, explicada em [Migrations geradas](persistence.md#migrations-geradas). Assim, se um módulo referenciar outro, o compilador só lhe permite enxergar `Contracts`. Tornar um tipo `public` fora dessas exceções exige mover o tipo para `Contracts` ou registrar a decisão nesta nota. O [teste de arquitetura](http-controllers.md#testes-de-arquitetura) falha se um módulo expuser outro tipo `public` (exceto migrations) ou conceder `InternalsVisibleTo` a `Ouroboros.Api`.

Quando houver projetos de teste que precisem de tipos internos, o acesso será concedido com `InternalsVisibleTo` no `.csproj` do módulo, nunca à Api, e não tornando tipos públicos.

## Pacotes NuGet

As versões dos pacotes são centralizadas em [Directory.Packages.props](../../Directory.Packages.props) (Central Package Management). Os `.csproj` declaram apenas `<PackageReference Include="..." />`, sem `Version`. Para adicionar ou atualizar um pacote, altere seu `PackageVersion` nesse arquivo.

## Fronteiras adotadas para a evolução

As decisões abaixo orientam a evolução dos módulos. A base de persistência já existe; as demais regras ainda não têm controles automáticos:

- Cada módulo é dono de seus dados e mapeamentos. Todos usam um único banco PostgreSQL, com os schemas `identity`, `customers`, `accounts` e `transactions` e um DbContext e histórico de migrations próprios por módulo, conforme [Persistência](persistence.md). Hoje só o schema `identity` existe, ainda sem entidades.
- JOINs, SQL direto e foreign keys entre schemas de módulos diferentes são proibidos. A aplicação deverá obter dados de outro módulo por contratos públicos pequenos e chamadas em memória.
- Entidades de domínio e entidades EF não serão compartilhadas entre módulos. Referências entre módulos deverão ser unidirecionais, sem ciclos; hoje nenhum módulo referencia outro.
- Transações locais serão o padrão. Operações futuras em vários módulos exigirão uma decisão explícita de consistência e coordenação; DbContexts diferentes não garantem atomicidade conjunta.
- A API ficará responsável por HTTP e composição, enquanto regras de negócio pertencerão aos módulos. Casos de uso poderão seguir Vertical Slices sem criar subprojetos por camada.

Essas fronteiras são escolhas do Ouroboros. Pastas e convenções, por si sós, não impedem violações em runtime. Hoje só a visibilidade dos tipos, as rotas e versões dos controllers e a exigência da connection string na subida são verificadas automaticamente; testes para as demais fronteiras e inspeções de mapeamentos deverão acompanhar a implementação que vier a necessitá-los.

## Execução e limites

Consulte o [README](../../README.md) para abrir a solution no Visual Studio, compilar a solução ou um módulo, executar os testes e iniciar o host.

O host exige a connection string `ConnectionStrings:Ouroboros` e não sobe sem ela; o README explica como [subir o PostgreSQL local com Docker Compose e aplicar as migrations](../../README.md#banco-de-dados-local). O host mapeia os controllers dos módulos, mas ainda não há controllers concretos, então responde 404 às requisições. Há apenas testes de arquitetura; não há testes funcionais ou contra o banco real, CI, autenticação ou operações financeiras nesta etapa. Ledger permanece fora do escopo.

O QA da estrutura inicial verificou a compilação da solution e dos quatro módulos individualmente, sem warnings ou erros, além da inicialização do host, que na época não exigia connection string (exigência introduzida na #5). A interface do Visual Studio não foi inspecionada; a validação usou os arquivos de projeto e a CLI. Não há comportamento de domínio a validar nesta entrega.

[Voltar à arquitetura](README.md)
