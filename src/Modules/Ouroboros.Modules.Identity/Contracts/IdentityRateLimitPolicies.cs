namespace Ouroboros.Modules.Identity.Contracts;

// Nomes das políticas de limite de requisições por IP dos endpoints anônimos de autenticação, usados
// pelos controllers dos hosts em [EnableRateLimiting(...)]. Os limites ficam na configuração
// (Identity:RateLimiting:Policies:<nome>), e cada política tem o próprio contador.
public static class IdentityRateLimitPolicies
{
    public const string Login = "identity-login";
}
