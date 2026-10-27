using Ouroboros.Identity.Contracts.Parameters;

namespace Ouroboros.Identity.Application.Parameters;

internal interface IParameterListReader
{
    Task<IReadOnlyList<ParameterView>> ReadAsync(Guid? after, int count, CancellationToken cancellationToken);
}

internal sealed class ListParameters(IParameterListReader reader) : IListParameters
{
    private const string Scope = "identity:parameters:v1:external-id:asc";

    public async Task<CursorPage<ParameterView>> ExecuteAsync(PageRequest request, CancellationToken cancellationToken)
    {
        var after = ParameterCursor.Decode(request, Scope);
        var rows = await reader.ReadAsync(after, request.Limit + 1, cancellationToken);
        var more = rows.Count > request.Limit;
        var data = rows.Take(request.Limit).ToArray();
        return new(data, more, more ? ParameterCursor.Encode(Scope, data[^1].Id) : null);
    }
}
