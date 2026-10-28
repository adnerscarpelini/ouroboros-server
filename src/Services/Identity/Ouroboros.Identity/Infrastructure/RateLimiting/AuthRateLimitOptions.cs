using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Ouroboros.Identity.Contracts;

namespace Ouroboros.Identity.Infrastructure.RateLimiting;

// Seção Identity:RateLimiting: uma janela fixa por IP para cada política, sem fila.
internal sealed class AuthRateLimitOptions
{
    public const string SectionName = "Identity:RateLimiting";

    // Políticas que precisam estar configuradas porque algum endpoint as usa.
    public static readonly IReadOnlyList<string> RequiredPolicies =
    [
        IdentityRateLimitPolicies.Keys,
        IdentityRateLimitPolicies.Login,
        IdentityRateLimitPolicies.Refresh,
        IdentityRateLimitPolicies.Logout,
        IdentityRateLimitPolicies.Registration,
        IdentityRateLimitPolicies.RegistrationCompletion,
        IdentityRateLimitPolicies.PasswordReset,
        IdentityRateLimitPolicies.PasswordResetCompletion,
        IdentityRateLimitPolicies.PasswordChange,
        IdentityRateLimitPolicies.ParameterWrite,
        IdentityRateLimitPolicies.AdminActions,
    ];

    public Dictionary<string, FixedWindowPolicy> Policies { get; set; } = [];
}

internal sealed class FixedWindowPolicy
{
    // Requisições aceitas por IP dentro de uma janela.
    public int PermitLimit { get; set; }

    public TimeSpan Window { get; set; }
}

// Recebe a configuração crua porque o binder descarta em silêncio uma política com valor que não
// converte (por exemplo, PermitLimit = "abc"), e ela pareceria apenas ausente.
internal sealed class AuthRateLimitOptionsValidator(IConfiguration configuration) : IValidateOptions<AuthRateLimitOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthRateLimitOptions options)
    {
        var failures = new List<string>();
        foreach (var policy in configuration.GetSection($"{AuthRateLimitOptions.SectionName}:Policies").GetChildren())
        {
            if (policy[nameof(FixedWindowPolicy.PermitLimit)] is { } permitLimit
                && !int.TryParse(permitLimit, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
            {
                failures.Add($"'{policy.Path}:{nameof(FixedWindowPolicy.PermitLimit)}' tem o valor inválido '{permitLimit}'; use um inteiro.");
            }

            if (policy[nameof(FixedWindowPolicy.Window)] is { } window
                && !TimeSpan.TryParse(window, CultureInfo.InvariantCulture, out _))
            {
                failures.Add($"'{policy.Path}:{nameof(FixedWindowPolicy.Window)}' tem o valor inválido '{window}'; use o formato hh:mm:ss.");
            }
        }

        if (failures.Count > 0)
        {
            return ValidateOptionsResult.Fail(failures);
        }

        foreach (var policy in AuthRateLimitOptions.RequiredPolicies.Where(policy => !options.Policies.ContainsKey(policy)))
        {
            failures.Add($"A política '{AuthRateLimitOptions.SectionName}:Policies:{policy}' não foi configurada.");
        }

        foreach (var (policy, limits) in options.Policies)
        {
            if (limits.PermitLimit <= 0)
            {
                failures.Add($"'{AuthRateLimitOptions.SectionName}:Policies:{policy}:PermitLimit' deve ser positivo.");
            }

            if (limits.Window <= TimeSpan.Zero)
            {
                failures.Add($"'{AuthRateLimitOptions.SectionName}:Policies:{policy}:Window' deve ser positiva.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
