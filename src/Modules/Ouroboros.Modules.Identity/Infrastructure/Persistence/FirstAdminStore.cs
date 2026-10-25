using Microsoft.EntityFrameworkCore;
using Ouroboros.Modules.Identity.Application.Administrators;
using Ouroboros.Modules.Identity.Domain.Users;

namespace Ouroboros.Modules.Identity.Infrastructure.Persistence;

internal sealed class FirstAdminStore(IdentityDbContext context) : IFirstAdminStore
{
    // Chave global preservada: serializa comandos inclusive em processos diferentes.
    private const string LockSql = "SELECT pg_advisory_xact_lock(hashtext('identity:create-first-admin'))";

    public async Task<FirstAdminCreationOutcome> TryCreateAsync(User admin, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync(LockSql, cancellationToken);
        if (await context.Users.AnyAsync(user => user.Role == UserRole.Admin, cancellationToken))
        {
            return FirstAdminCreationOutcome.AdminAlreadyExists;
        }

        if (await context.Users.AnyAsync(user => user.Email == admin.Email, cancellationToken))
        {
            return FirstAdminCreationOutcome.EmailAlreadyInUse;
        }

        context.Users.Add(admin);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return FirstAdminCreationOutcome.Created;
    }
}
