using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Ouroboros.Modules.Identity.Application.Notifications;

namespace Ouroboros.IntegrationTests;

// Guarda as mensagens em vez de enviá-las, para os testes lerem os links. Pode simular falha no envio.
internal sealed partial class CapturingEmailSender : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Messages { get; } = new();

    public bool Fail { get; set; }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new InvalidOperationException("Falha simulada no envio de e-mail.");
        }

        Messages.Enqueue(message);
        return Task.CompletedTask;
    }

    // Token do último link enviado para o endereço (parâmetro token da URL).
    public string TokenSentTo(string email)
    {
        var message = Messages.Last(message => message.To == email);
        return Uri.UnescapeDataString(TokenInLink().Match(message.Body).Groups["token"].Value);
    }

    [GeneratedRegex(@"[?&]token=(?<token>[^\s&]+)")]
    private static partial Regex TokenInLink();
}
