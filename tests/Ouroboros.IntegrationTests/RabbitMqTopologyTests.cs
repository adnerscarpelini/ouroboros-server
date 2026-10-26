using System.Net;
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Ouroboros.IntegrationTests;

public sealed class RabbitMqTopologyTests(RabbitMqFixture broker) : IClassFixture<RabbitMqFixture>
{
    [Fact]
    public async Task Environment_file_can_provision_the_existing_broker_without_printing_secrets()
    {
        await broker.ProvisionAsync(useEnvFile: true);
        await using var connection = await broker.ConnectAsync("notifications_consumer", broker.ConsumerPassword);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
        var result = await channel.QueueDeclarePassiveAsync(RabbitMqFixture.Queue, TestContext.Current.CancellationToken);
        Assert.Equal(RabbitMqFixture.Queue, result.QueueName);
    }

    [Fact]
    public async Task Runtime_credentials_have_only_the_approved_capabilities()
    {
        await using var identity = await broker.ConnectAsync("identity_publisher", broker.IdentityPassword);
        await using var consumer = await broker.ConnectAsync("notifications_consumer", broker.ConsumerPassword);
        await using var failures = await broker.ConnectAsync("notifications_failures", broker.FailuresPassword);
        await using (var channel = await identity.CreateChannelAsync(new CreateChannelOptions(true, true), TestContext.Current.CancellationToken))
        {
            await channel.BasicPublishAsync(RabbitMqFixture.Exchange, "email.requested.v1", true,
                new BasicProperties { Persistent = true }, Encoding.UTF8.GetBytes("opaque-test-payload"), TestContext.Current.CancellationToken);
        }
        await DeniedAsync(identity, channel => channel.BasicGetAsync(RabbitMqFixture.Queue, true, TestContext.Current.CancellationToken));
        await DeniedAsync(identity, channel => channel.QueueDeclareAsync("unauthorized", true, false, false));
        await DeniedAsync(identity, channel => channel.QueueDeclareAsync(RabbitMqFixture.Exchange, true, false, false));
        await DeniedAsync(identity, channel => channel.BasicPublishAsync(RabbitMqFixture.FailuresExchange, "email.failed.v1", true, new BasicProperties(), ReadOnlyMemory<byte>.Empty).AsTask());
        await DeniedAsync(consumer, channel => channel.BasicPublishAsync(RabbitMqFixture.Exchange, "email.requested.v1", true, new BasicProperties(), ReadOnlyMemory<byte>.Empty).AsTask());
        await DeniedAsync(consumer, channel => channel.QueueDeclareAsync("unauthorized", true, false, false));
        await DeniedAsync(consumer, channel => channel.BasicGetAsync(RabbitMqFixture.FailuresQueue, true));
        await DeniedAsync(failures, channel => channel.BasicPublishAsync(RabbitMqFixture.Exchange, "email.requested.v1", true, new BasicProperties(), ReadOnlyMemory<byte>.Empty).AsTask());
        await DeniedAsync(failures, channel => channel.BasicGetAsync(RabbitMqFixture.Queue, true, TestContext.Current.CancellationToken));
        await DeniedAsync(failures, channel => channel.QueueDeclareAsync("unauthorized", true, false, false));
        await using (var channel = await consumer.CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.NotNull(await channel.BasicGetAsync(RabbitMqFixture.Queue, true, TestContext.Current.CancellationToken));
        }
        await using (var channel = await failures.CreateChannelAsync(new CreateChannelOptions(true, true), TestContext.Current.CancellationToken))
        {
            var safe = JsonSerializer.SerializeToUtf8Bytes(new { failureId = Guid.NewGuid(), code = "invalid-contract" });
            await channel.BasicPublishAsync(RabbitMqFixture.FailuresExchange, "email.failed.v1", true,
                new BasicProperties { Persistent = true }, safe, TestContext.Current.CancellationToken);
        }
        using var management = broker.ManagementClient();
        Assert.Equal(HttpStatusCode.NotFound, (await management.GetAsync("users/guest", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Durable_topology_and_persistent_messages_survive_restart_and_reprovision()
    {
        var marker = Encoding.UTF8.GetBytes(Guid.NewGuid().ToString());
        await using (var connection = await broker.ConnectAsync())
        await using (var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), TestContext.Current.CancellationToken))
        {
            await channel.QueuePurgeAsync(RabbitMqFixture.Queue, cancellationToken: TestContext.Current.CancellationToken);
            await channel.BasicPublishAsync(RabbitMqFixture.Exchange, "email.requested.v1", true,
                new BasicProperties { Persistent = true }, marker, TestContext.Current.CancellationToken);
        }
        await broker.RestartAsync();
        await broker.ProvisionAsync();
        await using var restarted = await broker.ConnectAsync();
        await using var verify = await restarted.CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
        var message = await verify.BasicGetAsync(RabbitMqFixture.Queue, false, TestContext.Current.CancellationToken);
        Assert.NotNull(message);
        Assert.Equal(marker, message.Body.ToArray());
        Assert.True(message.BasicProperties.Persistent);
        await verify.BasicAckAsync(message.DeliveryTag, false, TestContext.Current.CancellationToken);
        using var management = broker.ManagementClient();
        using var response = await management.GetAsync("queues/ouroboros/notifications.failures.v1", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2_592_000_000L, json.RootElement.GetProperty("arguments").GetProperty("x-message-ttl").GetInt64());
    }

    private static async Task DeniedAsync(IConnection connection, Func<IChannel, Task> action)
    {
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<Exception>(() => action(channel));
        Assert.Equal((ushort)403, channel.CloseReason?.ReplyCode);
    }
}
