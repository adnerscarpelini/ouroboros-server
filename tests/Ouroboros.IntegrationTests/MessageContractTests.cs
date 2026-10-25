using System.Text.Json;
using System.Text.Json.Nodes;
using Ouroboros.Messaging;
using Ouroboros.Messaging.Contracts;

namespace Ouroboros.IntegrationTests;

public sealed class MessageContractTests
{
    internal static MessageEnvelope Example() => new(
        Guid.NewGuid(), "notification.email.requested", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15),
        "identity", Guid.NewGuid(), Guid.NewGuid(), "opaque-account-registration", null, null,
        new ProtectedMessagePayload(EnvelopeCodec.ProtectionAlgorithm, "notifications-key-1",
            Convert.ToBase64String(new byte[384]), Convert.ToBase64String(new byte[12]),
            Convert.ToBase64String(new byte[16]), Convert.ToBase64String(new byte[64])));

    [Fact]
    public void Producer_and_independent_consumer_projection_agree_on_v1_wire_fields()
    {
        var original = Example();
        var bytes = EnvelopeCodec.Encode(original);
        Assert.True(EnvelopeCodec.TryDecode(bytes, "notification.email.requested", 1, "identity", out var decoded, out _));
        Assert.Equal(original, decoded);
        // Projeção independente do futuro adaptador: BCL JSON, sem assembly de outro serviço.
        using var consumer = JsonDocument.Parse(bytes);
        Assert.Equal(original.MessageId, consumer.RootElement.GetProperty("messageId").GetGuid());
        Assert.Equal(1, consumer.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("identity", consumer.RootElement.GetProperty("producer").GetString());
        Assert.Equal(EnvelopeCodec.ProtectionAlgorithm, consumer.RootElement.GetProperty("payload").GetProperty("algorithm").GetString());
        Assert.DoesNotContain("recipient", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Optional_additions_are_compatible_but_unknown_versions_are_controlled_failures()
    {
        var json = JsonNode.Parse(EnvelopeCodec.Encode(Example()))!;
        json["futureOptionalField"] = "ignored";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(json);
        Assert.True(EnvelopeCodec.TryDecode(bytes, "notification.email.requested", 1, "identity", out _, out _));
        json["version"] = 2;
        Assert.False(EnvelopeCodec.TryDecode(JsonSerializer.SerializeToUtf8Bytes(json), "notification.email.requested", 1, "identity", out _, out var code));
        Assert.Equal("unsupported-version", code);
    }

    [Fact]
    public void Requested_contract_rejects_a_producer_different_from_its_schema()
    {
        var bytes = EnvelopeCodec.Encode(Example() with { Producer = "unexpected-producer" });
        Assert.False(EnvelopeCodec.TryDecode(bytes, "notification.email.requested", 1, "identity", out _, out _));
    }

    [Fact]
    public void Required_wire_names_are_case_sensitive_as_in_the_schema()
    {
        var json = JsonNode.Parse(EnvelopeCodec.Encode(Example()))!.AsObject();
        json["Version"] = json["version"]!.DeepClone();
        json.Remove("version");
        Assert.False(EnvelopeCodec.TryDecode(JsonSerializer.SerializeToUtf8Bytes(json), "notification.email.requested", 1, "identity", out _, out _));
    }

    [Theory]
    [InlineData("messageId")]
    [InlineData("type")]
    [InlineData("version")]
    [InlineData("occurredAt")]
    [InlineData("expiresAt")]
    [InlineData("producer")]
    [InlineData("correlationId")]
    [InlineData("causationId")]
    [InlineData("supersessionKey")]
    [InlineData("traceparent")]
    [InlineData("tracestate")]
    [InlineData("payload")]
    public void Missing_required_fields_are_rejected(string field)
    {
        var json = JsonNode.Parse(EnvelopeCodec.Encode(Example()))!.AsObject();
        json.Remove(field);
        Assert.False(EnvelopeCodec.TryDecode(JsonSerializer.SerializeToUtf8Bytes(json), "notification.email.requested", 1, "identity", out _, out _));
    }

    [Fact]
    public void Oversize_plaintext_protection_invalid_expiry_and_duplicate_fields_are_rejected()
    {
        var message = Example();
        Assert.Throws<ArgumentException>(() => EnvelopeCodec.Encode(message with { ExpiresAt = message.OccurredAt }));
        Assert.Throws<ArgumentException>(() => EnvelopeCodec.Encode(message with { SupersessionKey = "user@example.com" }));
        Assert.Throws<ArgumentException>(() => EnvelopeCodec.Encode(message with { Payload = message.Payload with { Algorithm = "plaintext" } }));
        Assert.Throws<ArgumentException>(() => EnvelopeCodec.Encode(message with { Payload = message.Payload with { Ciphertext = Convert.ToBase64String(new byte[16_385]) } }));
        Assert.False(EnvelopeCodec.TryDecode(new byte[32_769], "notification.email.requested", 1, "identity", out _, out _));
        var json = System.Text.Encoding.UTF8.GetString(EnvelopeCodec.Encode(message));
        var duplicate = "{\"version\":2," + json[1..];
        Assert.False(EnvelopeCodec.TryDecode(System.Text.Encoding.UTF8.GetBytes(duplicate), "notification.email.requested", 1, "identity", out _, out _));
    }

    [Fact]
    public void Failure_messages_have_only_safe_references_and_bounded_retention()
    {
        var now = DateTimeOffset.UtcNow;
        var reference = new MessageFailureReference(Guid.NewGuid(), Guid.NewGuid(), "invalid-contract", now, now.AddDays(30));
        using var json = JsonDocument.Parse(EnvelopeCodec.EncodeFailure(reference, now));
        Assert.Equal(new[] { "code", "failureId", "messageId", "occurredAt", "retainUntil", "type", "version" },
            json.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Throws<ArgumentException>(() => EnvelopeCodec.EncodeFailure(reference with { Code = "user@example.com" }, now));
        Assert.Throws<ArgumentException>(() => EnvelopeCodec.EncodeFailure(reference with { RetainUntil = now.AddDays(31) }, now));
    }

    [Fact]
    public void Client_factory_uses_tls_outside_local_development_and_bounded_resources()
    {
        var factory = RabbitConnectionFactory.Create("broker.test", 5671, "identity_publisher", new string('x', 32), false);
        Assert.True(factory.Ssl.Enabled);
        Assert.Equal("broker.test", factory.Ssl.ServerName);
        Assert.Equal(System.Net.Security.SslPolicyErrors.None, factory.Ssl.AcceptablePolicyErrors);
        Assert.Equal(8, factory.RequestedChannelMax);
        Assert.True(factory.AutomaticRecoveryEnabled);
        Assert.Throws<ArgumentException>(() => RabbitConnectionFactory.Create("broker.test", 5672, "guest", new string('x', 32), true));
    }
}
