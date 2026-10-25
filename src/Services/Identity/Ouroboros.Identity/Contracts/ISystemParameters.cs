namespace Ouroboros.Identity.Contracts;

// Leitura local dos parâmetros de Identity. Toda leitura consulta a tabela: não há
// valor em código, em arquivos de configuração nem em cache, e uma alteração vale na próxima leitura.
public interface ISystemParameters
{
    // Lança ParameterNotFoundException quando a chave não existe.
    Task<string> GetAsync(string key, CancellationToken cancellationToken);
}

public sealed class ParameterNotFoundException(string key)
    : Exception($"O parâmetro do sistema '{key}' não existe.")
{
    public string Key { get; } = key;
}
