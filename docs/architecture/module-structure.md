# Estrutura dos serviços

## Organização

O Ouroboros usa microsserviços em monorepo. Cada serviço possui executável, configuração, testes e implantação próprios. Estrutura de referência:

```text
src/
  Services/
    Identity/
      Ouroboros.Identity.Api/
      Ouroboros.Identity/
    Notifications/
      Ouroboros.Notifications.Worker/
      Ouroboros.Notifications/
  Ouroboros.BuildingBlocks/
tests/
```

A biblioteca própria organiza Domain, Application, Infrastructure e contratos como pastas, com assembly e namespace raiz `Ouroboros.<Serviço>`. Identity segue esse formato em `src/Services/Identity/Ouroboros.Identity`. Não é necessário criar um projeto por camada. Serviços HTTP possuem API; consumidores de mensagens podem ser workers sem API de negócio.

Customers e a fronteira financeira serão implementados conforme funcionalidades. Não criar serviços vazios para reproduzir pastas. Parâmetros pertencem ao proprietário, sem serviço Settings central obrigatório.

## Visibilidade dos tipos

Entidades e implementações são internas por padrão. A API usa contratos públicos mínimos e um ponto de composição. Migrations geradas podem ser públicas para descoberta do EF. InternalsVisibleTo de testes não permite compartilhar internals entre serviços.

Contratos expõem ExternalId e dados necessários, nunca entidades, DbContext, IQueryable ou transações. Interfaces locais de DI não constituem integração entre processos.

## Fronteiras adotadas para a evolução

Cada serviço possui seu banco, migrations e regras. Integrações usam HTTP ou mensagens versionadas, sem referência de implementação ou acesso a banco alheio. Saldo e lançamentos que exigem atomicidade permanecem na mesma fronteira financeira.

Reorganizar caminhos, namespaces, referências, solution, fixtures, scripts e documentação. Renomear a pasta não realiza a separação por si só.

## Pacotes NuGet

Versões centralizadas em Directory.Packages.props. Bibliotecas técnicas devem permitir implantação independente. Ver [dependências](layer-dependencies.md).

## Execução e limites

APIs são acessadas diretamente no desenvolvimento, com autorização e restrição administrativa. Gateways são posteriores. Projetos disponíveis: [estado de implementação](../implementation-status.md).

[Voltar à arquitetura](README.md)
