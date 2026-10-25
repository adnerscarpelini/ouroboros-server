using Ouroboros.Identity.Api.Listeners;

namespace Ouroboros.Identity.Api.Routing;

// Público em que um endpoint existe: as rotas externas só no listener público, as administrativas só no
// administrativo. Gravado em todo endpoint pela convenção de rota (ListenerRouteConvention).
public sealed record ListenerMetadata(ListenerRole Role);
