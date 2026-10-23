namespace Ouroboros.BuildingBlocks.Domain;

// Base das entidades com tabela própria, compartilhada pelos módulos. O Id é interno ao módulo; para fora (rotas, respostas,
// claims e Contracts) só sai o ExternalId. CreatedAt e UpdatedAt são preenchidos na gravação.
public abstract class Entity
{
    public long Id { get; private set; }

    public Guid ExternalId { get; private set; } = Guid.CreateVersion7();

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
}
