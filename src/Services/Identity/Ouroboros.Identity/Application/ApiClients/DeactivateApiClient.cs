using Ouroboros.Identity.Application.Administrators;
using Ouroboros.Identity.Application.Notifications;
using Ouroboros.Identity.Application.Sessions;
using Ouroboros.Identity.Contracts.ApiClients;

namespace Ouroboros.Identity.Application.ApiClients;

internal sealed class DeactivateApiClient(AdminAuthorization authorization, ISessionStore sessions, IApiClientStore store,
    IAdminDirectory admins, IEmailIntentFactory emails, TimeProvider clock) : IDeactivateApiClient
{
    public async Task<ApiClientResult> ExecuteAsync(DeactivateApiClientRequest request, CancellationToken cancellationToken)
    {
        var (actor, access) = await authorization.AuthenticateAsync(request.ActorId, request.CurrentPassword, cancellationToken);
        if (access != AdminAccess.Allowed) { return RegisterApiClient.Denied(access); }
        await using var transaction = await sessions.BeginTransactionAsync(cancellationToken);
        access = await authorization.LockAndConfirmAsync(actor!, cancellationToken);
        if (access != AdminAccess.Allowed) { return RegisterApiClient.Denied(access); }
        await store.LockAsync(request.ClientId, cancellationToken);
        var client = await store.FindAsync(request.ClientId, cancellationToken);
        if (client is null) { return new(ApiClientOutcome.NotFound); }
        var now = clock.GetUtcNow();
        if (client.Deactivate(actor!.ExternalId, now))
        {
            store.AddIntents((await admins.ActiveAdminsAsync(cancellationToken)).Select(admin => emails.Create(new(admin.Email,
                "Integrador desativado", $"Integrador {client.ExternalId} desativado por {actor.ExternalId} em {now:O}."),
                "api-client-notice", now, now.AddHours(24))));
            await store.SaveAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(ApiClientOutcome.Deactivated);
    }
}
