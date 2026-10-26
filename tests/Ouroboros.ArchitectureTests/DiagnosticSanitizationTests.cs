using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Ouroboros.Observability;

namespace Ouroboros.ArchitectureTests;

public sealed partial class DiagnosticSanitizationTests
{
    private const string Jwt = "eyJhbGciOiJSUzI1NiIsImtpZCI6InRlc3QifQ.eyJzdWIiOiIxMjM0NTY3ODkwIn0.c2lnbmF0dXJlLXRlc3QtdmFsdWU";
    private const string OpaqueToken = "Q2hhdmVPcGFjYTEyMzQ1Njc4OTBhYmNkZWZnaGlqa2xtbm9wcXI";
    private const string Email = "cliente.secreto@example.com";
    private const string Password = "S3nh@Forte!Diagnostico";

    [Theory]
    [InlineData("Authorization: Bearer " + Jwt, Jwt)]
    [InlineData("token " + Jwt + " recusado", Jwt)]
    [InlineData("Host=db;Username=identity_runtime;Password=" + Password + ";Database=x", Password)]
    [InlineData("password=\"com espaço\" e mais", "com espaço")]
    [InlineData("link http://localhost/confirm?token=private-test-token&x=1", "private-test-token")]
    [InlineData("amqp://notifications_consumer:segredo-do-broker@localhost:5672/", "segredo-do-broker")]
    [InlineData("destinatário " + Email, Email)]
    [InlineData("refresh " + OpaqueToken, OpaqueToken)]
    [InlineData("-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASC\n-----END PRIVATE KEY-----", "MIIEvQIBADANBgkqhkiG9w0BAQEFAASC")]
    [InlineData("api_key: abc123def456", "abc123def456")]
    [InlineData("client_secret='valor secreto'", "valor secreto")]
    [InlineData("{\"password\":\"hunter2json\",\"token\":\"tokjsonvalue\"}", "hunter2json")]
    [InlineData("{\"password\":\"hunter2json\",\"token\":\"tokjsonvalue\"}", "tokjsonvalue")]
    [InlineData("{ \"Password\" : \"spacedsecret\" }", "spacedsecret")]
    [InlineData("{\"client_secret\": \"exjsonsecret\"}", "exjsonsecret")]
    [InlineData("senha: senhatexto", "senhatexto")]
    [InlineData("senha=interpsecret", "interpsecret")]
    [InlineData("{\"senha\":\"jsonsenha\"}", "jsonsenha")]
    [InlineData("{\"password\":\"ab\\\"cdleak\"}", "cdleak")]
    [InlineData("{\"secret\":[\"arrsecret\"]}", "arrsecret")]
    public void Sensitive_text_is_redacted(string text, string secret) =>
        Assert.DoesNotContain(secret, DiagnosticRedaction.Sanitize(text, DiagnosticRedaction.MaximumMessageLength), StringComparison.Ordinal);

    [Theory]
    [InlineData("Publicação de 0196b2a0-0000-7000-8000-000000000001 não confirmada: publish-unconfirmed; intenção recuperável.")]
    [InlineData("trace 4bf92f3577b34da6a3ce929d0e0e4736 span 00f067aa0ba902b7")]
    [InlineData("at Ouroboros.Notifications.Infrastructure.InboxExecutor.RunOnceAsync(CancellationToken cancellationToken)")]
    [InlineData("Notifications processou 0196b2a00000700080000000000000001: Accepted.")]
    [InlineData("Microsoft.EntityFrameworkCore.DbUpdateException: An error occurred while saving the entity changes.")]
    public void Diagnostic_identifiers_and_code_locations_are_preserved(string text) =>
        Assert.Equal(text, DiagnosticRedaction.Sanitize(text, DiagnosticRedaction.MaximumMessageLength));

