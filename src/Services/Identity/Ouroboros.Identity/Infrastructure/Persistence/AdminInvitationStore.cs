using Microsoft.EntityFrameworkCore;
using Ouroboros.Identity.Application.Administrators;
using Ouroboros.Identity.Domain.Administrators;
using Ouroboros.Identity.Domain.Notifications;
using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Infrastructure.Persistence;

internal sealed class AdminDirectory(IdentityDbContext context) : IAdminDirectory
{
    public async Task<IReadOnlyList<AdminRecipient>> ActiveAdminsAsync(CancellationToken cancellationToken) =>
        await context.Users.AsNoTracking().Where(user => user.IsActive && user.Role == UserRole.Admin)
            .OrderBy(user => user.Id).Select(user => new AdminRecipient(user.ExternalId, user.Email)).ToArrayAsync(cancellationToken);
    public Task<bool> IsActiveAdminAsync(long userId, CancellationToken cancellationToken) =>
        context.Users.AsNoTracking().AnyAsync(user => user.Id == userId && user.IsActive && user.Role == UserRole.Admin, cancellationToken);
}

internal sealed class AdminInvitationStore(IdentityDbContext context) : IAdminInvitationStore
{
    public Task LockAdministrationAsync(CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('identity:create-first-admin'))", cancellationToken);
    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken) => context.Users.AnyAsync(user => user.Email == email, cancellationToken);
    public Task<bool> HasOpenInvitationAsync(string email, DateTimeOffset now, CancellationToken cancellationToken) =>
        context.AdminInvitations.AnyAsync(invitation => invitation.Email == email
            && ((invitation.State == InvitationState.PendingApproval && invitation.ApprovalExpiresAt > now)
                || (invitation.State == InvitationState.Approved && invitation.TokenExpiresAt > now)), cancellationToken);
    public Task<AdminInvitation?> FindTokenAsync(byte[] hash, DateTimeOffset now, CancellationToken cancellationToken) =>
        context.AdminInvitations.AsNoTracking().SingleOrDefaultAsync(invitation => invitation.TokenHash == hash
            && invitation.State == InvitationState.Approved && invitation.TokenExpiresAt > now, cancellationToken);
    public Task<AdminInvitation?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        context.AdminInvitations.SingleOrDefaultAsync(invitation => invitation.ExternalId == id, cancellationToken);
    public void Add(AdminInvitation invitation) => context.AdminInvitations.Add(invitation);
    public void AddUser(User user) => context.Users.Add(user);
    public void AddIntents(IEnumerable<EmailIntent> intents) => context.EmailIntents.AddRange(intents);
    public async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
        try { await context.SaveChangesAsync(cancellationToken); return true; }
        catch (DbUpdateException exception) when (PersistenceErrors.IsUniqueViolation(exception)) { return false; }
    }
}
