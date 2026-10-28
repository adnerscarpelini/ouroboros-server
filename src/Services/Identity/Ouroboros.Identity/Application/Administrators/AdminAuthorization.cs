using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Application.Sessions;
using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Application.Administrators;

internal sealed record AdminRecipient(Guid Id, string Email);
internal interface IAdminDirectory
{
    Task<IReadOnlyList<AdminRecipient>> ActiveAdminsAsync(CancellationToken cancellationToken);
    Task<bool> IsActiveAdminAsync(long userId, CancellationToken cancellationToken);
}
internal enum AdminAccess { Allowed, InvalidCredentials, Forbidden }
internal sealed class AdminAuthorization(ISessionStore sessions, IAdminDirectory directory, IPasswordHasher passwords, TimeProvider clock)
{
    internal async Task<(User? Actor, AdminAccess Access)> AuthenticateAsync(Guid actorId, string password, CancellationToken cancellationToken)
    {
        var actor = await sessions.FindUserAsync(actorId, cancellationToken);
        if (actor is null) { return (null, AdminAccess.InvalidCredentials); }
        if (!actor.IsActive || actor.Role != UserRole.Admin) { return (null, AdminAccess.Forbidden); }
        if (actor.IsLockedAt(clock.GetUtcNow()) || passwords.Verify(actor.PasswordHash, password ?? "") == PasswordVerificationOutcome.Failed)
        { return (null, AdminAccess.InvalidCredentials); }
        return (actor, AdminAccess.Allowed);
    }

    internal async Task<AdminAccess> LockAndConfirmAsync(User actor, CancellationToken cancellationToken)
    {
        await sessions.LockUserAsync(actor.Id, cancellationToken);
        var current = await sessions.ReadUserStateAsync(actor.Id, cancellationToken);
        if (!await directory.IsActiveAdminAsync(actor.Id, cancellationToken)) { return AdminAccess.Forbidden; }
        return current.PasswordHash != actor.PasswordHash || current.LockedUntil > clock.GetUtcNow()
            ? AdminAccess.InvalidCredentials : AdminAccess.Allowed;
    }
}
