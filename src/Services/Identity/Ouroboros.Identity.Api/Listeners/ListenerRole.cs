using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;

namespace Ouroboros.Identity.Api.Listeners;

// Público de um listener. O papel vem de quem aceitou a conexão, nunca de cabeçalho, caminho ou corpo da
// requisição, que o cliente controla.
public enum ListenerRole
{
    Public,
    Admin,
}

public interface IListenerRoleResolver
{
    ListenerRole Resolve(HttpContext context);
}

// Lê o papel gravado na conexão pelo listener do Kestrel (KestrelListeners). Sem papel (por exemplo, um
// endpoint do Kestrel não configurado por este serviço), vale o público: o administrativo nunca é o padrão.
internal sealed class ConnectionListenerRoleResolver : IListenerRoleResolver
{
    internal const string ConnectionItemKey = "Ouroboros.Identity.Api.ListenerRole";

    public ListenerRole Resolve(HttpContext context) =>
        context.Features.Get<IConnectionItemsFeature>()?.Items.TryGetValue(ConnectionItemKey, out var role) == true && role is ListenerRole listener
            ? listener
            : ListenerRole.Public;
}
