using System.Text;
using Microsoft.Extensions.Options;

namespace Ouroboros.Identity.Infrastructure.Security;

// Configuração dos tokens (seção Authentication:Jwt). Emissor, tempos e audiências ficam no appsettings
// do host; cada público aceita só a sua audiência. A chave vem de fora do Git (user-secrets em desenvolvimento).
internal sealed class JwtOptions
{
    public const string SectionName = "Authentication:Jwt";

    // HS256 exige chave de pelo menos 256 bits.
    public const int MinimumSigningKeyBytes = 32;

    public string Issuer { get; set; } = "";

    // Audiência dos tokens de cliente (papel Customer).
    public string Audience { get; set; } = "";

    // Audiência dos tokens de administrador. Ausente, vale Audience (um host com um só público).
    public string? AdminAudience { get; set; }

    public string AudienceOf(Ouroboros.Identity.Domain.Users.UserRole role) =>
        role == Ouroboros.Identity.Domain.Users.UserRole.Admin && !string.IsNullOrWhiteSpace(AdminAudience) ? AdminAudience : Audience;

    public string SigningKey { get; set; } = "";

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(7);

    public byte[] SigningKeyBytes => Encoding.UTF8.GetBytes(SigningKey);
}

internal sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            failures.Add($"'{JwtOptions.SectionName}:Issuer' não foi configurado.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            failures.Add($"'{JwtOptions.SectionName}:Audience' não foi configurado.");
        }

        if (!string.IsNullOrWhiteSpace(options.AdminAudience)
            && string.Equals(options.AdminAudience, options.Audience, StringComparison.Ordinal))
        {
            failures.Add($"'{JwtOptions.SectionName}:AdminAudience' deve ser diferente de '{JwtOptions.SectionName}:Audience'; omita-a em um host com um só público.");
        }

        if (string.IsNullOrWhiteSpace(options.SigningKey))
        {
            failures.Add(
                $"'{JwtOptions.SectionName}:SigningKey' não foi configurada. " +
                "Em desenvolvimento, defina-a com dotnet user-secrets (veja o README).");
        }
        else if (options.SigningKeyBytes.Length < JwtOptions.MinimumSigningKeyBytes)
        {
            failures.Add(
                $"'{JwtOptions.SectionName}:SigningKey' tem menos de {JwtOptions.MinimumSigningKeyBytes * 8} bits; " +
                $"use pelo menos {JwtOptions.MinimumSigningKeyBytes} bytes.");
        }

        if (options.AccessTokenLifetime <= TimeSpan.Zero)
        {
            failures.Add($"'{JwtOptions.SectionName}:AccessTokenLifetime' deve ser positivo.");
        }

        if (options.RefreshTokenLifetime <= TimeSpan.Zero)
        {
            failures.Add($"'{JwtOptions.SectionName}:RefreshTokenLifetime' deve ser positivo.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
