# Persistência com PostgreSQL e EF Core

## Decisão

Cada módulo que constitui um serviço persistente possui banco PostgreSQL, credencial, connection string, DbContext e migrations próprios. Schemas podem organizar um banco internamente, mas não substituem bancos separados entre serviços.

## Instância local compartilhada

Por decisão consciente de aprendizado, todos os bancos ficam em uma única instância PostgreSQL, em um único container Docker. Isso economiza recursos e administração local. Não precisamos de um servidor por serviço nesta etapa.

Disponibilidade, CPU, memória e armazenamento são compartilhados: parada, atualização, saturação ou perda do volume podem afetar todos os bancos. Separação lógica não oferece isolamento físico ou alta disponibilidade. APIs, workers e broker executam separadamente do PostgreSQL.

## Acesso e configuração

Runtime tem credencial exclusiva, sem superuser, criação de roles ou bancos. Revogar acessos públicos inadequados e testar negação entre proprietários. Provisionamento e migrations usam permissões separadas.

Não usar JOIN, FDW, dblink ou credenciais alheias para contornar a fronteira. Cada serviço aceita endpoint próprio, permitindo mover seu banco sem alterar regras de negócio.

## Migrations geradas

Histórico pertence ao serviço. Aplicar migrations explicitamente por comando, script ou bundle, sem execução automática na subida da API. Classes geradas podem ser públicas para descoberta do EF; não são contratos de integração.

Comandos disponíveis: [desenvolvimento](../development.md). Provisionamento e transferência: #81 e #82.

## Transações e recuperação

Transações locais protegem parâmetro e histórico, senha e sessões, estado e outbox. Não usar transação distribuída.

Transferência exige backup, janela sem escrita, cópia e validação de identificadores, hashes, sessões e histórico antes da troca. Não excluir origem ou volume automaticamente. Depois de novas escritas, rollback precisa preservá-las ou reconciliá-las; voltar apenas a imagem não basta.

## Testes

Usar PostgreSQL real para constraints, locks e concorrência. Verificar acesso negado a banco alheio, permissões runtime, migrations e restauração.

[Voltar à arquitetura](README.md)
