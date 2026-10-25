using Microsoft.Extensions.Options;

namespace Ouroboros.Identity.Api.Security;

// A API atende dois públicos com audiências distintas (Authentication:Jwt:Audience e AdminAudience). Na biblioteca a
// audiência de administrador é opcional, para um host com um só público; aqui ela é obrigatória, senão os dois
// canais emitiriam e aceitariam a mesma audiência e a separação dependeria só dos papéis.
internal sealed class ApiAudienceSettings
{
    public const string SectionName = "Authentication:Jwt";

    public string? Audience { get; set; }

    public string? AdminAudience { get; set; }
}

internal sealed class ApiAudienceSettingsValidator : IValidateOptions<ApiAudienceSettings>
{
    public ValidateOptionsResult Validate(string? name, ApiAudienceSettings options)
    {
        if (string.IsNullOrWhiteSpace(options.AdminAudience))
        {
            return ValidateOptionsResult.Fail($"'{ApiAudienceSettings.SectionName}:AdminAudience' não foi configurada: a API de Identity exige uma audiência própria para administradores.");
        }

        return string.Equals(options.Audience, options.AdminAudience, StringComparison.Ordinal)
            ? ValidateOptionsResult.Fail($"'{ApiAudienceSettings.SectionName}:AdminAudience' deve ser diferente de '{ApiAudienceSettings.SectionName}:Audience'.")
            : ValidateOptionsResult.Success;
    }
}
