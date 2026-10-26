using System.Globalization;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Ouroboros.Notifications.Application;
using Ouroboros.Notifications.Contracts;

namespace Ouroboros.Notifications.Infrastructure.Email;

internal sealed class PickupEmailTransport(IOptions<NotificationOptions> options, IHostEnvironment environment, TimeProvider clock) : IEmailTransport
{
    public async Task SendAsync(Guid messageId, EmailContent content, CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment()) { throw new InvalidOperationException("Pickup exige Development."); }
        var directory = Path.GetFullPath(options.Value.Email.PickupDirectory, environment.ContentRootPath);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{messageId:N}.eml");
        if (File.Exists(path)) { return; }
        var temporary = Path.Combine(directory, $".{messageId:N}-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, Format(options.Value.Email.From, messageId, content, clock.GetUtcNow()),
                new UTF8Encoding(false), cancellationToken);
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { /* Outro envio idempotente já concluiu. */ }
        }
        finally { File.Delete(temporary); }
    }

    internal static string Format(string from, Guid messageId, EmailContent message, DateTimeOffset date)
    {
        var subject = message.Subject.All(char.IsAscii) ? message.Subject
            : $"=?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(message.Subject))}?=";
        var builder = new StringBuilder();
        builder.Append("From: ").Append(from).Append("\r\nTo: ").Append(message.Recipient);
        builder.Append("\r\nSubject: ").Append(subject);
        builder.Append("\r\nDate: ").Append(date.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture));
        builder.Append("\r\nMessage-ID: <").Append(messageId.ToString("N")).Append("@ouroboros.local>\r\n");
        builder.Append("MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\n");
        builder.Append(message.Text.ReplaceLineEndings("\r\n"));
        return builder.ToString();
    }
}
