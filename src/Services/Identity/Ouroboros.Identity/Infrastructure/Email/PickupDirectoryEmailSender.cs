using System.Globalization;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ouroboros.Identity.Application.Notifications;

namespace Ouroboros.Identity.Infrastructure.Email;

// Adaptador de desenvolvimento (pickup directory): grava cada mensagem como um arquivo .eml, que
// qualquer cliente de e-mail abre. O log registra só o nome do arquivo, nunca o corpo.
internal sealed partial class PickupDirectoryEmailSender(
    IOptions<EmailOptions> options,
    IHostEnvironment environment,
    TimeProvider timeProvider,
    ILogger<PickupDirectoryEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var directory = Path.GetFullPath(options.Value.PickupDirectory, environment.ContentRootPath);
        Directory.CreateDirectory(directory);

        var now = timeProvider.GetUtcNow();
        var fileName = $"{now:yyyyMMddHHmmssfff}-{Guid.CreateVersion7():N}.eml";
        await File.WriteAllTextAsync(
            Path.Combine(directory, fileName),
            Format(options.Value.From, message, now),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        LogSaved(logger, fileName);
    }

    // Mensagem MIME simples (RFC 5322), em texto UTF-8. O assunto com acentos é codificado (RFC 2047).
    internal static string Format(string from, EmailMessage message, DateTimeOffset date)
    {
        var builder = new StringBuilder();
        builder.Append("From: ").Append(from).Append("\r\n");
        builder.Append("To: ").Append(message.To).Append("\r\n");
        builder.Append("Subject: ").Append(EncodeHeader(message.Subject)).Append("\r\n");
        builder.Append("Date: ").Append(date.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture)).Append("\r\n");
        builder.Append("Message-ID: <").Append(Guid.CreateVersion7().ToString("N")).Append("@ouroboros.local>\r\n");
        builder.Append("MIME-Version: 1.0\r\n");
        builder.Append("Content-Type: text/plain; charset=utf-8\r\n");
        builder.Append("Content-Transfer-Encoding: 8bit\r\n");
        builder.Append("\r\n");
        builder.Append(message.Body.ReplaceLineEndings("\r\n"));
        return builder.ToString();
    }

    private static string EncodeHeader(string value) =>
        value.All(char.IsAscii) ? value : $"=?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}?=";

    [LoggerMessage(Level = LogLevel.Information, Message = "E-mail gravado no pickup directory: {FileName}")]
    private static partial void LogSaved(ILogger logger, string fileName);
}
