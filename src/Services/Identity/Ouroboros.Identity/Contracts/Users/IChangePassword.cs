using Ouroboros.Identity.Contracts.Sessions;

namespace Ouroboros.Identity.Contracts.Users;

public interface IChangePassword
{
    Task<ChangePasswordResult> ExecuteAsync(ChangePasswordRequest request, CancellationToken cancellationToken);
}

public sealed record ChangePasswordRequest(Guid UserId, Guid SessionId, LoginChannel Channel, string CurrentPassword, string NewPassword);
public enum ChangePasswordOutcome { Changed, InvalidCredentials, PasswordRejected }
public sealed record ChangePasswordResult(ChangePasswordOutcome Outcome, string? Rule = null);
