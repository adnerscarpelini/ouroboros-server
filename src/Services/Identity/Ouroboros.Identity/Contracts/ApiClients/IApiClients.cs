using System.Text.Json.Serialization;

namespace Ouroboros.Identity.Contracts.ApiClients;

public interface IRegisterApiClient
{
    Task<ApiClientResult> ExecuteAsync(RegisterApiClientRequest request, CancellationToken cancellationToken);
}
public interface IGetApiClient
{
    Task<ApiClientResult> ExecuteAsync(Guid actorId, Guid clientId, CancellationToken cancellationToken);
}
public sealed record RegisterApiClientRequest(Guid ActorId, string Name, string? Description, string CurrentPassword);
public enum ApiClientOutcome { Registered, Found, InvalidCredentials, Forbidden, InvalidInput, NotFound }
public sealed record ApiClientModel([property: JsonPropertyName("client_id")] Guid ClientId,
    string Name, string Description, bool IsActive, Guid CreatedBy, DateTimeOffset CreatedAt);
public sealed record ApiClientResult(ApiClientOutcome Outcome, ApiClientModel? Client = null, string? Secret = null)
{
    public override string ToString() => Outcome.ToString();
}
