using Ouroboros.Identity.Domain.EmailTokens;
using Ouroboros.Identity.Domain.Notifications;

namespace Ouroboros.Identity.Application.PasswordResets;

// Scoped no mesmo contexto de ISessionStore. Consumo e troca de senha participam da transação
// aberta pela porta de sessões: usuário -> sessões ordenadas -> tokens, sem commit parcial.
internal interface IPasswordResetStore
{
    Task<PasswordResetAccount?> FindCustomerAsync(string email, CancellationToken cancellationToken);
    Task<PasswordResetAccount?> FindActiveUserAsync(long userId, CancellationToken cancellationToken);
    Task<PasswordResetToken?> FindPendingTokenAsync(byte[] hash, DateTimeOffset now, CancellationToken cancellationToken);
    Task ReplacePendingTokenAsync(EmailToken token, EmailIntent intent, DateTimeOffset now, CancellationToken cancellationToken);
    Task<int> ConsumeTokenAsync(long tokenId, DateTimeOffset now, CancellationToken cancellationToken);
    Task ChangePasswordAsync(long userId, string passwordHash, DateTimeOffset now, CancellationToken cancellationToken);
}

internal sealed record PasswordResetAccount(long Id, string Email, string PasswordHash);
internal sealed record PasswordResetToken(long Id, long? UserId);
