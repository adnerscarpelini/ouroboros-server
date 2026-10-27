using System.Security.Cryptography;
using System.Text;
using Ouroboros.Identity.Contracts.Parameters;

namespace Ouroboros.Identity.Application.Parameters;

internal interface IParameterHistoryReader
{
    Task<IReadOnlyList<ParameterHistoryView>> ReadAsync(string key, ParameterCursor.Cursor? after, int count, CancellationToken cancellationToken);
}

internal sealed class GetParameterHistory(IParameterHistoryReader reader) : IGetParameterHistory
{
    public async Task<CursorPage<ParameterHistoryView>> ExecuteAsync(ParameterHistoryRequest request, CancellationToken cancellationToken)
    {
        // O vínculo não cresce com a chave nem com escapes Unicode na serialização do cursor.
        var scope = "identity:parameter-history:v1:changed-at-desc:id-desc:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Key)));
        var after = ParameterCursor.DecodePosition(request.Page, scope);
        if (after is not null && after.ChangedAt is null) { throw new InvalidPageException(); }
        var rows = await reader.ReadAsync(request.Key, after, request.Page.Limit + 1, cancellationToken);
        var more = rows.Count > request.Page.Limit;
        var data = rows.Take(request.Page.Limit).ToArray();
        return new(data, more, more ? ParameterCursor.Encode(scope, data[^1].Id, data[^1].ChangedAt) : null);
    }
}
