namespace Ouroboros.Identity.Contracts.Parameters;

public sealed record ParameterHistoryRequest(string Key, PageRequest Page);
public sealed record ParameterHistoryView(Guid Id, string Key, int Revision, string? PreviousValue, string NewValue,
    string? PreviousDescription, string NewDescription, string DataType, string? ValidationPattern,
    bool IsSensitive, Guid? ChangedBy, DateTimeOffset ChangedAt);

public interface IGetParameterHistory
{
    Task<CursorPage<ParameterHistoryView>> ExecuteAsync(ParameterHistoryRequest request, CancellationToken cancellationToken);
}
