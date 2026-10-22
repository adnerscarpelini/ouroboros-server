namespace Ouroboros.Modules.Identity.Infrastructure.Persistence;

internal sealed class IdentityDatabaseOptions
{
    public const string ConnectionStringName = "Ouroboros";

    public string? ConnectionString { get; set; }
}
