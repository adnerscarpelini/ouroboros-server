using Microsoft.Extensions.Options;

namespace Ouroboros.WebClient.Infrastructure;

// Validado na subida: o processo não inicia apontando para algo que não seja a origem de um gateway, nem com
// connection strings configuradas, porque o web client não acessa bancos.
internal sealed class WebClientOptionsValidator(IConfiguration configuration) : IValidateOptions<WebClientOptions>
{
    public ValidateOptionsResult Validate(string? name, WebClientOptions options)
    {
        var failures = new List<string>();

        if (!TryValidateBaseUrl(options.Gateway.BaseUrl, out _))
        {
            failures.Add("WebClient:Gateway:BaseUrl deve ser a origem HTTP ou HTTPS absoluta do gateway, sem credenciais, caminho, query ou fragmento.");
        }

        if (options.Gateway.Timeout <= TimeSpan.Zero || options.Gateway.Timeout > GatewayClientOptions.MaximumTimeout)
        {
            failures.Add("WebClient:Gateway:Timeout deve ser maior que zero e de no máximo 30 segundos.");
        }

        if (configuration.GetSection("ConnectionStrings").GetChildren().Any(entry => !string.IsNullOrWhiteSpace(entry.Value)))
        {
            failures.Add("O web client não acessa bancos de dados: remova ConnectionStrings da configuração.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    public static bool TryValidateBaseUrl(string? value, out Uri? baseUri)
    {
        baseUri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        baseUri = uri;
        return true;
    }
}
