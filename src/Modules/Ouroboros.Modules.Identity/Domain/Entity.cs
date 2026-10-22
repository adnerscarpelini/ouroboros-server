namespace Ouroboros.Modules.Identity.Domain;

// Base das entidades com tabela própria. O Id é interno ao módulo; para fora (rotas, respostas,
// claims e Contracts) só sai o ExternalId. CreatedAt e UpdatedAt são preenchidos na gravação.
internal abstract class Entity
{
    public long Id { get; private set; }

    public Guid ExternalId { get; private set; } = Guid.CreateVersion7();

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
}
