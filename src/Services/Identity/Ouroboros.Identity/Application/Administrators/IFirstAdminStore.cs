using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Application.Administrators;

// Confirma a verificação de ausência, unicidade e criação na mesma transação local.
internal interface IFirstAdminStore
{
    Task<FirstAdminCreationOutcome> TryCreateAsync(User admin, CancellationToken cancellationToken);
}

internal enum FirstAdminCreationOutcome
{
    Created,
    AdminAlreadyExists,
    EmailAlreadyInUse,
}
