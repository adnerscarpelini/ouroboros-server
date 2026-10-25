namespace Ouroboros.Modules.Settings.Application.Parameters;

internal interface IParameterReader
{
    Task<string?> GetValueAsync(string key, CancellationToken cancellationToken);
}
