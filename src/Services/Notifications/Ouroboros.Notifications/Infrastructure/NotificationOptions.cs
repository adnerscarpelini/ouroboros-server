using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ouroboros.Notifications.Infrastructure;

internal sealed class NotificationOptions
{
    public const string SectionName = "Notifications";
    public ConsumerSettings Consumer { get; set; } = new();
    public BrokerSettings Broker { get; set; } = new();
    public EmailSettings Email { get; set; } = new();
    public string PrivateKeyRingPath { get; set; } = "";
}

internal sealed class ConsumerSettings { public bool Enabled { get; set; } }
internal sealed class BrokerSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string User { get; set; } = "notifications_consumer";
    public string Password { get; set; } = "";
}
internal sealed class EmailSettings
{
    public string Provider { get; set; } = "None";
    public string From { get; set; } = "nao-responda@ouroboros.local";
    public string PickupDirectory { get; set; } = "";
}

internal sealed class NotificationOptionsValidator(IConfiguration configuration, IHostEnvironment environment) : IValidateOptions<NotificationOptions>
{
    public ValidateOptionsResult Validate(string? name, NotificationOptions options)
    {
        if (configuration.GetSection("Identity:Email").Exists())
        {
            return ValidateOptionsResult.Fail("Configuração legada de e-mail recusada; use Notifications:Email.");
        }
        if (options.Email.Provider is not ("None" or "PickupDirectory"))
        {
            return ValidateOptionsResult.Fail("Notifications:Email:Provider inválido.");
        }
        if (options.Email.Provider == "PickupDirectory" && (!environment.IsDevelopment()
            || string.IsNullOrWhiteSpace(options.Email.PickupDirectory)))
        {
            return ValidateOptionsResult.Fail("PickupDirectory exige Development e pasta configurada.");
        }
        if (!MailAddress.TryCreate(options.Email.From, out var from) || from.Address != options.Email.From
            || options.Email.From.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            return ValidateOptionsResult.Fail("Notifications:Email:From inválido.");
        }
        if (options.Consumer.Enabled && (!environment.IsDevelopment() || options.Email.Provider == "None"))
        {
            return ValidateOptionsResult.Fail("Entrega direta exige Development e provedor explícito; aceitação durável ainda não está disponível.");
        }
        if (options.Consumer.Enabled && (string.IsNullOrWhiteSpace(options.PrivateKeyRingPath)
            || !Path.IsPathFullyQualified(options.PrivateKeyRingPath) || options.Broker.User != "notifications_consumer"
            || string.IsNullOrWhiteSpace(options.Broker.Password) || options.Broker.Password.Length < 32
            || string.IsNullOrWhiteSpace(options.Broker.Host) || options.Broker.Port is < 1 or > 65535))
        {
            return ValidateOptionsResult.Fail("Configuração de consumo/proteção de Notifications incompleta ou inválida.");
        }
        return ValidateOptionsResult.Success;
    }
}
