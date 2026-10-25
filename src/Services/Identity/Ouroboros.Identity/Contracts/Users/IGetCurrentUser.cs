using Ouroboros.Identity.Contracts.Sessions;

namespace Ouroboros.Identity.Contracts.Users;

// Caso de uso: dados do usuário autenticado, lidos do banco. Devolve null quando o usuário não existe,
// deixou de estar ativo ou não tem mais o papel do canal (host), mesmo com um access token ainda válido.
public interface IGetCurrentUser
{
    Task<CurrentUser?> ExecuteAsync(GetCurrentUserRequest request, CancellationToken cancellationToken);
}

// UserId é o ExternalId do usuário (claim sub do access token).
public sealed record GetCurrentUserRequest(Guid UserId, LoginChannel Channel);

// Lista fechada de campos: nunca o id interno, o hash da senha nem dados de bloqueio.
public sealed record CurrentUser(Guid Id, string Email, string Role);
