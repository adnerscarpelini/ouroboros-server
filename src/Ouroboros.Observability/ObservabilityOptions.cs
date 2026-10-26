using Microsoft.Extensions.Options;

namespace Ouroboros.Observability;

// Destino OTLP opcional dos diagnósticos (o coletor local do Compose). Sem endpoint, só o console recebe.
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";
    public OtlpSettings Otlp { get; set; } = new();
}

public sealed class OtlpSettings
{
    public string? Endpoint { get; set; }
}

internal sealed class ObservabilityOptionsValidator : IValidateOptions<ObservabilityOptions>
{
    public ValidateOptionsResult Validate(string? name, ObservabilityOptions options)
    {
        var endpoint = options.Otlp.Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint)) { return ValidateOptionsResult.Success; }
        // Credenciais, query e fragmento não pertencem ao endpoint: nada sensível na configuração do destino.
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath == "/"
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Observability:Otlp:Endpoint deve ser URL http(s) absoluta, sem credenciais, caminho ou query.");
    }
}
