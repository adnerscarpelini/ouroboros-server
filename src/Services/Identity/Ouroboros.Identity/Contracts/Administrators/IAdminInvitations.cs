namespace Ouroboros.Identity.Contracts.Administrators;

public interface IInviteAdmin
{
    Task<AdminInvitationResult> ExecuteAsync(InviteAdminRequest request, CancellationToken cancellationToken);
}
public interface ICompleteAdminInvitation
{
    Task<AdminInvitationResult> ExecuteAsync(CompleteAdminInvitationRequest request, CancellationToken cancellationToken);
}
public sealed record InviteAdminRequest(Guid ActorId, string Email, string CurrentPassword);
public sealed record CompleteAdminInvitationRequest(string Token, string Password);
public interface IDecideAdminInvitation
{
    Task<AdminInvitationResult> ExecuteAsync(DecideAdminInvitationRequest request, CancellationToken cancellationToken);
}
public sealed record DecideAdminInvitationRequest(Guid ActorId, Guid InvitationId, bool Approve, string CurrentPassword);
public enum AdminInvitationOutcome { Accepted, Completed, InvalidCredentials, Forbidden, InvalidEmail, InvalidToken, PasswordRejected, Conflict, NoApprover, NotFound, Decided }
public sealed record AdminInvitationResult(AdminInvitationOutcome Outcome, Guid? InvitationId = null, string? Rule = null);
