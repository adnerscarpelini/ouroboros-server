namespace Ouroboros.WebClient.Infrastructure;

// Nome do HttpClient que aponta para o gateway. Os clientes de cada Feature partem dele; nenhum outro destino HTTP
// é configurado no web client.
internal static class GatewayClient
{
    public const string Name = "gateway";
}
