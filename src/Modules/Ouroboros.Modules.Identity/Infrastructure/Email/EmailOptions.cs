using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ouroboros.Modules.Identity.Infrastructure.Email;

internal enum EmailProvider
{
    // Nenhum envio configurado: tentar enviar é erro explícito. Um adaptador SMTP entra quando houver
    // infraestrutura.
    None,

    // Desenvolvimento: grava cada mensagem como .eml numa pasta local, fora do Git.
    PickupDirectory,
}

// Seção Identity:Email.
internal sealed class EmailOptions
{
    public const string SectionName = "Identity:Email";

    public EmailProvider Provider { get; set; } = EmailProvider.None;

    // Pasta das mensagens; relativa ao diretório do host quando não for absoluta.
    public string PickupDirectory { get; set; } = "";

    public string From { get; set; } = "nao-responda@ouroboros.local";
}

internal sealed class EmailOptionsValidator(IHostEnvironment environment) : IValidateOptions<EmailOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        if (options.Provider != EmailProvider.PickupDirectory)
        {
            return ValidateOptionsResult.Success;
        }

        if (!environment.IsDevelopment())
        {
            return ValidateOptionsResult.Fail(
                $"O adaptador de e-mail local ('{EmailOptions.SectionName}:Provider' = {EmailProvider.PickupDirectory}) " +
                $"só pode ser usado em Development; o ambiente atual é '{environment.EnvironmentName}'.");
        }

        return string.IsNullOrWhiteSpace(options.PickupDirectory)
            ? ValidateOptionsResult.Fail($"'{EmailOptions.SectionName}:PickupDirectory' não foi configurado.")
            : ValidateOptionsResult.Success;
    }
}
