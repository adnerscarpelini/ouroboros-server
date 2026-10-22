# Estrutura dos módulos

## Escopo atual

O Ouroboros é uma fintech fictícia e educacional organizada como monólito modular: dois hosts ASP.NET Core, a API externa e o backoffice, compõem os módulos e são a entrada HTTP da aplicação ([Hosts](hosts.md)). Nesta etapa existe a estrutura dos projetos e, no Identity, a entidade `User` com o hash de senha ([Domínio de identidade](identity.md)); os demais modelos de domínio e os casos de uso ainda serão definidos.

A [solution Ouroboros.slnx](../../Ouroboros.slnx) reúne nove projetos C# independentes:

| Projeto | Papel |
| --- | --- |
| `Ouroboros.Api` | Host da API externa (app do cliente, anônimos e integrações) e composition root: referencia e registra os quatro módulos e serve os controllers que não são de backoffice. |
| `Ouroboros.Backoffice.Api` | Host interno do backoffice: referencia e registra os quatro módulos e serve só os controllers que herdam a `AdminControllerBase`. |
| `Ouroboros.Hosting` | Configuração HTTP comum aos dois hosts (controllers por público, versionamento, ProblemDetails); não referencia módulos. |
| `Ouroboros.Modules.Identity` | Identidade: entidade `User`, hash de senha e persistência no schema `identity`. |
| `Ouroboros.Modules.Customers` | Estrutura reservada para clientes. |
| `Ouroboros.Modules.Accounts` | Estrutura reservada para contas. |
| `Ouroboros.Modules.Transactions` | Estrutura reservada para transações. |
| `Ouroboros.ArchitectureTests` | Testes de arquitetura da visibilidade dos módulos, da não exposição do `Id` interno das entidades e da descoberta, das rotas e do versionamento dos controllers, na pasta `tests`. |
| `Ouroboros.IntegrationTests` | Testes de integração da API contra um PostgreSQL real em container, na pasta `tests`; exigem Docker (veja [Testes de integração](integration-tests.md)). |

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
| `Domain` | Entidades, objetos de valor e regras do domínio, após sua definição; as entidades seguem as [convenções de identificadores e datas](entity-conventions.md). |
| `Infrastructure` | Persistência e outras implementações de infraestrutura, quando solicitadas; o DbContext e as migrations ficam em `Infrastructure/Persistence`, conforme [Persistência](persistence.md#padrão-para-um-módulo-novo). |
| `Controllers` | Controllers HTTP `internal` do módulo, em `Customer` e `Admin`; criada quando o módulo ganha endpoints. |
| `DependencyInjection.cs` | Ponto de registro das dependências do módulo. |

As pastas vazias são preservadas no Git por arquivos `.gitkeep`, e itens `Folder` nos projetos explicitam sua organização no Visual Studio. Hoje só o Identity tem conteúdo, descrito em [Domínio de identidade](identity.md):

- `Domain`: a base `Entity` e, em `Users/`, a entidade `User` e o enum `UserRole`;
- `Application`: em `Security/`, a porta `IPasswordHasher` e o `UserPasswordVerifier`;
- `Infrastructure`: em `Persistence/`, o `IdentityDbContext`, suas options, as migrations (`InitialCreate` e `AddUsers`), a `UserConfiguration` (em `Configurations/`) e os tipos das [convenções das entidades](entity-conventions.md) (`EntityConfiguration<TEntity>`, `AuditTimestampsInterceptor` e `SnakeCaseNaming`); em `Security/`, o `AspNetCorePasswordHasher`.

`Contracts` continua vazia no Identity e nos demais módulos.

Os métodos `AddIdentityModule`, `AddCustomersModule`, `AddAccountsModule` e `AddTransactionsModule` retornam `IServiceCollection`, e cada host os invoca no próprio `Program.cs` ([API externa](../../src/Ouroboros.Api/Program.cs) e [backoffice](../../src/Ouroboros.Backoffice.Api/Program.cs)). Só o do Identity registra algo: sua application part de controllers, o `IPasswordHasher` (implementado por `AspNetCorePasswordHasher`) e o `UserPasswordVerifier` como singletons, o `TimeProvider` e o `AuditTimestampsInterceptor`, e o `IdentityDbContext`, com a connection string validada na subida (veja [Persistência](persistence.md)). Nenhum módulo cria o banco nem aplica migrations ao subir.

Um módulo com endpoints ganha a pasta `Controllers`, registra o próprio assembly como application part em `Add<Module>Module` e passa a referenciar `Microsoft.AspNetCore.App`, conforme [Controllers HTTP nos módulos](http-controllers.md). Os controllers dependem de `Application` e não acessam `Infrastructure` diretamente. Hoje só o Identity está preparado, com a `AdminControllerBase` e sem controllers concretos.

Entidades, DTOs, handlers, repositórios, DbContexts, migrations, endpoints e dados de exemplo só serão adicionados mediante solicitação de funcionalidades; o DbContext e a migration inicial vazia do Identity vieram da configuração da persistência (#5), a base `Entity` e os tipos de persistência que a acompanham, das convenções das entidades (#6), e o `User`, o hash de senha e a migration `AddUsers`, do domínio de identidade (#8). Criar um módulo não autoriza inferir seus casos de uso. Não há `BuildingBlocks`: nenhum mecanismo transversal é necessário nesta estrutura.

## Visibilidade dos tipos

Todo tipo de um módulo é `internal` por padrão. Somente dois pontos são `public`:

- a classe `DependencyInjection` e seu método `Add<Module>Module`, usados pelo host;
- os tipos em `Contracts`, que formam a API pública do módulo para os demais.

Entidades, objetos de valor, casos de uso, repositórios, DbContexts e controllers das pastas `Domain`, `Application`, `Infrastructure` e `Controllers` nunca são `public`. A única exceção são as classes de migration geradas pelo `dotnet ef`, explicada em [Migrations geradas](persistence.md#migrations-geradas). Assim, se um módulo referenciar outro, o compilador só lhe permite enxergar `Contracts`. Tornar um tipo `public` fora dessas exceções exige mover o tipo para `Contracts` ou registrar a decisão nesta nota. O [teste de arquitetura](http-controllers.md#testes-de-arquitetura) falha se um módulo expuser outro tipo `public` (exceto migrations) ou conceder `InternalsVisibleTo` a `Ouroboros.Api`; a mesma regra vale para `Ouroboros.Backoffice.Api`, mas ainda não é verificada para ele.

Quando um projeto de teste precisa de tipos internos, o acesso é concedido com `InternalsVisibleTo` no `.csproj` do módulo, nunca aos hosts, e não tornando tipos públicos. Hoje só o Identity o concede, a `Ouroboros.IntegrationTests`, para que os testes das [convenções das entidades](entity-conventions.md#testes) usem `Entity`, `EntityConfiguration<TEntity>`, o interceptor e o `IdentityDbContext`, e os do [domínio de identidade](identity.md#testes) usem `User`, o hasher e o verificador. A descoberta dos DbContexts para aplicar migrations não depende disso: eles são resolvidos pela DI, com os tipos encontrados por reflexão.

O `Id` (`long`) das entidades também não sai do módulo: `Contracts`, rotas e DTOs de resposta (com nome terminado em `Response`) usam o `ExternalId`, conforme [Identificadores, datas e nomes das entidades](entity-conventions.md); um teste de arquitetura verifica essa regra por convenção de nomes.

## Pacotes NuGet

As versões dos pacotes são centralizadas em [Directory.Packages.props](../../Directory.Packages.props) (Central Package Management). Os `.csproj` declaram apenas `<PackageReference Include="..." />`, sem `Version`. Para adicionar ou atualizar um pacote, altere seu `PackageVersion` nesse arquivo.

## Fronteiras adotadas para a evolução

As decisões abaixo orientam a evolução dos módulos. A base de persistência já existe; as demais regras ainda não têm controles automáticos:

- Cada módulo é dono de seus dados e mapeamentos. Todos usam um único banco PostgreSQL, com os schemas `identity`, `customers`, `accounts` e `transactions` e um DbContext e histórico de migrations próprios por módulo, conforme [Persistência](persistence.md). Hoje só o schema `identity` existe, com a tabela `users`; as entidades seguem as [convenções das entidades](entity-conventions.md).
- JOINs, SQL direto e foreign keys entre schemas de módulos diferentes são proibidos. A aplicação deverá obter dados de outro módulo por contratos públicos pequenos e chamadas em memória.
- Entidades de domínio e entidades EF não serão compartilhadas entre módulos. Referências entre módulos deverão ser unidirecionais, sem ciclos; hoje nenhum módulo referencia outro.
- Transações locais serão o padrão. Operações futuras em vários módulos exigirão uma decisão explícita de consistência e coordenação; DbContexts diferentes não garantem atomicidade conjunta.
- Os hosts ficarão responsáveis por HTTP e composição, enquanto regras de negócio pertencerão aos módulos. Casos de uso poderão seguir Vertical Slices sem criar subprojetos por camada.

Essas fronteiras são escolhas do Ouroboros. Pastas e convenções, por si sós, não impedem violações em runtime. Hoje só a visibilidade dos tipos, a não exposição do `Id` interno em `Contracts` e respostas, as convenções de identificadores, datas e nomes das entidades, as rotas e versões dos controllers, a exigência da connection string na subida e a aplicação das migrations dos módulos num PostgreSQL real são verificadas automaticamente; testes para as demais fronteiras e inspeções de mapeamentos deverão acompanhar a implementação que vier a necessitá-los.

## Execução e limites

Consulte o [README](../../README.md) para abrir a solution no Visual Studio, compilar a solução ou um módulo, [executar os testes](../../README.md#testes) e iniciar os hosts.

Cada host exige a connection string `ConnectionStrings:Ouroboros` e não sobe sem ela; o README explica como [subir o PostgreSQL local com Docker Compose e aplicar as migrations](../../README.md#banco-de-dados-local). Cada host mapeia os controllers dos módulos do seu público, mas ainda não há controllers concretos, então ambos respondem 404 às requisições. Há testes de arquitetura e [testes de integração contra um PostgreSQL real](integration-tests.md), estes ainda sem endpoints de módulo a exercitar; não há testes funcionais de casos de uso, CI, autenticação (login, JWT) ou operações financeiras nesta etapa; o Identity tem só o usuário e o hash de senha, sem endpoints. Ledger permanece fora do escopo.

O QA da estrutura inicial verificou a compilação da solution e dos quatro módulos individualmente, sem warnings ou erros, além da inicialização do host, que na época não exigia connection string (exigência introduzida na #5). A interface do Visual Studio não foi inspecionada; a validação usou os arquivos de projeto e a CLI. Não há comportamento de domínio a validar nesta entrega.

[Voltar à arquitetura](README.md)
