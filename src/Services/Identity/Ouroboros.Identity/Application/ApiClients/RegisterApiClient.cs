using Ouroboros.Identity.Application.Administrators;
using Ouroboros.Identity.Application.Notifications;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Application.Sessions;
using Ouroboros.Identity.Contracts.ApiClients;
using Ouroboros.Identity.Domain.ApiClients;

namespace Ouroboros.Identity.Application.ApiClients;

internal sealed class RegisterApiClient(AdminAuthorization authorization, ISessionStore sessions, IApiClientStore store,
    IAdminDirectory admins, IEmailIntentFactory emails, TimeProvider clock) : IRegisterApiClient
{
    public async Task<ApiClientResult> ExecuteAsync(RegisterApiClientRequest request, CancellationToken cancellationToken)
    {
        var (actor, access) = await authorization.AuthenticateAsync(request.ActorId, request.CurrentPassword, cancellationToken);
        if (access != AdminAccess.Allowed) { return Denied(access); }
        var (secret, hash) = OpaqueTokens.Generate();
        ApiClient client;
        try { client = ApiClient.Create(request.Name, request.Description, hash, actor!.ExternalId); }
        catch (ArgumentException) { return new(ApiClientOutcome.InvalidInput); }
        await using var transaction = await sessions.BeginTransactionAsync(cancellationToken);
        access = await authorization.LockAndConfirmAsync(actor!, cancellationToken);
        if (access != AdminAccess.Allowed) { return Denied(access); }
        var now = clock.GetUtcNow();
        store.Add(client);
        store.AddIntents((await admins.ActiveAdminsAsync(cancellationToken)).Select(admin => emails.Create(new(admin.Email,
            "Integrador cadastrado", $"Integrador {client.ExternalId} cadastrado por {actor!.ExternalId} em {now:O}."),
            "api-client-notice", now, now.AddHours(24))));
        await store.SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(ApiClientOutcome.Registered, Model(client), secret);
    }

    internal static ApiClientModel Model(ApiClient client) => new(client.ExternalId, client.Name, client.Description,
        client.IsActive, client.CreatedBy, client.CreatedAt);
    internal static ApiClientResult Denied(AdminAccess access) => new(access == AdminAccess.Forbidden ? ApiClientOutcome.Forbidden : ApiClientOutcome.InvalidCredentials);
}
