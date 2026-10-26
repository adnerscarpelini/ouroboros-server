using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Ouroboros.Identity.Infrastructure.Messaging;

internal sealed class EmailIntentOptions
{
    public const string SectionName = "Identity:EmailIntents";
    public string PublicKeyRingPath { get; set; } = "";
    public string SupersessionKeyPath { get; set; } = "";
    public bool MaintenanceEnabled { get; set; } = true;
    public string? BackupDirectory { get; set; }
}

internal sealed class EmailIntentOptionsValidator(IConfiguration configuration) : IValidateOptions<EmailIntentOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailIntentOptions options) =>
        configuration.GetSection("Identity:Email").Exists()
            ? ValidateOptionsResult.Fail("Identity:Email foi removido; o transporte pertence a Notifications. Configure Identity:EmailIntents.")
            : string.IsNullOrWhiteSpace(options.PublicKeyRingPath) || !Path.IsPathFullyQualified(options.PublicKeyRingPath)
            || string.IsNullOrWhiteSpace(options.SupersessionKeyPath) || !Path.IsPathFullyQualified(options.SupersessionKeyPath)
            ? ValidateOptionsResult.Fail("Identity:EmailIntents exige caminhos externos absolutos para pública de Notifications e chave de substituição.")
            : ValidateOptionsResult.Success;
}
