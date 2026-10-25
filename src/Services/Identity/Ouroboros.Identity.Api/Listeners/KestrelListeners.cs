using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Ouroboros.Identity.Api.Listeners;

// Abre um endpoint do Kestrel por URL configurada e grava na conexão o público do listener que a aceitou.
// Endpoints explícitos substituem ASPNETCORE_URLS e --urls, que não conseguem expor o listener administrativo.
internal static class KestrelListeners
{
    public static void Configure(KestrelServerOptions kestrel, ListenerOptions options)
    {
        foreach (var url in options.Public.Urls)
        {
            Listen(kestrel, url, ListenerRole.Public);
        }

        foreach (var url in options.Admin.Urls)
        {
            Listen(kestrel, url, ListenerRole.Admin);
        }
    }

    private static void Listen(KestrelServerOptions kestrel, string url, ListenerRole role)
    {
        var uri = new Uri(url);

        void Configure(ListenOptions listen)
        {
            listen.Use(next => context =>
            {
                context.Items[ConnectionListenerRoleResolver.ConnectionItemKey] = role;
                return next(context);
            });
            if (uri.Scheme == Uri.UriSchemeHttps)
            {
                listen.UseHttps();
            }
        }

        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            kestrel.ListenLocalhost(uri.Port, Configure);
        }
        else
        {
            kestrel.Listen(IPAddress.Parse(uri.Host.Trim('[', ']')), uri.Port, Configure);
        }
    }
}
