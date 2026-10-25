namespace Ouroboros.Identity.Application.Parameters;

internal sealed class ParameterConflictException(string key, Exception innerException)
    : Exception($"Conflito ao gravar o parâmetro '{key}'.", innerException)
{
    public string Key { get; } = key;
}
