namespace Ouroboros.Identity.Infrastructure.Persistence;

internal sealed class IdentityDatabaseOptions
{
    // Connection string do banco próprio do serviço (role de runtime, sem DDL).
    public const string ConnectionStringName = "Identity";

    // Connection string do banco original da instância, usada enquanto a transferência do banco de Identity não foi
    // feita (e para voltar a ela antes de novas escritas). Sai com a desativação do banco original.
    public const string LegacyConnectionStringName = "Ouroboros";

    public string? ConnectionString { get; set; }
}
