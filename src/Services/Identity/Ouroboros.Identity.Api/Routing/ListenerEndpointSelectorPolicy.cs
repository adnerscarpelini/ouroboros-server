using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection;
using Ouroboros.Identity.Api.Listeners;

namespace Ouroboros.Identity.Api.Routing;

// Endpoint de outro público nem entra na seleção: a rota administrativa chamada no listener público (e a externa
// no administrativo) é tratada como inexistente, com a mesma resposta de qualquer rota desconhecida e sem revelar que
// existe, inclusive com um método HTTP que a rota não declara (sem 405). Por isso é uma política de nó do roteador,
// avaliada antes da de método HTTP: o público é decidido a cada requisição, pela conexão que a aceitou.
internal sealed class ListenerEndpointSelectorPolicy : MatcherPolicy, INodeBuilderPolicy
{
    // HttpMethodMatcherPolicy usa -1000; as menores ordens executam primeiro.
    public override int Order => -2000;

    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints) =>
        endpoints.Any(endpoint => endpoint.Metadata.GetMetadata<ListenerMetadata>() is not null);

    // Um nó por público. Endpoint sem metadado de público (por exemplo, um futuro health check) vale nos dois.
    public IReadOnlyList<PolicyNodeEdge> GetEdges(IReadOnlyList<Endpoint> endpoints) =>
    [
        new PolicyNodeEdge(ListenerRole.Public, [.. endpoints.Where(endpoint => ServedTo(endpoint, ListenerRole.Public))]),
        new PolicyNodeEdge(ListenerRole.Admin, [.. endpoints.Where(endpoint => ServedTo(endpoint, ListenerRole.Admin))]),
    ];

    public PolicyJumpTable BuildJumpTable(int exitDestination, IReadOnlyList<PolicyJumpTableEdge> edges) =>
        new ListenerJumpTable(
            exitDestination,
            edges.Single(edge => (ListenerRole)edge.State == ListenerRole.Public).Destination,
            edges.Single(edge => (ListenerRole)edge.State == ListenerRole.Admin).Destination);

    private static bool ServedTo(Endpoint endpoint, ListenerRole role) =>
        endpoint.Metadata.GetMetadata<ListenerMetadata>() is not { } metadata || metadata.Role == role;

    private sealed class ListenerJumpTable(int exit, int publicDestination, int adminDestination) : PolicyJumpTable
    {
        public override int GetDestination(HttpContext httpContext) =>
            httpContext.RequestServices.GetRequiredService<IListenerRoleResolver>().Resolve(httpContext) switch
            {
                ListenerRole.Public => publicDestination,
                ListenerRole.Admin => adminDestination,
                _ => exit,
            };
    }
}
