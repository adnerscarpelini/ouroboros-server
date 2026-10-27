namespace Ouroboros.Identity.Contracts.Parameters;

public interface IChangeParameter
{
    Task<ChangeParameterResult> ExecuteAsync(ChangeParameterRequest request, CancellationToken cancellationToken);
}

public sealed record ChangeParameterRequest(Guid UserId, string Key, int Revision, string CurrentPassword, string? Value, string? Description);
public enum ChangeParameterOutcome { Changed, InvalidCredentials, NotFound, Conflict, InvalidValue }
public sealed record ChangeParameterResult(ChangeParameterOutcome Outcome, string? Rule = null);
