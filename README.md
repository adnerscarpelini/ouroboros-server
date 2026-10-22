<p align="center">
  <img src="logo.png" alt="Logo do Ouroboros Server" width="240" />
</p>

# Ouroboros Server

Projeto pessoal de estudos e portfólio em C#/.NET para uma fintech fictícia e educacional, sem movimentação de dinheiro real. A proposta inicial de e-commerce foi substituída por esse contexto. O projeto evolui continuamente, com foco em aprendizado, simplicidade e decisões explicáveis.

## Estado atual

Somente a estrutura inicial de um monólito modular em .NET 10. Os casos de uso e os modelos de domínio ainda serão definidos.

A solution contém seis projetos reais:

- `Ouroboros.Api`: host ASP.NET Core e composição dos módulos; mapeia os controllers dos módulos, ainda sem endpoints.
- `Ouroboros.Modules.Identity`: estrutura para identidade.
- `Ouroboros.Modules.Customers`: estrutura para clientes.
- `Ouroboros.Modules.Accounts`: estrutura para contas.
- `Ouroboros.Modules.Transactions`: estrutura para transações.
- `Ouroboros.ArchitectureTests`: testes de arquitetura da visibilidade dos tipos dos módulos e da descoberta, das rotas e do versionamento dos controllers.

Cada módulo é uma class library com pastas vazias `Application`, `Contracts`, `Domain` e `Infrastructure`, além de `DependencyInjection.cs`. O método de registro apenas retorna a coleção de serviços (o Identity também registra seus controllers e já tem a base dos controllers de backoffice); a persistência será configurada depois da definição dos casos de uso.

Não há entidades, DTOs, handlers, repositórios, DbContexts, migrations, banco, Docker, autenticação ou operações financeiras implementadas. Os endpoints HTTP serão declarados em controllers `internal` dos próprios módulos, conforme [Controllers HTTP nos módulos](docs/architecture/http-controllers.md), com a versão na URL (`/api/v1/...`) segundo o [Versionamento da API HTTP](docs/architecture/api-versioning.md). Há apenas testes de arquitetura; não há testes funcionais nem workflow de CI nesta etapa.

## Visual Studio e compilação

Requisito: .NET SDK 10.0.402, fixado em `global.json`, e Visual Studio compatível com .NET 10 e SLNX.

Abra `Ouroboros.slnx` por **Arquivo → Abrir → Projeto/Solução**. Abrir somente a pasta do repositório usa a visualização de arquivos. `src` e `Modules` são pastas organizadoras da solution; cada módulo é um projeto C# independente, compilável pelo menu de contexto **Compilar**.

Defina `Ouroboros.Api` como projeto de inicialização para executar o host. As bibliotecas dos módulos não são executáveis independentes.

Na raiz do repositório:

```powershell
dotnet restore Ouroboros.slnx
dotnet build Ouroboros.slnx --no-restore
dotnet build src/Modules/Ouroboros.Modules.Customers/Ouroboros.Modules.Customers.csproj
dotnet test --solution Ouroboros.slnx
dotnet run --project src/Ouroboros.Api --no-launch-profile --urls http://localhost:8080
```

O host não exige connection string e não acessa banco. Como ainda não há endpoints, requisições retornam 404. Os arquivos `.gitkeep` preservam as pastas vazias no Git; os itens `Folder` nos projetos explicitam sua organização para o Visual Studio.

## Documentação

- [Índice](docs/README.md)
- [Arquitetura](docs/architecture/README.md)
