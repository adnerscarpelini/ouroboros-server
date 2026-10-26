using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ouroboros.Observability;

// Sanitização aplicada a todo diagnóstico antes de qualquer destino (console e OTLP). Remove segredos e dados
// pessoais reconhecíveis e impõe limites de tamanho; não serializa objetos, corpos nem Exception.Data.
internal static partial class DiagnosticRedaction
{
    internal const string Redacted = "[REDACTED]";
    internal const int MaximumMessageLength = 4096;
    internal const int MaximumValueLength = 1024;
    internal const int MaximumStackTraceLength = 8192;
    internal const int MaximumAttributes = 32;
    internal const int MaximumExceptionDepth = 4;

    private static readonly string[] s_sensitiveKeys =
    [
        "password", "passwd", "pwd", "senha", "secret", "segredo", "token", "authorization", "cookie", "credential", "credencial",
        "connectionstring",
        "apikey", "api_key", "privatekey", "private_key", "recipient", "email", "ciphertext", "payload",
    ];

    internal static bool IsSensitiveKey(string key) =>
        s_sensitiveKeys.Any(word => key.Contains(word, StringComparison.OrdinalIgnoreCase));

    internal static string Sanitize(string? text, int maximumLength)
    {
        if (string.IsNullOrEmpty(text)) { return text ?? ""; }
        // Limita antes das expressões para que uma entrada enorme não custe tempo ilimitado.
        var value = text.Length > maximumLength * 2 ? text[..(maximumLength * 2)] : text;
        value = PemBlock().Replace(value, Redacted);
        value = UrlCredentials().Replace(value, "$1" + Redacted + "@");
        value = UrlQuery().Replace(value, "$1?" + Redacted);
        value = Jwt().Replace(value, Redacted);
        value = AuthorizationScheme().Replace(value, "$1 " + Redacted);
        value = SecretAssignment().Replace(value, "$1$2" + Redacted);
        value = EmailAddress().Replace(value, "[EMAIL]");
        value = OpaqueValue().Replace(value, match => LooksOpaque(match.Value) ? Redacted : match.Value);
        return Truncate(value, maximumLength);
    }

    internal static object? SanitizeValue(string key, object? value)
    {
        if (value is null) { return null; }
        if (IsSensitiveKey(key)) { return Redacted; }
        return value switch
        {
            string text => Sanitize(text, MaximumValueLength),
            bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
            Guid or DateTime or DateTimeOffset or TimeSpan or Enum => value,
            IFormattable formattable => Sanitize(formattable.ToString(null, CultureInfo.InvariantCulture), MaximumValueLength),
            _ => $"[{value.GetType().Name}]",
        };
    }

    // Tipo, mensagem e pilha sanitizados da cadeia de exceções, sem Data, propriedades ou ToString de objetos.
    internal static (string Type, string Message, string StackTrace) Describe(Exception exception)
    {
        var builder = new StringBuilder();
        var pending = new Queue<(Exception Error, int Depth)>();
        pending.Enqueue((exception, 0));
        while (pending.Count > 0 && builder.Length < MaximumStackTraceLength)
        {
            var (current, depth) = pending.Dequeue();
            if (depth > 0) { builder.Append("\n ---> "); }
            builder.Append(current.GetType().FullName).Append(": ").Append(Sanitize(current.Message, MaximumValueLength));
            if (current.StackTrace is { } stack) { builder.Append('\n').Append(Sanitize(stack, MaximumStackTraceLength)); }
            if (depth + 1 >= MaximumExceptionDepth) { continue; }
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.Take(MaximumExceptionDepth)) { pending.Enqueue((inner, depth + 1)); }
            }
            else if (current.InnerException is not null) { pending.Enqueue((current.InnerException, depth + 1)); }
        }
        return (exception.GetType().FullName ?? exception.GetType().Name, Sanitize(exception.Message, MaximumValueLength),
            Truncate(builder.ToString(), MaximumStackTraceLength));
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength] + "…[truncado]";

    // Valor longo com letras e dígitos misturados (tokens opacos, Base64, ciphertext). Hexadecimal (trace, GUID,
    // impressões) e identificadores de código sem dígitos são preservados.
    private static bool LooksOpaque(string value)
    {
        var digits = value.Count(char.IsAsciiDigit);
        var letters = value.Count(char.IsAsciiLetter);
        var hexOnly = value.TrimEnd('=').All(character => char.IsAsciiHexDigit(character) || character == '-');
        return !hexOnly && digits >= 2 && letters >= 2;
    }

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]+-----[\s\S]*?(?:-----END [A-Z0-9 ]+-----|$)", RegexOptions.NonBacktracking)]
    private static partial Regex PemBlock();

    [GeneratedRegex(@"(?i)\b([a-z][a-z0-9+.\-]*://)[^\s/@:]+:[^\s/@]+@", RegexOptions.NonBacktracking)]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"(?i)\b([a-z][a-z0-9+.\-]*://[^\s?#""'<>]+)\?[^\s#""'<>]*", RegexOptions.NonBacktracking)]
    private static partial Regex UrlQuery();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]*", RegexOptions.NonBacktracking)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.NonBacktracking)]
    private static partial Regex AuthorizationScheme();

    // Atribuição chave=valor ou chave: valor, inclusive em JSON ("password": "x"), com a chave em inglês ou português.
    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|senha|secret|segredo|client_secret|token|access_token|refresh_token|id_token|api[_\-]?key|authorization|cookie|set-cookie|private[_\-]?key|credentials?|credenciais|credencial)\b(""?'?\s*[=:]\s*)(""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|\[[^\]]*\]|[^\s;,&""'}\]]+)", RegexOptions.NonBacktracking)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.NonBacktracking)]
    private static partial Regex EmailAddress();

    [GeneratedRegex(@"[A-Za-z0-9+/_\-]{32,}={0,2}", RegexOptions.NonBacktracking)]
    private static partial Regex OpaqueValue();
}
