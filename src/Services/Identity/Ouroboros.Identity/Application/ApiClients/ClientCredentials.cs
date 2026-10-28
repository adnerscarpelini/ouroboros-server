using System.Security.Cryptography;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Application.Sessions;
using Ouroboros.Identity.Contracts.ApiClients;

namespace Ouroboros.Identity.Application.ApiClients;

internal sealed class ClientCredentials(IApiClientStore store, ISessionStore transactions, IAccessTokenIssuer issuer, TimeProvider clock) : IClientCredentials
{
    public async Task<ClientCredentialsResult> ExecuteAsync(ClientCredentialsRequest request, CancellationToken cancellationToken)
    {
        if (request.GrantType != "client_credentials") { return new(ClientCredentialsOutcome.UnsupportedGrant); }
        if (!Guid.TryParse(request.ClientId, out var id) || id == Guid.Empty || request.ClientSecret is not { Length: > 0 and <= 256 })
        { return new(ClientCredentialsOutcome.InvalidCredentials); }
        var hash = OpaqueTokens.Hash(request.ClientSecret);
        var snapshot = await store.ReadCredentialAsync(id, cancellationToken);
        if (snapshot is null || !snapshot.IsActive || !CryptographicOperations.FixedTimeEquals(hash, snapshot.SecretHash))
        { return new(ClientCredentialsOutcome.InvalidCredentials); }
        await using var transaction = await transactions.BeginTransactionAsync(cancellationToken);
        await store.LockAsync(id, cancellationToken);
        var current = await store.ReadCredentialAsync(id, cancellationToken);
        if (current is null || !current.IsActive || !CryptographicOperations.FixedTimeEquals(hash, current.SecretHash))
        { return new(ClientCredentialsOutcome.InvalidCredentials); }
        var now = clock.GetUtcNow();
        var access = issuer.IssueClient(id);
        await transaction.CommitAsync(cancellationToken);
        return new(ClientCredentialsOutcome.Issued, access.Token, (long)(access.ExpiresAt - now).TotalSeconds);
    }
}

internal sealed class ValidateApiClient(IApiClientStore store) : IValidateApiClient
{
    public Task<bool> ExecuteAsync(Guid clientId, CancellationToken cancellationToken) => store.IsActiveAsync(clientId, cancellationToken);
}
