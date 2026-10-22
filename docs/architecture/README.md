# Arquitetura

Notas gerais e decisões do Ouroboros, distinguindo a estrutura implementada das regras para sua evolução.

- [Estrutura dos módulos](module-structure.md): projetos da solution, pastas padrão, injeção de dependência e fronteiras adotadas.
- [Controllers HTTP nos módulos](http-controllers.md): controllers `internal` declarados pelos módulos, rotas por público e testes de arquitetura da visibilidade, da descoberta e das rotas.
- [Versionamento da API HTTP](api-versioning.md): versão maior na URL com Asp.Versioning, declaração e descontinuação de versões.
- [Persistência com PostgreSQL e EF Core](persistence.md): um banco com um schema por módulo, DbContext e histórico de migrations por módulo, connection string validada na subida, migrations por comando e banco de desenvolvimento em Docker Compose.

[Voltar à documentação](../README.md)
