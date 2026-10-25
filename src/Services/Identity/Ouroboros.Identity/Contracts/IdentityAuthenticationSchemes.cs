namespace Ouroboros.Identity.Contracts;

// Esquemas de autenticação por público. Cada um valida a audiência do próprio canal; quem hospeda os
// dois públicos no mesmo processo escolhe o esquema pelo listener que recebeu a requisição.
public static class IdentityAuthenticationSchemes
{
    // Clientes e integrações: audiência Authentication:Jwt:Audience. É o esquema padrão do JwtBearer.
    public const string Customer = "Bearer";

    // Administradores: audiência Authentication:Jwt:AdminAudience (se ausente, a mesma do esquema do cliente).
    public const string Admin = "Bearer.Admin";
}
