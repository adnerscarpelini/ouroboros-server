namespace Ouroboros.Modules.Identity.Application.Parameters;

internal interface IParameterReader
{
    Task<string?> GetValueAsync(string key, CancellationToken cancellationToken);
}
