using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Ouroboros.Messaging;

namespace Ouroboros.Identity.Infrastructure.Messaging;

internal sealed class EmailPublisherOptions
{
    public const string SectionName = "Identity:EmailPublisher";
    public bool Enabled { get; set; }
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string User { get; set; } = "identity_publisher";
    public string Password { get; set; } = "";
    public int BatchSize { get; set; } = 4;
    public int PollSeconds { get; set; } = 2;
}

internal sealed class EmailPublisherOptionsValidator(IHostEnvironment environment) : IValidateOptions<EmailPublisherOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailPublisherOptions options)
    {
        if (!options.Enabled) { return ValidateOptionsResult.Success; }
        if (options.BatchSize is < 1 or > 8 || options.PollSeconds is < 1 or > 30 || options.User != "identity_publisher")
        { return ValidateOptionsResult.Fail("Limites ou identidade do publicador inválidos."); }
        try { _ = RabbitConnectionFactory.Create(options.Host, options.Port, options.User, options.Password, environment.IsDevelopment()); }
        catch (ArgumentException) { return ValidateOptionsResult.Fail("Configuração do broker do publicador inválida."); }
        return ValidateOptionsResult.Success;
    }
}
