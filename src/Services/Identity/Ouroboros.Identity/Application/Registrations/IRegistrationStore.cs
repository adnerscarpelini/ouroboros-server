using Ouroboros.Identity.Domain.EmailTokens;
using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Application.Registrations;

// Cada escrita confirma a operação inteira; o transporte só é chamado depois do retorno.
internal interface IRegistrationStore
{
    Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken);
    Task<RegistrationToken?> FindPendingTokenAsync(byte[] hash, DateTimeOffset now, CancellationToken cancellationToken);
    Task ReplacePendingTokenAsync(EmailToken token, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(long tokenId, User user, DateTimeOffset now, CancellationToken cancellationToken);
}

internal sealed record RegistrationToken(long Id, string Email);
