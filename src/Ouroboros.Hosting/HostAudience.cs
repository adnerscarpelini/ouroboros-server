namespace Ouroboros.Hosting;

// Público atendido por um host: a API externa (app e integrações) ou o backoffice (rede interna).
public enum HostAudience
{
    External,
    Backoffice,
}

// Disponível pela DI para quem precisar saber em qual host está rodando (por exemplo, a audiência
// dos tokens).
public sealed record CurrentHost(HostAudience Audience);
