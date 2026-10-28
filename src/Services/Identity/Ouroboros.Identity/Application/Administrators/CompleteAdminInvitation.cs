using System.Security.Cryptography;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Application.Sessions;
using Ouroboros.Identity.Contracts.Administrators;
using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Application.Administrators;

internal sealed class CompleteAdminInvitation(IAdminInvitationStore store, ISessionStore sessions, IAdminDirectory directory,
    IPasswordHasher passwords, InvitationEmails emails, TimeProvider clock) : ICompleteAdminInvitation
{
    public async Task<AdminInvitationResult> ExecuteAsync(CompleteAdminInvitationRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token) || request.Token.Length > 256) { return new(AdminInvitationOutcome.InvalidToken); }
        var hash = OpaqueTokens.Hash(request.Token);
        var snapshot = await store.FindTokenAsync(hash, clock.GetUtcNow(), cancellationToken);
        if (snapshot is null) { return new(AdminInvitationOutcome.InvalidToken); }
        if (PasswordPolicy.Validate(request.Password ?? "", snapshot.Email) is { } rule)
        { return new(AdminInvitationOutcome.PasswordRejected, Rule: rule); }
        var passwordHash = passwords.Hash(request.Password!);
        await using var transaction = await sessions.BeginTransactionAsync(cancellationToken);
        await store.LockAdministrationAsync(cancellationToken);
        var invitation = await store.FindAsync(snapshot.ExternalId, cancellationToken);
        var now = clock.GetUtcNow();
        if (invitation is null || !invitation.CanComplete(now) || invitation.TokenHash is null
            || !CryptographicOperations.FixedTimeEquals(hash, invitation.TokenHash) || await store.EmailExistsAsync(invitation.Email, cancellationToken))
        { return new(AdminInvitationOutcome.InvalidToken); }
        var admin = User.CreateInvitedAdmin(invitation.Email, passwordHash, invitation.InvitedBy);
        invitation.Complete(now);
        store.AddUser(admin);
        var recipients = (await directory.ActiveAdminsAsync(cancellationToken)).Append(new AdminRecipient(admin.ExternalId, admin.Email)).ToArray();
        store.AddIntents(emails.Notices(recipients, invitation, "aceito", now));
        if (!await store.SaveAsync(cancellationToken)) { return new(AdminInvitationOutcome.InvalidToken); }
        await transaction.CommitAsync(cancellationToken);
        return new(AdminInvitationOutcome.Completed);
    }
}
