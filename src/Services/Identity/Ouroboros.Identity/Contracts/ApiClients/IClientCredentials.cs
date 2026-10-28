namespace Ouroboros.Identity.Contracts.ApiClients;

public interface IClientCredentials
{
    Task<ClientCredentialsResult> ExecuteAsync(ClientCredentialsRequest request, CancellationToken cancellationToken);
}
public interface IValidateApiClient
{
    Task<bool> ExecuteAsync(Guid clientId, CancellationToken cancellationToken);
}
public sealed record ClientCredentialsRequest(string GrantType, string? ClientId, string? ClientSecret);
public enum ClientCredentialsOutcome { Issued, InvalidCredentials, UnsupportedGrant }
public sealed record ClientCredentialsResult(ClientCredentialsOutcome Outcome, string? AccessToken = null, long ExpiresIn = 0)
{
    public override string ToString() => Outcome.ToString();
}
