using Microsoft.EntityFrameworkCore;
using Ouroboros.Identity.Application.Parameters;
using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Infrastructure.Persistence;

internal sealed class ParameterAdministrationReader(IdentityDbContext context) : IParameterAdministrationReader
{
    public Task<bool?> ReadSensitivityAsync(string key, CancellationToken cancellationToken) =>
        context.Parameters.Where(parameter => parameter.Key == key).Select(parameter => (bool?)parameter.IsSensitive).SingleOrDefaultAsync(cancellationToken);

    public Task<bool> IsActiveAdminAsync(long userId, CancellationToken cancellationToken) =>
        context.Users.AnyAsync(user => user.Id == userId && user.Role == UserRole.Admin && user.IsActive, cancellationToken);

    public async Task<IReadOnlyList<string>> ActiveAdminEmailsAsync(CancellationToken cancellationToken) =>
        await context.Users.Where(user => user.Role == UserRole.Admin && user.IsActive).OrderBy(user => user.Id)
            .Select(user => user.Email).ToArrayAsync(cancellationToken);
}
