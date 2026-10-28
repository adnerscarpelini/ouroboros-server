using Ouroboros.Identity.Application.Sessions;
using Ouroboros.Identity.Contracts.Administrators;
using Ouroboros.Identity.Domain.Administrators;

namespace Ouroboros.Identity.Application.Administrators;

internal sealed class DecideAdminInvitation(AdminAuthorization authorization, ISessionStore sessions, IAdminInvitationStore store,
    IAdminDirectory directory, InvitationApprovers approvers, InvitationEmails emails, TimeProvider clock) : IDecideAdminInvitation
{
    public async Task<AdminInvitationResult> ExecuteAsync(DecideAdminInvitationRequest request, CancellationToken cancellationToken)
    {
        var (actor, access) = await authorization.AuthenticateAsync(request.ActorId, request.CurrentPassword, cancellationToken);
        if (access != AdminAccess.Allowed) { return InviteAdmin.Denied(access); }
        await using var transaction = await sessions.BeginTransactionAsync(cancellationToken);
        await store.LockAdministrationAsync(cancellationToken);
        access = await authorization.LockAndConfirmAsync(actor!, cancellationToken);
        if (access != AdminAccess.Allowed) { return InviteAdmin.Denied(access); }
        var invitation = await store.FindAsync(request.InvitationId, cancellationToken);
        if (invitation is null) { return new(AdminInvitationOutcome.NotFound); }
        var admins = await directory.ActiveAdminsAsync(cancellationToken);
        if (invitation.InvitedBy == actor!.ExternalId
            || !(await approvers.ReadAsync(admins, invitation.InvitedBy, cancellationToken)).Any(admin => admin.Id == actor.ExternalId))
        { return new(AdminInvitationOutcome.Forbidden); }
        if (invitation.State != InvitationState.PendingApproval) { return new(AdminInvitationOutcome.Conflict); }
        var now = clock.GetUtcNow();
        if (invitation.ApprovalExpiresAt <= now)
        {
            invitation.Expire();
            await store.SaveAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(AdminInvitationOutcome.InvalidToken);
        }
        if (request.Approve) { store.AddIntents([await emails.ApproveAsync(invitation, actor.ExternalId, now, cancellationToken)]); }
        else { invitation.Reject(actor.ExternalId, now); }
        store.AddIntents(emails.Notices(admins, invitation, request.Approve ? "aprovado" : "recusado", now));
        if (!await store.SaveAsync(cancellationToken)) { return new(AdminInvitationOutcome.Conflict); }
        await transaction.CommitAsync(cancellationToken);
        return new(AdminInvitationOutcome.Decided);
    }
}
