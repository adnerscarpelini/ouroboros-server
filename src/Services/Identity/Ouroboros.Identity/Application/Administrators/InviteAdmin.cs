using Ouroboros.Identity.Application.Sessions;
using Ouroboros.Identity.Contracts;
using Ouroboros.Identity.Contracts.Administrators;
using Ouroboros.Identity.Domain.Administrators;
using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Application.Administrators;

internal sealed class InviteAdmin(AdminAuthorization authorization, ISessionStore sessions, IAdminInvitationStore store,
    IAdminDirectory directory, ISystemParameters parameters, InvitationEmails emails, TimeProvider clock) : IInviteAdmin
{
    public async Task<AdminInvitationResult> ExecuteAsync(InviteAdminRequest request, CancellationToken cancellationToken)
    {
        var (actor, access) = await authorization.AuthenticateAsync(request.ActorId, request.CurrentPassword, cancellationToken);
        if (access != AdminAccess.Allowed) { return Denied(access); }
        var email = request.Email?.Trim().ToLowerInvariant() ?? "";
        var domain = await parameters.GetAsync(CreateFirstAdmin.AllowedEmailDomainParameter, cancellationToken);
        if (!EmailAddresses.IsValid(email) || email[(email.LastIndexOf('@') + 1)..] != domain)
        { return new(AdminInvitationOutcome.InvalidEmail); }

        await using var transaction = await sessions.BeginTransactionAsync(cancellationToken);
        await store.LockAdministrationAsync(cancellationToken);
        access = await authorization.LockAndConfirmAsync(actor!, cancellationToken);
        if (access != AdminAccess.Allowed) { return Denied(access); }
        var now = clock.GetUtcNow();
        if (await store.EmailExistsAsync(email, cancellationToken) || await store.HasOpenInvitationAsync(email, now, cancellationToken))
        { return new(AdminInvitationOutcome.Conflict); }
        var admins = await directory.ActiveAdminsAsync(cancellationToken);
        var invitation = AdminInvitation.Create(email, actor!.ExternalId, now);
        if (admins.Count == 1) { store.AddIntents([await emails.ApproveAsync(invitation, null, now, cancellationToken)]); }
        store.Add(invitation);
        store.AddIntents(emails.Notices(admins, invitation, "criado", now));
        if (!await store.SaveAsync(cancellationToken)) { return new(AdminInvitationOutcome.Conflict); }
        await transaction.CommitAsync(cancellationToken);
        return new(AdminInvitationOutcome.Accepted, invitation.ExternalId);
    }

    internal static AdminInvitationResult Denied(AdminAccess access) => new(access == AdminAccess.Forbidden
        ? AdminInvitationOutcome.Forbidden : AdminInvitationOutcome.InvalidCredentials);
}
