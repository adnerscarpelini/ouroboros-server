using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Ouroboros.Observability;

// Uma linha JSON por registro já sanitizado, em UTC, com serviço, versão, TraceId, SpanId e a identidade da
// ocorrência. TraceId e SpanId saem como campos próprios, nunca como o identificador W3C completo.
internal sealed class JsonConsoleLogExporter(ServiceIdentity service, TextWriter? output = null) : BaseExporter<LogRecord>
{
    private static readonly JsonWriterOptions s_options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly TextWriter _output = output ?? Console.Out;

    public override ExportResult Export(in Batch<LogRecord> batch)
    {
        var text = new StringBuilder();
        foreach (var record in batch) { text.Append(Format(record)).Append('\n'); }
        try
        {
            _output.Write(text.ToString());
            _output.Flush();
            return ExportResult.Success;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return ExportResult.Failure;
        }
    }

    internal string Format(LogRecord record)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var json = new Utf8JsonWriter(buffer, s_options))
        {
            json.WriteStartObject();
            json.WriteString("timestamp", new DateTimeOffset(DateTime.SpecifyKind(record.Timestamp, DateTimeKind.Utc)));
            json.WriteString("level", record.LogLevel.ToString());
            json.WriteString("category", record.CategoryName);
            json.WriteNumber("eventId", record.EventId.Id);
            json.WriteString("service", service.Name);
            json.WriteString("serviceVersion", service.Version);
            if (record.TraceId != default)
            {
                json.WriteString("traceId", record.TraceId.ToHexString());
                json.WriteString("spanId", record.SpanId.ToHexString());
            }
            string? occurrence = null;
            var attributes = new List<KeyValuePair<string, object?>>();
            foreach (var pair in record.Attributes ?? [])
            {
                if (pair.Key == SanitizingLogProcessor.OccurrenceIdAttribute) { occurrence = pair.Value as string; }
                else { attributes.Add(pair); }
            }
            if (occurrence is not null) { json.WriteString("occurrenceId", occurrence); }
            json.WriteString("message", record.FormattedMessage ?? record.Body);
            if (attributes.Count > 0)
            {
                json.WriteStartObject("attributes");
                foreach (var (key, value) in attributes) { JsonValues.Write(json, key, value); }
                json.WriteEndObject();
            }
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
