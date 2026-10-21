# Estrutura dos módulos

## Escopo atual

O Ouroboros é uma fintech fictícia e educacional organizada como monólito modular: um host ASP.NET Core compõe os módulos e será a entrada HTTP da aplicação. Nesta etapa existe somente a estrutura dos projetos; os modelos de domínio e casos de uso ainda serão definidos.

A [solution Ouroboros.slnx](../../Ouroboros.slnx) reúne cinco projetos C# independentes:

| Projeto | Papel |
| --- | --- |
| `Ouroboros.Api` | Host e composition root: referencia e registra os quatro módulos. |
| `Ouroboros.Modules.Identity` | Estrutura reservada para identidade. |
| `Ouroboros.Modules.Customers` | Estrutura reservada para clientes. |
| `Ouroboros.Modules.Accounts` | Estrutura reservada para contas. |
| `Ouroboros.Modules.Transactions` | Estrutura reservada para transações. |

Cada módulo tem seu próprio `.csproj` e pode ser compilado individualmente. As pastas `src` e `Modules` apenas organizam os projetos na solution. Dentro de cada módulo, as pastas abaixo são divisões lógicas de uma única class library, sem isolamento de compilação entre elas.

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
| `Infrastructure` | Persistência e outras implementações de infraestrutura, quando solicitadas. |
| `DependencyInjection.cs` | Ponto de registro das dependências do módulo. |

As quatro pastas estão vazias. Arquivos `.gitkeep` as preservam no Git e itens `Folder` nos projetos explicitam sua organização no Visual Studio.

Os métodos `AddIdentityModule`, `AddCustomersModule`, `AddAccountsModule` e `AddTransactionsModule` apenas retornam `IServiceCollection`. O [host](../../src/Ouroboros.Api/Program.cs) já os invoca, mas eles não registram persistência nem criam banco.

Entidades, DTOs, handlers, repositórios, DbContexts, migrations, endpoints e dados de exemplo só serão adicionados mediante solicitação de funcionalidades. Criar um módulo não autoriza inferir seus casos de uso. Não há `BuildingBlocks`: nenhum mecanismo transversal é necessário nesta estrutura.

## Visibilidade dos tipos

Todo tipo de um módulo é `internal` por padrão. Somente dois pontos são `public`:

- a classe `DependencyInjection` e seu método `Add<Module>Module`, usados pelo host;
- os tipos em `Contracts`, que formam a API pública do módulo para os demais.

Entidades, objetos de valor, casos de uso, repositórios e DbContexts das pastas `Domain`, `Application` e `Infrastructure` nunca são `public`. Assim, se um módulo referenciar outro, o compilador só lhe permite enxergar `Contracts`. Tornar um tipo `public` fora dessas exceções exige mover o tipo para `Contracts` ou registrar a decisão nesta nota.

Quando houver projetos de teste, o acesso aos tipos internos será concedido com `InternalsVisibleTo` no `.csproj` do módulo, e não tornando tipos públicos.

## Pacotes NuGet

As versões dos pacotes são centralizadas em [Directory.Packages.props](../../Directory.Packages.props) (Central Package Management). Os `.csproj` declaram apenas `<PackageReference Include="..." />`, sem `Version`. Para adicionar ou atualizar um pacote, altere seu `PackageVersion` nesse arquivo.

## Fronteiras adotadas para a evolução

As decisões abaixo orientam implementações futuras; não representam banco, contratos ou controles automáticos já existentes:

- Cada módulo será dono de seus dados e mapeamentos. Ao implementar PostgreSQL, os schemas serão `identity`, `customers`, `accounts` e `transactions`, com um DbContext e histórico de migrations próprios por módulo.
- JOINs, SQL direto e foreign keys entre schemas de módulos diferentes são proibidos. A aplicação deverá obter dados de outro módulo por contratos públicos pequenos e chamadas em memória.
- Entidades de domínio e entidades EF não serão compartilhadas entre módulos. Referências entre módulos deverão ser unidirecionais, sem ciclos; hoje nenhum módulo referencia outro.
- Transações locais serão o padrão. Operações futuras em vários módulos exigirão uma decisão explícita de consistência e coordenação; DbContexts diferentes não garantem atomicidade conjunta.
- A API ficará responsável por HTTP e composição, enquanto regras de negócio pertencerão aos módulos. Casos de uso poderão seguir Vertical Slices sem criar subprojetos por camada.

Essas fronteiras são escolhas do Ouroboros. Pastas e convenções, por si sós, não impedem violações em runtime. Testes de arquitetura e inspeções de mapeamentos deverão acompanhar a implementação que vier a necessitá-los.

## Execução e limites

Consulte o [README](../../README.md) para abrir a solution no Visual Studio, compilar a solução ou um módulo e iniciar o host.

A estrutura atual não exige connection string. O host inicia sem rotas e responde 404 às requisições. Não há banco, Docker, testes funcionais, CI, autenticação ou operações financeiras nesta etapa. Ledger permanece fora do escopo.

O QA desta estrutura verificou a compilação da solution e dos quatro módulos individualmente, sem warnings ou erros, além da inicialização do host sem connection string. A interface do Visual Studio não foi inspecionada; a validação usou os arquivos de projeto e a CLI. Não há comportamento de domínio a validar nesta entrega.

[Voltar à arquitetura](README.md)
