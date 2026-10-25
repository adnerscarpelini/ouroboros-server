namespace Ouroboros.Identity.Contracts;

// Chaves de HttpContext.Items que o host define para o Identity.
public static class IdentityHttpItems
{
    // Separa os contadores de rate limit por público: o mesmo IP tem um contador de login no listener
    // externo e outro no administrativo, como quando eram processos distintos.
    public const string RateLimitScope = "Ouroboros.Identity.RateLimitScope";
}
