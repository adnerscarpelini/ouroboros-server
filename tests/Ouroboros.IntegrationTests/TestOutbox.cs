using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ouroboros.Identity.Application.Notifications;
using Ouroboros.Identity.Domain.Notifications;
using Ouroboros.Identity.Infrastructure.Persistence;
using Ouroboros.Messaging;
using Ouroboros.Notifications.Infrastructure;

namespace Ouroboros.IntegrationTests;

// Lê somente intenções já commitadas. Não substitui a persistência ou a criptografia de produção.
internal static partial class TestOutbox
{
    internal static async Task<List<EmailMessage>> ReadAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var records = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().EmailIntents.AsNoTracking()
            .Where(intent => intent.ProtectedEnvelope != null).OrderBy(intent => intent.Id).ToListAsync(TestContext.Current.CancellationToken);
        var reader = new ProtectedEmailPayloadReader(Options.Create(new NotificationOptions { PrivateKeyRingPath = TestSigningKeys.EmailPrivateKeyRingPath }));
        return records.Select(record =>
        {
            Assert.True(EnvelopeCodec.TryDecode(record.ProtectedEnvelope!, "notification.email.requested", 1, "identity", out var message, out _));
            var content = reader.Read(message!);
            return new EmailMessage(content.Recipient, content.Subject, content.Text);
        }).ToList();
    }

    internal static async Task<string> TokenAsync(IServiceProvider services, string email)
    {
        var message = (await ReadAsync(services)).Last(message => message.To == email);
        return Uri.UnescapeDataString(TokenInLink().Match(message.Body).Groups["token"].Value);
    }

    [GeneratedRegex(@"[?&]token=(?<token>[^\s&]+)")]
    private static partial Regex TokenInLink();
}

internal sealed class FailingEmailIntentFactory : IEmailIntentFactory
{
    public EmailIntent Create(EmailMessage content, string purpose, DateTimeOffset now, DateTimeOffset expiresAt) =>
        throw new InvalidOperationException("Proteção indisponível no teste.");
}
