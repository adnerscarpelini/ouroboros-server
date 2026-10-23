namespace Ouroboros.Hosting;

// Público atendido por um host: a API externa (app e integrações) ou o backoffice (rede interna).
public enum HostAudience
{
    External,
    Backoffice,
}
