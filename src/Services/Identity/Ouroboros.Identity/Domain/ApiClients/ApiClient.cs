using Ouroboros.BuildingBlocks.Domain;

namespace Ouroboros.Identity.Domain.ApiClients;

internal sealed class ApiClient : Entity
{
    private ApiClient(string name, string description, byte[] secretHash, Guid createdBy)
    { Name = name; Description = description; SecretHash = secretHash; CreatedBy = createdBy; }
    public string Name { get; private set; }
    public string Description { get; private set; }
    public byte[] SecretHash { get; private set; }
    public Guid CreatedBy { get; private set; }
    public bool IsActive { get; private set; } = true;

    public static ApiClient Create(string name, string? description, byte[] hash, Guid actor)
    {
        var normalized = name?.Trim() ?? "";
        if (normalized.Length is < 1 or > 100 || normalized.Any(char.IsControl)
            || (description?.Length ?? 0) > 1000 || description?.Contains('\0') == true || hash.Length != 32 || actor == Guid.Empty)
        { throw new ArgumentException("Dados do integrador inválidos."); }
        return new(normalized, description ?? "", hash, actor);
    }
}