    [Fact]
    public void Sensitive_event_reaches_no_destination_with_secrets_payload_or_exception_data()
    {
        using var diagnostics = new DiagnosticsHost();
        var inner = new InvalidOperationException($"Falha ao enviar para {Email} com Bearer {Jwt}");
        var exception = new InvalidOperationException($"Npgsql: Host=db;Password={Password}", inner);
        exception.Data["Payload"] = "conteúdo bruto do e-mail";

        LogSensitive(diagnostics.Logger(), exception, Password, Jwt, Email, new SecretCarrier(), $"http://localhost/confirm?token={OpaqueToken}");

        var record = Assert.Single(diagnostics.Records);
        var console = diagnostics.Console.ToString();
        foreach (var secret in new[] { Password, Jwt, Email, OpaqueToken, SecretCarrier.Secret, "conteúdo bruto do e-mail" })
        {
            Assert.DoesNotContain(secret, console, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, record.Message ?? "", StringComparison.Ordinal);
            Assert.All(record.Attributes, pair => Assert.DoesNotContain(secret, pair.Value?.ToString() ?? "", StringComparison.Ordinal));
        }
        Assert.Null(record.Exception);
        Assert.Equal(typeof(InvalidOperationException).FullName, record.Attribute("exception.type"));
        Assert.Contains("InvalidOperationException", (string)record.Attribute("exception.stacktrace")!, StringComparison.Ordinal);
        Assert.Equal(DiagnosticRedaction.Redacted, record.Attribute("Password"));
        Assert.Equal(DiagnosticRedaction.Redacted, record.Attribute("AccessToken"));
        Assert.Equal(DiagnosticRedaction.Redacted, record.Attribute("Recipient"));
        Assert.Equal($"[{nameof(SecretCarrier)}]", record.Attribute("Carrier"));
        Assert.Equal("http://localhost/confirm?" + DiagnosticRedaction.Redacted, record.Attribute("Link"));
    }

