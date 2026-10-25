using System.Net;
using Microsoft.Extensions.Options;

namespace Ouroboros.Identity.Api.Listeners;

// Configuração dos listeners (seção Identity:Listeners). O público atende as rotas externas; o
// administrativo atende só api/v{version}/admin/... e, por padrão, só em loopback.
internal sealed class ListenerOptions
{
    public const string SectionName = "Identity:Listeners";

    public ListenerEndpoints Public { get; set; } = new();

    public AdminListenerEndpoints Admin { get; set; } = new();
}

internal class ListenerEndpoints
{
    public string[] Urls { get; set; } = [];
}

internal sealed class AdminListenerEndpoints : ListenerEndpoints
{
    // Expor o listener administrativo fora de loopback (rede interna) é uma decisão explícita.
    public bool AllowNonLoopback { get; set; }
}

internal sealed class ListenerOptionsValidator : IValidateOptions<ListenerOptions>
{
    public ValidateOptionsResult Validate(string? name, ListenerOptions options)
    {
        var failures = new List<string>();
        var publicEndpoints = Check("Public", options.Public.Urls, failures);
        var adminEndpoints = Check("Admin", options.Admin.Urls, failures);

        foreach (var (host, port) in adminEndpoints)
        {
            if (!options.Admin.AllowNonLoopback && !IsLoopback(host))
            {
                failures.Add(
                    $"'{ListenerOptions.SectionName}:Admin:Urls' contém o host '{host}', que não é loopback. " +
                    $"O listener administrativo só escuta em loopback; para escutar em rede interna, defina '{ListenerOptions.SectionName}:Admin:AllowNonLoopback' como true.");
            }

            if (publicEndpoints.Any(other => other.Port == port))
            {
                failures.Add($"A porta {port} não pode ser usada pelos listeners público e administrativo ao mesmo tempo.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    internal static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    private static List<(string Host, int Port)> Check(string listener, string[] urls, List<string> failures)
    {
        var endpoints = new List<(string, int)>();
        if (urls.Length == 0)
        {
            failures.Add($"'{ListenerOptions.SectionName}:{listener}:Urls' não foi configurado.");
        }

        foreach (var url in urls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")
                || uri.AbsolutePath != "/"
                || uri.Port <= 0)
            {
                failures.Add($"'{ListenerOptions.SectionName}:{listener}:Urls' contém um endereço inválido: '{url}'. Use http(s)://host:porta.");
                continue;
            }

            if (!IsLoopback(uri.Host) && !IPAddress.TryParse(uri.Host.Trim('[', ']'), out _))
            {
                failures.Add($"'{ListenerOptions.SectionName}:{listener}:Urls' deve usar localhost ou um endereço IP: '{url}'.");
                continue;
            }

            endpoints.Add((uri.Host, uri.Port));
        }

        return endpoints;
    }
}
