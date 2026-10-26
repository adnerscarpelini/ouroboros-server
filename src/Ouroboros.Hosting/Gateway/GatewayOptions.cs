using System.Net;
using Microsoft.Extensions.Options;

namespace Ouroboros.Hosting.Gateway;

public enum GatewayAudience
{
    External,
    Admin,
}

internal sealed record GatewayAudienceDescriptor(GatewayAudience Audience);

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    public string[] Urls { get; set; } = [];

    public bool AllowNonLoopback { get; set; }

    public GatewayDownstreamOptions Downstream { get; set; } = new();

    public GatewayForwardedHeadersOptions ForwardedHeaders { get; set; } = new();
}

public sealed class GatewayDownstreamOptions
{
    public string IdentityBaseUrl { get; set; } = string.Empty;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
}

public sealed class GatewayForwardedHeadersOptions
{
    public string[] KnownProxies { get; set; } = [];

    public string[] KnownNetworks { get; set; } = [];
}

internal sealed class GatewayOptionsValidator(GatewayAudience audience) : IValidateOptions<GatewayOptions>
{
    public ValidateOptionsResult Validate(string? name, GatewayOptions options)
    {
        var failures = new List<string>();

        if (options.Urls is { Length: 0 })
        {
            failures.Add($"'{GatewayOptions.SectionName}:Urls' deve conter ao menos uma URL de escuta.");
        }
        else
        {
            foreach (var rawUrl in options.Urls)
            {
                if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    failures.Add($"'{GatewayOptions.SectionName}:Urls' contém uma URL inválida: '{rawUrl}'.");
                    continue;
                }

                if (audience == GatewayAudience.Admin && !options.AllowNonLoopback && !IsLoopbackHost(uri.Host))
                {
                    failures.Add($"O gateway administrativo exige loopback em '{GatewayOptions.SectionName}:Urls' ('{rawUrl}') quando AllowNonLoopback é false.");
                }
            }
        }

        if (!TryValidateBaseUrl(options.Downstream.IdentityBaseUrl, out _))
        {
            failures.Add($"'{GatewayOptions.SectionName}:Downstream:IdentityBaseUrl' deve ser uma URL HTTP/HTTPS absoluta sem credenciais, caminho ou query.");
        }

        if (options.Downstream.Timeout <= TimeSpan.Zero || options.Downstream.Timeout > TimeSpan.FromSeconds(30))
        {
            failures.Add($"'{GatewayOptions.SectionName}:Downstream:Timeout' deve ser maior que zero e de no máximo 30 segundos.");
        }

        failures.AddRange(options.ForwardedHeaders.KnownProxies
            .Where(proxy => !IPAddress.TryParse(proxy, out _))
            .Select(proxy => $"'{GatewayOptions.SectionName}:ForwardedHeaders:KnownProxies' contém um IP inválido: '{proxy}'."));

        failures.AddRange(options.ForwardedHeaders.KnownNetworks
            .Where(network => !IPNetwork.TryParse(network, out _))
            .Select(network => $"'{GatewayOptions.SectionName}:ForwardedHeaders:KnownNetworks' contém uma rede inválida: '{network}'."));

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    internal static bool TryValidateBaseUrl(string? raw, out Uri? baseUri)
    {
        baseUri = null;
        if (string.IsNullOrWhiteSpace(raw)
            || !Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.AbsolutePath != "/" && uri.AbsolutePath.Length > 0))
        {
            return false;
        }

        baseUri = uri;
        return true;
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
}
