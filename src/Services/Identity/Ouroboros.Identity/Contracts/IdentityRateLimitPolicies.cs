namespace Ouroboros.Identity.Contracts;

// Nomes das políticas de limite de requisições por IP dos endpoints anônimos de autenticação, usados
// pelos controllers dos hosts em [EnableRateLimiting(...)]. Os limites ficam na configuração
// (Identity:RateLimiting:Policies:<nome>), e cada política tem o próprio contador.
public static class IdentityRateLimitPolicies
{
    public const string Login = "identity-login";

    public const string Refresh = "identity-refresh";

    public const string Logout = "identity-logout";

    public const string Registration = "identity-registration";

    public const string RegistrationCompletion = "identity-registration-completion";

    public const string PasswordReset = "identity-password-reset";

    public const string PasswordResetCompletion = "identity-password-reset-completion";
}
