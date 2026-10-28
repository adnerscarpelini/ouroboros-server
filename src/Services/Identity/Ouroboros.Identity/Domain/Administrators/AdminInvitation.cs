using Ouroboros.BuildingBlocks.Domain;

namespace Ouroboros.Identity.Domain.Administrators;

internal enum InvitationState { PendingApproval, Approved, Completed, Rejected, Expired }

internal sealed class AdminInvitation : Entity
{
    private AdminInvitation(string email, Guid invitedBy, DateTimeOffset approvalExpiresAt)
    { Email = email; InvitedBy = invitedBy; ApprovalExpiresAt = approvalExpiresAt; }

    public string Email { get; private set; }
    public Guid InvitedBy { get; private set; }
    public InvitationState State { get; private set; } = InvitationState.PendingApproval;
    public DateTimeOffset ApprovalExpiresAt { get; private set; }
    public byte[]? TokenHash { get; private set; }
    public DateTimeOffset? TokenExpiresAt { get; private set; }
    public Guid? DecidedBy { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }

    public static AdminInvitation Create(string email, Guid invitedBy, DateTimeOffset now) => new(email, invitedBy, now.AddHours(24));

    public void Approve(byte[] hash, DateTimeOffset expiresAt, Guid? decidedBy, DateTimeOffset now)
    {
        if (State != InvitationState.PendingApproval || ApprovalExpiresAt <= now || hash.Length != 32)
        { throw new InvalidOperationException("Convite não pode ser aprovado."); }
        State = InvitationState.Approved;
        TokenHash = hash;
        TokenExpiresAt = expiresAt;
        DecidedBy = decidedBy;
        DecidedAt = decidedBy is null ? null : now;
    }

    public bool CanComplete(DateTimeOffset now) => State == InvitationState.Approved && TokenExpiresAt > now;
    public bool IsOpen(DateTimeOffset now) => State == InvitationState.PendingApproval ? ApprovalExpiresAt > now : CanComplete(now);
    public void Complete(DateTimeOffset now)
    {
        if (!CanComplete(now)) { throw new InvalidOperationException("Convite inválido."); }
        State = InvitationState.Completed;
        TokenHash = null;
    }

    public void Reject(Guid decidedBy, DateTimeOffset now)
    {
        if (State != InvitationState.PendingApproval || ApprovalExpiresAt <= now) { throw new InvalidOperationException("Convite inválido."); }
        State = InvitationState.Rejected; DecidedBy = decidedBy; DecidedAt = now;
    }
    public void Expire() { State = InvitationState.Expired; TokenHash = null; }
}
