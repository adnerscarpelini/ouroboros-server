using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ouroboros.Modules.Identity.Infrastructure.Persistence;

// Tradução de erros do PostgreSQL, para que Application não dependa do provedor.
internal static class PersistenceErrors
{
    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
