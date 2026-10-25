namespace Ouroboros.Identity.Contracts.Sessions;

// Caso de uso: encerrar todas as sessões do usuário autenticado, inclusive a atual. Revoga os refresh
// tokens ativos; os access tokens já emitidos continuam válidos até expirar. É idempotente.
public interface ILogoutAll
{
    Task ExecuteAsync(LogoutAllRequest request, CancellationToken cancellationToken);
}

// UserId é o ExternalId do usuário (claim sub do access token).
public sealed record LogoutAllRequest(Guid UserId);