    [Theory]
    [InlineData("password=segredo123 e token: abc")]
    [InlineData("{\"password\":\"x\",\"secret\":[\"y\"]}")]
    public void Sanitizing_twice_is_stable(string text)
    {
        var once = DiagnosticRedaction.Sanitize(text, DiagnosticRedaction.MaximumMessageLength);
        Assert.Equal(once, DiagnosticRedaction.Sanitize(once, DiagnosticRedaction.MaximumMessageLength));
        Assert.DoesNotContain(DiagnosticRedaction.Redacted + "]", once, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_details_portuguese_keys_and_exception_json_reach_no_destination()
    {
        using var diagnostics = new DiagnosticsHost();
        var exception = new InvalidOperationException("{\"client_secret\": \"exjsonsecret\"}");

        LogDetails(diagnostics.Logger(), exception, "{\"password\":\"hunter2json\",\"token\":\"tokjsonvalue\"}", "senhaporchave", "pwdporchave");

        var record = Assert.Single(diagnostics.Records);
        var exported = string.Join('\n', record.Attributes.Select(pair => pair.Value?.ToString())) + record.Message;
        foreach (var secret in new[] { "hunter2json", "tokjsonvalue", "senhaporchave", "pwdporchave", "exjsonsecret" })
        {
            Assert.DoesNotContain(secret, diagnostics.Console.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, exported, StringComparison.Ordinal);
        }
        Assert.Equal(DiagnosticRedaction.Redacted, record.Attribute("Senha"));
        Assert.Equal(DiagnosticRedaction.Redacted, record.Attribute("Pwd"));
    }

    [Fact]
    public void Object_whose_to_string_fails_or_leaks_is_never_formatted_and_does_not_reach_the_caller()
    {
        using var diagnostics = new DiagnosticsHost();

        var error = Record.Exception(() => LogObject(diagnostics.Logger(), new ThrowingCarrier()));

        Assert.Null(error);
        var record = Assert.Single(diagnostics.Records);
        Assert.Equal($"Objeto [{nameof(ThrowingCarrier)}] recebido.", record.Message);
    }

    [Fact]
    public void State_without_template_keeps_sanitized_attributes_and_only_pattern_sanitized_text()
    {
        using var diagnostics = new DiagnosticsHost();
        var state = new List<KeyValuePair<string, object?>> { new("Code", "provider-transient"), new("Token", Jwt) };

        diagnostics.Logger().Log(LogLevel.Warning, default, state, null, (_, _) => $"texto livre com Bearer {Jwt} e senha=livre123");

        var record = Assert.Single(diagnostics.Records);
        Assert.Equal("provider-transient", record.Attribute("Code"));
        Assert.Equal(DiagnosticRedaction.Redacted, record.Attribute("Token"));
        Assert.DoesNotContain(Jwt, diagnostics.Console.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("livre123", diagnostics.Console.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Occurrence_identity_is_assigned_once_before_every_destination_and_is_not_the_trace()
    {
        using var diagnostics = new DiagnosticsHost();
        using var activity = new Activity("probe").SetIdFormat(ActivityIdFormat.W3C).Start();

        diagnostics.Logger().LogWarning("primeira");
        diagnostics.Logger().LogWarning("segunda");

        var console = diagnostics.ConsoleLines().Select(line => line.GetProperty("occurrenceId").GetString()).ToArray();
        var exported = diagnostics.Records.Select(record => (string?)record.Attribute(SanitizingLogProcessor.OccurrenceIdAttribute)).ToArray();
        Assert.Equal(console, exported);
        Assert.Equal(2, exported.Distinct().Count());
        Assert.All(exported, id => Assert.Equal(7, Guid.Parse(id!).Version));
        Assert.All(diagnostics.Records, record => Assert.Equal(activity.TraceId.ToHexString(), record.TraceId));
        Assert.All(exported, id => Assert.NotEqual(activity.TraceId.ToHexString(), Guid.Parse(id!).ToString("N")));
    }

    [Fact]
    public void Occurrence_identity_supplied_by_the_caller_is_replaced()
    {
        using var diagnostics = new DiagnosticsHost();
        diagnostics.Logger().LogWarning("forjado {log.record.uid}", "00000000-0000-0000-0000-000000000000");
        var record = Assert.Single(diagnostics.Records);
        Assert.Single(record.Attributes, pair => pair.Key == SanitizingLogProcessor.OccurrenceIdAttribute);
        Assert.NotEqual("00000000-0000-0000-0000-000000000000", record.Attribute(SanitizingLogProcessor.OccurrenceIdAttribute));
    }

    [Fact]
    public void Limits_are_applied_to_messages_values_attributes_and_exception_chains()
    {
        var huge = new string('a', 100_000);
        using var diagnostics = new DiagnosticsHost();
        Exception exception = new InvalidOperationException("raiz");
        for (var depth = 0; depth < 20; depth++) { exception = new InvalidOperationException("nível " + depth + " " + huge, exception); }
        var state = Enumerable.Range(0, 100).Select(index => new KeyValuePair<string, object?>("Field" + index, huge)).ToList();

        diagnostics.Logger().Log(LogLevel.Error, default, state, exception, (_, _) => huge);

        var record = Assert.Single(diagnostics.Records);
        Assert.True(record.Attributes.Length <= DiagnosticRedaction.MaximumAttributes + 4);
        Assert.All(record.Attributes.Where(pair => pair.Key != "exception.stacktrace"),
            pair => Assert.True(pair.Value is not string text || text.Length <= DiagnosticRedaction.MaximumValueLength + 16));
        Assert.True(((string)record.Attribute("exception.stacktrace")!).Length <= DiagnosticRedaction.MaximumStackTraceLength + 16);
        Assert.True(record.Message!.Length <= DiagnosticRedaction.MaximumMessageLength + 16);
        Assert.DoesNotContain("raiz", (string)record.Attribute("exception.stacktrace")!, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostic_pipeline_does_not_depend_on_databases_or_business_services()
    {
        var references = typeof(ObservabilityHostExtensions).Assembly.GetReferencedAssemblies().Select(assembly => assembly.Name!).ToArray();
        Assert.DoesNotContain(references, name => name.Contains("Npgsql", StringComparison.Ordinal)
            || name.Contains("EntityFrameworkCore", StringComparison.Ordinal) || name.StartsWith("System.Data", StringComparison.Ordinal)
            || name.StartsWith("Ouroboros.", StringComparison.Ordinal));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha com senha {Password}, token {AccessToken} para {Recipient}: {Carrier} {Link}")]
    private static partial void LogSensitive(ILogger logger, Exception exception, string password, string accessToken, string recipient, SecretCarrier carrier, string link);

    [LoggerMessage(Level = LogLevel.Error, Message = "Detalhe {Detail}; {Senha}; {Pwd}")]
    private static partial void LogDetails(ILogger logger, Exception exception, string detail, string senha, string pwd);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Objeto {Carrier} recebido.")]
    private static partial void LogObject(ILogger logger, ThrowingCarrier carrier);

    private sealed class ThrowingCarrier
    {
        public override string ToString() => throw new InvalidOperationException("ToString não deve ser chamado.");
    }

    private sealed class SecretCarrier
    {
        internal const string Secret = "objeto-com-segredo-no-tostring";
        public override string ToString() => Secret;
    }
}
