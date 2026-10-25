namespace Ouroboros.Hosting;

// Público atendido por um host: a API externa (app e integrações), o backoffice (rede interna) ou um
// serviço que atende os dois e define a política de cada endpoint por convenção (veja Ouroboros.Identity.Api).
public enum HostAudience
{
    External,
    Backoffice,
    Service,
}
