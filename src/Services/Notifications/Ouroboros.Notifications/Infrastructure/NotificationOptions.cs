using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ouroboros.Notifications.Infrastructure;

internal sealed class NotificationOptions
{
    public const string SectionName = "Notifications";
    public ConsumerSettings Consumer { get; set; } = new();
    public BrokerSettings Broker { get; set; } = new();
    public EmailSettings Email { get; set; } = new();
    public string PrivateKeyRingPath { get; set; } = "";
    public string FingerprintKeyRingPath { get; set; } = "";
    public string? BackupDirectory { get; set; }
    public int ProcessingPollSeconds { get; set; } = 1;
    public BrokerSettings FailuresBroker { get; set; } = new() { User = "notifications_failures" };
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
        if (options.Consumer.Enabled && options.Email.Provider == "None")
        {
            return ValidateOptionsResult.Fail("Processamento exige um provedor explícito de Notifications.");
        }
        if (options.Consumer.Enabled && (string.IsNullOrWhiteSpace(options.PrivateKeyRingPath)
            || !Path.IsPathFullyQualified(options.PrivateKeyRingPath) || options.Broker.User != "notifications_consumer"
            || string.IsNullOrWhiteSpace(options.Broker.Password) || options.Broker.Password.Length < 32
            || string.IsNullOrWhiteSpace(options.Broker.Host) || options.Broker.Port is < 1 or > 65535))
        {
            return ValidateOptionsResult.Fail("Configuração de consumo/proteção de Notifications incompleta ou inválida.");
        }
        if (options.Consumer.Enabled)
        {
            if (!Path.IsPathFullyQualified(options.FingerprintKeyRingPath) || options.ProcessingPollSeconds is < 1 or > 30
                || options.FailuresBroker.User != "notifications_failures" || options.FailuresBroker.Password.Length < 32
                || options.FailuresBroker.Password == options.Broker.Password || string.IsNullOrWhiteSpace(options.FailuresBroker.Host)
                || options.FailuresBroker.Port is < 1 or > 65535)
            { return ValidateOptionsResult.Fail("Configuração de impressão, execução ou publicação sanitizada incompleta."); }
        }
        if (options.Consumer.Enabled || !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Notifications")))
        {
            try
            {
                var database = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Notifications"));
                if (database.Username != "notifications_runtime" || string.IsNullOrWhiteSpace(database.Password)
                    || string.IsNullOrWhiteSpace(database.Database) || string.IsNullOrWhiteSpace(database.Host))
                { return ValidateOptionsResult.Fail("Configure somente a conexão própria de runtime de Notifications."); }
            }
            catch (ArgumentException) { return ValidateOptionsResult.Fail("Conexão própria de Notifications inválida."); }
        }
        return ValidateOptionsResult.Success;
    }
}
