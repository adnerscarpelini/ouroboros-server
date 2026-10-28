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
public interface IDeactivateApiClient
{
    Task<ApiClientResult> ExecuteAsync(DeactivateApiClientRequest request, CancellationToken cancellationToken);
}
public sealed record DeactivateApiClientRequest(Guid ActorId, Guid ClientId, string CurrentPassword);
public sealed record RegisterApiClientRequest(Guid ActorId, string Name, string? Description, string CurrentPassword);
public enum ApiClientOutcome { Registered, Found, InvalidCredentials, Forbidden, InvalidInput, NotFound, Deactivated }
public sealed record ApiClientModel([property: JsonPropertyName("client_id")] Guid ClientId,
    string Name, string Description, bool IsActive, Guid CreatedBy, DateTimeOffset CreatedAt,
    Guid? DeactivatedBy = null, DateTimeOffset? DeactivatedAt = null);
public sealed record ApiClientResult(ApiClientOutcome Outcome, ApiClientModel? Client = null, string? Secret = null)
{
    public override string ToString() => Outcome.ToString();
}
