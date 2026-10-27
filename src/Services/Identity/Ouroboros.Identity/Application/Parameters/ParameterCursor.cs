using System.Text.Json;
using Ouroboros.Identity.Contracts.Parameters;

namespace Ouroboros.Identity.Application.Parameters;

// Cursor não é autorização. O escopo vincula a posição à consulta e à sua ordenação.
internal static class ParameterCursor
{
    public static string Encode(string scope, Guid position, DateTimeOffset? changedAt = null) => Convert.ToBase64String(
        JsonSerializer.SerializeToUtf8Bytes(new Cursor(1, scope, position, changedAt))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static Guid? Decode(PageRequest request, string scope) => DecodePosition(request, scope)?.Position;

    public static Cursor? DecodePosition(PageRequest request, string scope)
    {
        if (request.Limit is < 1 or > 100) { throw new InvalidPageException(); }
        if (request.After is null) { return null; }
        if (request.After.Length is 0 or > 1024 || request.After.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        { throw new InvalidPageException(); }
        try
        {
            var text = request.After.Replace('-', '+').Replace('_', '/');
            text = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
            var cursor = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(text));
            return cursor is { Version: 1 } && cursor.Scope == scope && cursor.Position != Guid.Empty
                && (cursor.ChangedAt is null || cursor.ChangedAt.Value.Offset == TimeSpan.Zero)
                ? cursor : throw new InvalidPageException();
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        { throw new InvalidPageException(); }
    }

    internal sealed record Cursor(int Version, string Scope, Guid Position, DateTimeOffset? ChangedAt = null);
}
