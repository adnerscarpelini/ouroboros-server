namespace Ouroboros.WebClient.Infrastructure;

// Seção WebClient do appsettings. O gateway é o único destino HTTP do web client.
internal sealed class WebClientOptions
{
    public const string SectionName = "WebClient";

    public GatewayClientOptions Gateway { get; set; } = new();

    public DataProtectionOptions DataProtection { get; set; } = new();
}

internal sealed class GatewayClientOptions
{
    public static readonly TimeSpan MaximumTimeout = TimeSpan.FromSeconds(30);

    // Origem do gateway (esquema, host e porta), sem credenciais, caminho, query ou fragmento.
    public string? BaseUrl { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
}

internal sealed class DataProtectionOptions
{
    // Diretório persistente das chaves que protegem os cookies. Sem valor, vale o local padrão do ASP.NET Core.
    public string? KeysPath { get; set; }
}
