namespace Ouroboros.Identity.Contracts.Parameters;

public sealed record PageRequest(int Limit = 50, string? After = null);
public sealed record CursorPage<T>(IReadOnlyList<T> Data, bool HasMore, string? NextCursor);
public sealed record ParameterView(Guid Id, string Key, string Value, string Description, string DataType,
    string? ValidationPattern, bool IsSensitive, int Revision, Guid? UpdatedBy, DateTimeOffset UpdatedAt);

public interface IListParameters
{
    Task<CursorPage<ParameterView>> ExecuteAsync(PageRequest request, CancellationToken cancellationToken);
}

public sealed class InvalidPageException() : Exception("Limite ou cursor inválido para esta consulta.");
