using System.Text;
using Microsoft.Extensions.Options;

namespace Ouroboros.Modules.Identity.Infrastructure.Security;

// Configuração dos tokens (seção Authentication:Jwt). Emissor e tempos ficam no appsettings de
// cada host; a audiência também, porque cada host aceita só a sua. A chave vem de fora do Git
// (user-secrets em desenvolvimento).
internal sealed class JwtOptions
{
    public const string SectionName = "Authentication:Jwt";

    // HS256 exige chave de pelo menos 256 bits.
    public const int MinimumSigningKeyBytes = 32;

    public string Issuer { get; set; } = "";

    public string Audience { get; set; } = "";

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
