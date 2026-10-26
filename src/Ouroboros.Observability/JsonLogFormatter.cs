using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Ouroboros.Observability;

// Uma linha JSON por entrada, em UTC, com serviço, versão e o contexto W3C ativo no momento do log. TraceId e
// SpanId saem como campos próprios, nunca como o identificador W3C completo (00-<trace>-<span>-<flags>).
internal sealed class JsonLogFormatter(ServiceIdentity service) : ConsoleFormatter(FormatterName)
{
    internal const string FormatterName = "ouroboros-json";
    private const string OriginalFormat = "{OriginalFormat}";
    private static readonly JsonWriterOptions s_options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (message is null && logEntry.Exception is null) { return; }
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var json = new Utf8JsonWriter(buffer, s_options))
        {
            json.WriteStartObject();
            json.WriteString("timestamp", DateTimeOffset.UtcNow);
            json.WriteString("level", logEntry.LogLevel.ToString());
            json.WriteString("category", logEntry.Category);
            json.WriteNumber("eventId", logEntry.EventId.Id);
            json.WriteString("service", service.Name);
            json.WriteString("serviceVersion", service.Version);
            if (Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity)
            {
                json.WriteString("traceId", activity.TraceId.ToHexString());
                json.WriteString("spanId", activity.SpanId.ToHexString());
            }
            json.WriteString("message", message);
            if (logEntry.State is IReadOnlyList<KeyValuePair<string, object?>> values && values.Any(pair => pair.Key != OriginalFormat))
            {
                json.WriteStartObject("attributes");
                foreach (var (key, value) in values.Where(pair => pair.Key != OriginalFormat))
                {
                    WriteValue(json, key, value);
                }
                json.WriteEndObject();
            }
            if (logEntry.Exception is not null) { json.WriteString("exception", logEntry.Exception.ToString()); }
            json.WriteEndObject();
        }
        textWriter.Write(Encoding.UTF8.GetString(buffer.WrittenSpan));
        textWriter.Write(Environment.NewLine);
    }

    private static void WriteValue(Utf8JsonWriter json, string key, object? value)
    {
        switch (value)
        {
            case null: json.WriteNull(key); break;
            case bool flag: json.WriteBoolean(key, flag); break;
            case int or long or short or byte or uint or ushort or sbyte: json.WriteNumber(key, Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            case ulong number: json.WriteNumber(key, number); break;
            case decimal number: json.WriteNumber(key, number); break;
            case double or float when double.IsFinite(Convert.ToDouble(value, CultureInfo.InvariantCulture)):
                json.WriteNumber(key, Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            case IFormattable formattable: json.WriteString(key, formattable.ToString(null, CultureInfo.InvariantCulture)); break;
            default: json.WriteString(key, value.ToString()); break;
        }
    }
}
