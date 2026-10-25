using Ouroboros.Identity.Application.Notifications;

namespace Ouroboros.Identity.Infrastructure.Email;

// Usado quando nenhum provedor de e-mail foi configurado: falha de forma explícita em vez de
// descartar mensagens em silêncio.
internal sealed class UnconfiguredEmailSender : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            $"Nenhum provedor de e-mail foi configurado ('{EmailOptions.SectionName}:Provider').");
}
