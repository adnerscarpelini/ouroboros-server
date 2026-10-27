extern alias identityapi;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using identityapi::Ouroboros.Identity.Api.CommandLine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ouroboros.Hosting;
using Ouroboros.Identity.Application;
using Ouroboros.Identity.Contracts.Administrators;
using Ouroboros.Identity.Domain.Notifications;
using Ouroboros.Identity.Infrastructure.Messaging;
using Ouroboros.Identity.Infrastructure.Persistence;
using Ouroboros.Messaging;
using Ouroboros.Messaging.Contracts;
using Ouroboros.Notifications;
using Ouroboros.Notifications.Application;
using Ouroboros.Notifications.Contracts;
using Ouroboros.Notifications.Domain;
using Ouroboros.Notifications.Infrastructure;
using Ouroboros.Notifications.Infrastructure.Email;
using Ouroboros.Notifications.Infrastructure.Persistence;
using RabbitMQ.Client;

namespace Ouroboros.IntegrationTests;

[Collection("Telemetry")]
public sealed class ServiceUpdateAndRecoveryTests(RabbitMqFixture broker, ProvisionedInstance database)
    : IClassFixture<RabbitMqFixture>, IClassFixture<ProvisionedInstance>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Compatible_notifications_release_updates_and_reverts_without_redeploying_identity()
    {
        await EnsureMigratedAndCleanAsync();

        var identityRuntimeCs = database.ConnectionStringFor(ProvisionedInstance.IdentityRuntime, ProvisionedInstance.IdentityDatabase);
        var notificationsRuntimeCs = database.ConnectionStringFor(ProvisionedInstance.NotificationsRuntime, ProvisionedInstance.NotificationsDatabase);

        // Publica duas releases reais e compatíveis de Notifications.Worker (1.0.0+rev100 e 1.1.0+rev110).
        var releaseRoot = Path.Combine(Path.GetTempPath(), "ouroboros-worker-releases-" + Guid.NewGuid().ToString("N"));
        var v100Dir = Path.Combine(releaseRoot, "v1.0.0");
        var v110Dir = Path.Combine(releaseRoot, "v1.1.0");
        await PublishWorkerReleaseAsync("1.0.0", "rev100", v100Dir);
        await PublishWorkerReleaseAsync("1.1.0", "rev110", v110Dir);

        using var keys = new NotificationTestKeys();
        try
        {
            // Identity permanece em execução contínua durante toda a atualização e reversão de Notifications.
            await using var identityApi = CreateIdentityApi(identityRuntimeCs);
            using var publicClient = identityApi.CreateClient();
            using var metrics = new EmailOutboxMetrics();
            using var publisher = CreateOutboxPublisher(identityApi.Services, metrics);

            // 1. Notifications na versão anterior compatível (1.0.0+rev100) consome intenção real gerada pelo Identity.
            var portV100First = FreeTcpPort();
            using (var workerV100 = StartPublishedWorker(v100Dir, keys, notificationsRuntimeCs, portV100First, useSharedIdentityKeyRing: true))
            using (var healthClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{portV100First}") })
            {
                var stdout = workerV100.StandardOutput.ReadToEndAsync(Cancellation);
                try
                {
                    await WaitForHttpStatusAsync(healthClient, OuroborosHealthExtensions.ReadyPath, HttpStatusCode.OK);
                    using var registration1 = await publicClient.PostAsJsonAsync(
                        "/api/v1/identity/registrations",
                        new { Email = "update-step1@example.com" },
                        Cancellation);
                    Assert.Equal(HttpStatusCode.Accepted, registration1.StatusCode);

                    await publisher.RunOnceAsync(Cancellation);
                    await WaitForDeliveredEmailCountAsync(keys.EmailsPath, 1);
                    await Task.Delay(1200, Cancellation);
                }
                finally
                {
                    await StopProcessAsync(workerV100);
                }

                Assert.Contains("\"serviceVersion\":\"1.0.0+rev100\"", await stdout, StringComparison.Ordinal);
            }

            // 2. Atualiza Notifications para 1.1.0+rev110 sem reimplantar nem reiniciar Identity.
            // Valida consumo tanto da intenção padrão do Identity quanto de contrato v1 com campo opcional aditivo.
            MessageEnvelope additiveMessage;
            var portV110 = FreeTcpPort();
            using (var workerV110 = StartPublishedWorker(v110Dir, keys, notificationsRuntimeCs, portV110, useSharedIdentityKeyRing: true))
            using (var healthClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{portV110}") })
            {
                var stdout = workerV110.StandardOutput.ReadToEndAsync(Cancellation);
                try
                {
                    await WaitForHttpStatusAsync(healthClient, OuroborosHealthExtensions.ReadyPath, HttpStatusCode.OK);
                    using var registration2 = await publicClient.PostAsJsonAsync(
                        "/api/v1/identity/registrations",
                        new { Email = "update-step2@example.com" },
                        Cancellation);
                    Assert.Equal(HttpStatusCode.Accepted, registration2.StatusCode);

                    await publisher.RunOnceAsync(Cancellation);

                    additiveMessage = keys.Message(
                        new EmailContent("additive-v110@example.com", "Contrato compatível v1", "Use http://localhost/confirm?token=private-test-token", "registration"),
                        metadata => metadata with { SupersessionKey = "additive-v110" });
                    await PublishRawAsync(AddOptionalJsonField(EnvelopeCodec.Encode(additiveMessage), "optionalExtension", "v1-compatible"), additiveMessage.MessageId);

                    await WaitForDeliveredEmailCountAsync(keys.EmailsPath, 3);
                    await Task.Delay(1200, Cancellation);
                }
                finally
                {
                    await StopProcessAsync(workerV110);
                }

                Assert.Contains("\"serviceVersion\":\"1.1.0+rev110\"", await stdout, StringComparison.Ordinal);
            }

            // 3. Reverte Notifications para 1.0.0+rev100 sem reimplantar Identity e sem perder deduplicação.
            var portV100Rollback = FreeTcpPort();
            using (var workerRollback = StartPublishedWorker(v100Dir, keys, notificationsRuntimeCs, portV100Rollback, useSharedIdentityKeyRing: true))
            using (var healthClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{portV100Rollback}") })
            {
                var stdout = workerRollback.StandardOutput.ReadToEndAsync(Cancellation);
                try
                {
                    await WaitForHttpStatusAsync(healthClient, OuroborosHealthExtensions.ReadyPath, HttpStatusCode.OK);

                    // Reentrega de mensagem já entregue durante a versão 1.1.0 é deduplicada após o rollback.
                    await PublishRawAsync(EnvelopeCodec.Encode(additiveMessage), additiveMessage.MessageId);

                    // Nova intenção do mesmo Identity em execução e novo envelope com campo opcional continuam aceitos pela versão 1.0.0.
                    using var registration3 = await publicClient.PostAsJsonAsync(
                        "/api/v1/identity/registrations",
                        new { Email = "update-step3@example.com" },
                        Cancellation);
                    Assert.Equal(HttpStatusCode.Accepted, registration3.StatusCode);

                    await publisher.RunOnceAsync(Cancellation);

                    var rollbackAdditive = keys.Message(
                        new EmailContent("additive-v100@example.com", "Rollback compatível v1", "Use http://localhost/confirm?token=private-test-token", "registration"),
                        metadata => metadata with { SupersessionKey = "additive-v100" });
                    await PublishRawAsync(AddOptionalJsonField(EnvelopeCodec.Encode(rollbackAdditive), "futureMetadata", "ignored-safely"), rollbackAdditive.MessageId);

                    await WaitForDeliveredEmailCountAsync(keys.EmailsPath, 5);
                    await Task.Delay(1200, Cancellation);
                }
                finally
                {
                    await StopProcessAsync(workerRollback);
                }

                Assert.Contains("\"serviceVersion\":\"1.0.0+rev100\"", await stdout, StringComparison.Ordinal);
            }

            // A mensagem entregue antes do rollback permanece com exatamente 1 tentativa na inbox.
            await using var notificationsDb = NotificationDatabaseScenario.Context(database);
            var deduplicatedRow = await notificationsDb.Inbox.AsNoTracking().SingleAsync(row => row.MessageId == additiveMessage.MessageId, Cancellation);
            Assert.Equal(InboxState.Delivered, deduplicatedRow.State);
            Assert.Equal(1, deduplicatedRow.Attempts);
        }
        finally
        {
            if (Directory.Exists(releaseRoot))
            {
                Directory.Delete(releaseRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Stopping_and_restarting_notifications_keeps_login_working_and_delivers_pending_intents_on_recovery()
    {
        await EnsureMigratedAndCleanAsync();

        var identityRuntimeCs = database.ConnectionStringFor(ProvisionedInstance.IdentityRuntime, ProvisionedInstance.IdentityDatabase);
        var notificationsRuntimeCs = database.ConnectionStringFor(ProvisionedInstance.NotificationsRuntime, ProvisionedInstance.NotificationsDatabase);

        const string adminEmail = "admin-notif-down@adnerscarpelini.com";
        const string adminPassword = "uma frase longa e segura para o admin do teste";
        await CreateAdministratorAsync(identityRuntimeCs, adminEmail, adminPassword);

        using var keys = new NotificationTestKeys();
        await using var identityApi = CreateIdentityApi(identityRuntimeCs);
        using var publicClient = identityApi.CreateClient();
        using var adminClient = CreateAdminClient(identityApi);
        using var metrics = new EmailOutboxMetrics();
        using var publisher = CreateOutboxPublisher(identityApi.Services, metrics);

        // Com Notifications parado: Identity continua pronto, login funciona e intenção de e-mail fica registrada e aguarda entrega.
        using (var readyResponse = await publicClient.GetAsync(OuroborosHealthExtensions.ReadyPath, Cancellation))
        {
            Assert.Equal(HttpStatusCode.OK, readyResponse.StatusCode);
        }

        using (var loginResponse = await adminClient.PostAsJsonAsync(
            "/api/v1/admin/identity/sessions",
            new { Email = adminEmail, Password = adminPassword },
            Cancellation))
        {
            Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        }

        using (var registration = await publicClient.PostAsJsonAsync(
            "/api/v1/identity/registrations",
            new { Email = "waiting-for-notifications@example.com" },
            Cancellation))
        {
            Assert.Equal(HttpStatusCode.Accepted, registration.StatusCode);
        }

        await publisher.RunOnceAsync(Cancellation);
        Assert.False(Directory.Exists(keys.EmailsPath));

        // Ao iniciar Notifications, o serviço fica pronto e entrega a mensagem que aguardava recuperação.
        var healthPort = FreeTcpPort();
        using var worker = StartDefaultWorker(keys, notificationsRuntimeCs, healthPort, useSharedIdentityKeyRing: true);
        using var workerHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{healthPort}") };
        try
        {
            await WaitForHttpStatusAsync(workerHttp, OuroborosHealthExtensions.ReadyPath, HttpStatusCode.OK);
            await WaitForDeliveredEmailCountAsync(keys.EmailsPath, 1);
        }
        finally
        {
            await StopProcessAsync(worker);
        }

        await using var notificationsDb = NotificationDatabaseScenario.Context(database);
        var delivered = await notificationsDb.Inbox.AsNoTracking().SingleAsync(Cancellation);
        Assert.Equal(InboxState.Delivered, delivered.State);
    }

    [Fact]
    public async Task Stopping_and_restarting_rabbitmq_keeps_login_working_preserves_outbox_and_queued_messages_and_delivers_after_recovery()
    {
        await EnsureMigratedAndCleanAsync();

        var identityRuntimeCs = database.ConnectionStringFor(ProvisionedInstance.IdentityRuntime, ProvisionedInstance.IdentityDatabase);
        var notificationsRuntimeCs = database.ConnectionStringFor(ProvisionedInstance.NotificationsRuntime, ProvisionedInstance.NotificationsDatabase);

        const string adminEmail = "admin-broker-down@adnerscarpelini.com";
        const string adminPassword = "uma frase longa e segura para o admin do teste";
        await CreateAdministratorAsync(identityRuntimeCs, adminEmail, adminPassword);

        using var keys = new NotificationTestKeys();
        await using var identityApi = CreateIdentityApi(identityRuntimeCs);
        using var publicClient = identityApi.CreateClient();
        using var adminClient = CreateAdminClient(identityApi);
        using var metrics = new EmailOutboxMetrics();
        using var publisher = CreateOutboxPublisher(identityApi.Services, metrics);

        // Publica uma mensagem persistente na fila antes da queda do broker (sem consumidor ativo ainda).
        var preQueuedMessage = keys.Message(
            new EmailContent("queued-before-broker-stop@example.com", "Mensagem persistente em fila", "Use http://localhost/confirm?token=private-test-token", "registration"),
            metadata => metadata with { SupersessionKey = "queued-before-stop" });
        await PublishRawAsync(EnvelopeCodec.Encode(preQueuedMessage), preQueuedMessage.MessageId);

        var healthPort = FreeTcpPort();
        Process? worker = null;
        using var workerHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{healthPort}") };

        await broker.StopAsync();
        try
        {
            worker = StartDefaultWorker(keys, notificationsRuntimeCs, healthPort, useSharedIdentityKeyRing: true);

            // Com o RabbitMQ parado: Notifications está vivo (200), mas não pronto (503).
            await WaitForHttpStatusAsync(workerHttp, OuroborosHealthExtensions.LivePath, HttpStatusCode.OK);
            using var workerReady = await workerHttp.GetAsync(OuroborosHealthExtensions.ReadyPath, Cancellation);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, workerReady.StatusCode);
            Assert.Equal("{\"status\":\"Unhealthy\"}", await workerReady.Content.ReadAsStringAsync(Cancellation));

            // Identity continua pronto e o login administrativo funciona normalmente.
            using var identityReady = await publicClient.GetAsync(OuroborosHealthExtensions.ReadyPath, Cancellation);
            Assert.Equal(HttpStatusCode.OK, identityReady.StatusCode);

            using var loginResponse = await adminClient.PostAsJsonAsync(
                "/api/v1/admin/identity/sessions",
                new { Email = adminEmail, Password = adminPassword },
                Cancellation);
            Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

            // Nova intenção criada durante a queda do broker permanece pendente na outbox do Identity.
            using var registration = await publicClient.PostAsJsonAsync(
                "/api/v1/identity/registrations",
                new { Email = "outbox-during-broker-stop@example.com" },
                Cancellation);
            Assert.Equal(HttpStatusCode.Accepted, registration.StatusCode);

            await publisher.RunOnceAsync(Cancellation);

            await using var scope = identityApi.Services.CreateAsyncScope();
            var identityDb = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var pendingIntent = await identityDb.EmailIntents.AsNoTracking().SingleAsync(Cancellation);
            Assert.Equal(EmailIntentState.Pending, pendingIntent.State);
            Assert.NotNull(pendingIntent.ProtectedEnvelope);
            Assert.NotNull(pendingIntent.NextAttemptAt);
        }
        finally
        {
            await broker.StartAsync();
        }

        try
        {
            // Após o retorno do RabbitMQ: a mensagem persistente já enfileirada sobreviveu no volume do broker
            // e a intenção pendente na outbox é publicada e entregue por Notifications.
            await WaitForHttpStatusAsync(workerHttp, OuroborosHealthExtensions.ReadyPath, HttpStatusCode.OK);

            await database.ExecuteAdminAsync(ProvisionedInstance.IdentityDatabase, "UPDATE identity.email_intents SET next_attempt_at = NULL");
            await publisher.RunOnceAsync(Cancellation);

            await WaitForDeliveredEmailCountAsync(keys.EmailsPath, 2);

            await using var scope = identityApi.Services.CreateAsyncScope();
            var identityDb = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var publishedIntent = await identityDb.EmailIntents.AsNoTracking().SingleAsync(Cancellation);
            Assert.Equal(EmailIntentState.Published, publishedIntent.State);
            Assert.Null(publishedIntent.ProtectedEnvelope);

            await using var notificationsDb = NotificationDatabaseScenario.Context(database);
            Assert.Equal(2, await notificationsDb.Inbox.CountAsync(row => row.State == InboxState.Delivered, Cancellation));
        }
        finally
        {
            if (worker is not null)
            {
                await StopProcessAsync(worker);
            }
        }
    }

    [Fact]
    public async Task Publisher_and_consumer_redeliveries_preserve_message_id_and_handle_duplication_explicitly()
    {
        await EnsureMigratedAndCleanAsync();

        var identityRuntimeCs = database.ConnectionStringFor(ProvisionedInstance.IdentityRuntime, ProvisionedInstance.IdentityDatabase);
        var notificationsRuntimeCs = database.ConnectionStringFor(ProvisionedInstance.NotificationsRuntime, ProvisionedInstance.NotificationsDatabase);

        using var keys = new NotificationTestKeys();
        await using var identityApi = CreateIdentityApi(identityRuntimeCs);
        using var publicClient = identityApi.CreateClient();
        using var metrics = new EmailOutboxMetrics();
        using var publisher = CreateOutboxPublisher(identityApi.Services, metrics);

        // Parte 1: parada do publicador após confirmação do broker e antes de registrar o progresso local.
        using (var registration = await publicClient.PostAsJsonAsync(
            "/api/v1/identity/registrations",
            new { Email = "publisher-crash@example.com" },
            Cancellation))
        {
            Assert.Equal(HttpStatusCode.Accepted, registration.StatusCode);
        }

        Guid publisherMessageId;
        await using (var scope = identityApi.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<EmailOutboxStore>();
            var lease = Guid.CreateVersion7();
            var claimed = Assert.Single(await store.ClaimAsync(lease, DateTimeOffset.UtcNow, 1, Cancellation));
            publisherMessageId = claimed.ExternalId;
            await using var transport = new ConfirmedEmailPublisher(
                Options.Create(new EmailPublisherOptions { Host = broker.Host, Port = broker.Port, Password = broker.IdentityPassword, BatchSize = 4 }),
                new TestHostEnvironment());
            await transport.PublishAsync(claimed, Cancellation);
            // Simula parada abrupta do publicador antes de ConfirmAsync.
        }

        await database.ExecuteAdminAsync(ProvisionedInstance.IdentityDatabase, "UPDATE identity.email_intents SET lease_until = now() - interval '1 second'");
        await publisher.RunOnceAsync(Cancellation);

        // Notifications consome as duas entregas com o mesmo MessageId e executa o envio apenas uma vez.
        var healthPort = FreeTcpPort();
        using (var worker = StartDefaultWorker(keys, notificationsRuntimeCs, healthPort, useSharedIdentityKeyRing: true))
        using (var workerHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{healthPort}") })
        {
            try
            {
                await WaitForHttpStatusAsync(workerHttp, OuroborosHealthExtensions.ReadyPath, HttpStatusCode.OK);
                await WaitForDeliveredEmailCountAsync(keys.EmailsPath, 1);
            }
            finally
            {
                await StopProcessAsync(worker);
            }
        }

        await using (var notificationsDb = NotificationDatabaseScenario.Context(database))
        {
            var row = await notificationsDb.Inbox.AsNoTracking().SingleAsync(item => item.MessageId == publisherMessageId, Cancellation);
            Assert.Equal(InboxState.Delivered, row.State);
            Assert.Equal(1, row.Attempts);
        }

        // Parte 2: parada do consumidor após o envio externo e antes da confirmação local (DeliverAsync).
        var consumerCrashMessage = keys.Message(
            new EmailContent("consumer-crash@example.com", "Reentrega após envio", "Use http://localhost/confirm?token=private-test-token", "registration"),
            metadata => metadata with { SupersessionKey = "consumer-crash-key" });

        var sentMessageIds = new List<Guid>();
        using var host = BuildNotificationsHostWithTransportSpy(keys, notificationsRuntimeCs, sentMessageIds);

        await using (var acceptScope = host.Services.CreateAsyncScope())
        {
            var acceptance = acceptScope.ServiceProvider.GetRequiredService<InboxAcceptance>();
            Assert.Equal(
                AcceptanceResult.Accepted,
                await acceptance.AcceptAsync(EnvelopeCodec.Encode(consumerCrashMessage), consumerCrashMessage.MessageId.ToString("D"), Cancellation));
        }

        // Primeira tentativa: reclama a mensagem, registra BeginAttempt (Attempts = 1) e conclui o envio,
        // mas interrompe antes de DeliverAsync confirmar localmente no banco.
        await using (var firstAttemptScope = host.Services.CreateAsyncScope())
        {
            var store = firstAttemptScope.ServiceProvider.GetRequiredService<InboxExecutionStore>();
            var reader = firstAttemptScope.ServiceProvider.GetRequiredService<IEmailPayloadReader>();
            var transport = firstAttemptScope.ServiceProvider.GetRequiredService<IEmailTransport>();
            var now = DateTimeOffset.UtcNow;
            var lease = Guid.CreateVersion7();
            var claimed = Assert.Single(await store.ClaimAsync(lease, now, Cancellation));
            Assert.Equal(consumerCrashMessage.MessageId, claimed.MessageId);
            Assert.Equal(1, await store.BeginAttemptAsync(claimed.MessageId, lease, now, now.AddMinutes(1), Cancellation));
            var content = reader.Read(consumerCrashMessage);
            await transport.SendAsync(claimed.MessageId, content, Cancellation);
            // Simula parada do consumidor antes de store.DeliverAsync.
        }

        await using (var notificationsDb = NotificationDatabaseScenario.Context(database))
        {
            var sendingRow = await notificationsDb.Inbox.AsNoTracking().SingleAsync(item => item.MessageId == consumerCrashMessage.MessageId, Cancellation);
            Assert.Equal(InboxState.Sending, sendingRow.State);
            Assert.Equal(1, sendingRow.Attempts);
            Assert.NotNull(sendingRow.ProtectedEnvelope);
        }

        // Expira a lease e a janela de nova tentativa; o executor retoma a mensagem com o mesmo MessageId.
        await database.ExecuteAdminAsync(
            ProvisionedInstance.NotificationsDatabase,
            "UPDATE notifications.inbox SET lease_until = now() - interval '1 second', next_attempt_at = now() - interval '1 second' WHERE state = 'Sending'");

        var executor = new InboxExecutor(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            host.Services.GetRequiredService<IOptions<NotificationOptions>>(),
            TimeProvider.System,
            NullLogger<InboxExecutor>.Instance);
        await executor.RunOnceAsync(Cancellation);

        Assert.Equal([consumerCrashMessage.MessageId, consumerCrashMessage.MessageId], sentMessageIds);

        await using (var notificationsDb = NotificationDatabaseScenario.Context(database))
        {
            var deliveredRow = await notificationsDb.Inbox.AsNoTracking().SingleAsync(item => item.MessageId == consumerCrashMessage.MessageId, Cancellation);
            Assert.Equal(InboxState.Delivered, deliveredRow.State);
            Assert.Equal(2, deliveredRow.Attempts);
            Assert.Null(deliveredRow.ProtectedEnvelope);
        }

        // O PickupEmailTransport é idempotente por {messageId:N}.eml e preserva o Message-ID RFC 5322.
        var emlFiles = Directory.GetFiles(keys.EmailsPath, "*.eml");
        Assert.Equal(2, emlFiles.Length);
        var consumerEml = await File.ReadAllTextAsync(Path.Combine(keys.EmailsPath, $"{consumerCrashMessage.MessageId:N}.eml"), Cancellation);
        Assert.Contains($"Message-ID: <{consumerCrashMessage.MessageId:N}@ouroboros.local>", consumerEml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shared_postgres_outage_affects_both_services_without_leaking_secrets_and_recovers_after_restart_and_additive_migration_rollback_preserves_data()
    {
        await EnsureMigratedAndCleanAsync();

        var identityRuntimeCs = database.ConnectionStringFor(ProvisionedInstance.IdentityRuntime, ProvisionedInstance.IdentityDatabase);
        var notificationsRuntimeCs = database.ConnectionStringFor(ProvisionedInstance.NotificationsRuntime, ProvisionedInstance.NotificationsDatabase);

        const string adminEmail = "admin-pg-outage@adnerscarpelini.com";
        const string adminPassword = "uma frase longa e segura para o admin do teste";
        await CreateAdministratorAsync(identityRuntimeCs, adminEmail, adminPassword);

        using var keys = new NotificationTestKeys();
        await using var identityApi = CreateIdentityApi(identityRuntimeCs);
        using var publicClient = identityApi.CreateClient();
        using var adminClient = CreateAdminClient(identityApi);

        var healthPort = FreeTcpPort();
        using var worker = StartDefaultWorker(keys, notificationsRuntimeCs, healthPort, useSharedIdentityKeyRing: true);
        using var workerHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{healthPort}") };

        try
        {
            await WaitForHttpStatusAsync(workerHttp, OuroborosHealthExtensions.ReadyPath, HttpStatusCode.OK);
            using (var readyBefore = await publicClient.GetAsync(OuroborosHealthExtensions.ReadyPath, Cancellation))
            {
                Assert.Equal(HttpStatusCode.OK, readyBefore.StatusCode);
            }

            // Para o container PostgreSQL compartilhado entre ouroboros_identity e ouroboros_notifications.
            await database.StopAsync();
            try
            {
                // Impacto comum: ambos os serviços permanecem vivos (/health/live 200), mas reportam não prontos (/health/ready 503) sem vazar segredos.
                using var identityLive = await publicClient.GetAsync(OuroborosHealthExtensions.LivePath, Cancellation);
                Assert.Equal(HttpStatusCode.OK, identityLive.StatusCode);

                using var identityReady = await publicClient.GetAsync(OuroborosHealthExtensions.ReadyPath, Cancellation);
                var identityReadyBody = await identityReady.Content.ReadAsStringAsync(Cancellation);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, identityReady.StatusCode);
                Assert.Equal("{\"status\":\"Unhealthy\"}", identityReadyBody);
                AssertNoSecretsExposed(identityReadyBody);

                using var workerLive = await workerHttp.GetAsync(OuroborosHealthExtensions.LivePath, Cancellation);
                Assert.Equal(HttpStatusCode.OK, workerLive.StatusCode);

                using var workerReady = await workerHttp.GetAsync(OuroborosHealthExtensions.ReadyPath, Cancellation);
                var workerReadyBody = await workerReady.Content.ReadAsStringAsync(Cancellation);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, workerReady.StatusCode);
                Assert.Equal("{\"status\":\"Unhealthy\"}", workerReadyBody);
                AssertNoSecretsExposed(workerReadyBody);

                using var failedLogin = await adminClient.PostAsJsonAsync(
                    "/api/v1/admin/identity/sessions",
                    new { Email = adminEmail, Password = adminPassword },
                    Cancellation);
                var failedLoginBody = await failedLogin.Content.ReadAsStringAsync(Cancellation);
                Assert.False(failedLogin.IsSuccessStatusCode);
                AssertNoSecretsExposed(failedLoginBody);
            }
            finally
            {
                await database.StartAsync();
            }

            // Recuperação após retorno do PostgreSQL: ambos voltam a 200 OK em /health/ready e o login volta a funcionar.
            await WaitForHttpStatusAsync(publicClient, OuroborosHealthExtensions.ReadyPath, HttpStatusCode.OK);
            await WaitForHttpStatusAsync(workerHttp, OuroborosHealthExtensions.ReadyPath, HttpStatusCode.OK);

            using (var recoveredLogin = await adminClient.PostAsJsonAsync(
                "/api/v1/admin/identity/sessions",
                new { Email = adminEmail, Password = adminPassword },
                Cancellation))
            {
                Assert.Equal(HttpStatusCode.OK, recoveredLogin.StatusCode);
            }

            // Limites de rollback de migrations e preservação de novas escritas:
            // Uma migration aditiva aplicada por notifications_migrator e novas escritas permanecem preservadas
            // quando o binário anterior continua executando sem downgrade destrutivo.
            await using (var migratorConnection = database.Connect(ProvisionedInstance.NotificationsMigrator, ProvisionedInstance.NotificationsDatabase))
            {
                await migratorConnection.OpenAsync(Cancellation);
                await using var alterCmd = migratorConnection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE notifications.inbox ADD COLUMN IF NOT EXISTS rollback_audit_note text NULL;";
                await alterCmd.ExecuteNonQueryAsync(Cancellation);
            }

            var messageAfterAdditiveMigration = keys.Message(
                new EmailContent("additive-schema@example.com", "Preservação no rollback", "Use http://localhost/confirm?token=private-test-token", "registration"),
                metadata => metadata with { SupersessionKey = "additive-schema-key" });
            await PublishRawAsync(EnvelopeCodec.Encode(messageAfterAdditiveMigration), messageAfterAdditiveMigration.MessageId);
            await WaitForDeliveredEmailCountAsync(keys.EmailsPath, 1);

            await database.ExecuteAdminAsync(
                ProvisionedInstance.NotificationsDatabase,
                $"UPDATE notifications.inbox SET rollback_audit_note = 'preserved-across-binary-rollback' WHERE message_id = '{messageAfterAdditiveMigration.MessageId:D}'");

            using (var readyAfterWrite = await workerHttp.GetAsync(OuroborosHealthExtensions.ReadyPath, Cancellation))
            {
                var body = await readyAfterWrite.Content.ReadAsStringAsync(Cancellation);
                Assert.Equal(HttpStatusCode.OK, readyAfterWrite.StatusCode);
                Assert.Equal("{\"status\":\"Healthy\"}", body);
                AssertNoSecretsExposed(body);
            }

            await using (var verifyConnection = database.Connect(ProvisionedInstance.NotificationsRuntime, ProvisionedInstance.NotificationsDatabase))
            {
                await verifyConnection.OpenAsync(Cancellation);
                await using var checkCmd = verifyConnection.CreateCommand();
                checkCmd.CommandText = $"SELECT rollback_audit_note FROM notifications.inbox WHERE message_id = '{messageAfterAdditiveMigration.MessageId:D}';";
                var preservedValue = (string?)await checkCmd.ExecuteScalarAsync(Cancellation);
                Assert.Equal("preserved-across-binary-rollback", preservedValue);
            }

            await using (var migratorCleanup = database.Connect(ProvisionedInstance.NotificationsMigrator, ProvisionedInstance.NotificationsDatabase))
            {
                await migratorCleanup.OpenAsync(Cancellation);
                await using var dropCmd = migratorCleanup.CreateCommand();
                dropCmd.CommandText = "ALTER TABLE notifications.inbox DROP COLUMN IF EXISTS rollback_audit_note;";
                await dropCmd.ExecuteNonQueryAsync(Cancellation);
            }
        }
        finally
        {
            await StopProcessAsync(worker);
        }
    }

    private void AssertNoSecretsExposed(string content)
    {
        foreach (var secret in database.Passwords.Values.Concat([broker.IdentityPassword, broker.ConsumerPassword, broker.FailuresPassword]))
        {
            Assert.DoesNotContain(secret, content, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Password=", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Host=", content, StringComparison.OrdinalIgnoreCase);
    }

    private async Task EnsureMigratedAndCleanAsync()
    {
        var identityMigratorCs = database.ConnectionStringFor(ProvisionedInstance.IdentityMigrator, ProvisionedInstance.IdentityDatabase);
        var notificationsMigratorCs = database.ConnectionStringFor(ProvisionedInstance.NotificationsMigrator, ProvisionedInstance.NotificationsDatabase);

        using (var outWriter = new StringWriter())
        using (var errWriter = new StringWriter())
        {
            var exitCode = await MigrateDatabaseCommand.RunAsync(
                key => key == "ConnectionStrings__Identity" ? identityMigratorCs : null,
                outWriter,
                errWriter,
                Cancellation);
            Assert.Equal(MigrateDatabaseCommand.Success, exitCode);
        }

        await Ouroboros.Notifications.DependencyInjection.MigrateDatabaseAsync(notificationsMigratorCs, Cancellation);
        Assert.Equal(0, (await database.ProvisionAsync()).ExitCode);

        await database.ExecuteAdminAsync(
            ProvisionedInstance.IdentityDatabase,
            "TRUNCATE TABLE identity.email_intents, identity.email_tokens, identity.refresh_tokens, identity.sessions, identity.users RESTART IDENTITY CASCADE;");
        await NotificationDatabaseScenario.ResetAsync(database);

        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Cancellation);
        await channel.QueuePurgeAsync(RabbitMqFixture.Queue, cancellationToken: Cancellation);
        await channel.QueuePurgeAsync(RabbitMqFixture.FailuresQueue, cancellationToken: Cancellation);
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<identityapi::Ouroboros.Identity.Api.IdentityApiEntryPoint> CreateIdentityApi(string identityRuntimeCs) =>
        new IntegrationApiFactory(identityRuntimeCs)
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Identity"] = identityRuntimeCs,
                })));

    private static HttpClient CreateAdminClient(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<identityapi::Ouroboros.Identity.Api.IdentityApiEntryPoint> api)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Add(IntegrationApiFactory.ListenerHeader, IntegrationApiFactory.AdminListener);
        return client;
    }

    private EmailOutboxPublisher CreateOutboxPublisher(IServiceProvider services, EmailOutboxMetrics metrics) =>
        new(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new EmailPublisherOptions
            {
                Host = broker.Host,
                Port = broker.Port,
                Password = broker.IdentityPassword,
                BatchSize = 4,
            }),
            new TestHostEnvironment(),
            TimeProvider.System,
            metrics,
            NullLogger<EmailOutboxPublisher>.Instance);

    private static async Task CreateAdministratorAsync(string identityRuntimeCs, string email, string password)
    {
        await using var api = CreateIdentityApi(identityRuntimeCs);
        await using var scope = api.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await context.Sessions.ExecuteDeleteAsync(Cancellation);
        await context.Users.ExecuteDeleteAsync(Cancellation);
        var createAdmin = scope.ServiceProvider.GetRequiredService<ICreateFirstAdmin>();
        var created = await createAdmin.ExecuteAsync(new CreateFirstAdminRequest(email, password), Cancellation);
        Assert.True(created.Succeeded);
    }

    private static async Task PublishWorkerReleaseAsync(string version, string revision, string outputDirectory)
    {
        var projectPath = RepositoryRoot.Resolve("src", "Services", "Notifications", "Ouroboros.Notifications.Worker", "Ouroboros.Notifications.Worker.csproj");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepositoryRoot.Resolve("."),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("publish");
        start.ArgumentList.Add(projectPath);
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("Debug");
        start.ArgumentList.Add($"-p:Version={version}");
        start.ArgumentList.Add($"-p:SourceRevisionId={revision}");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add(outputDirectory);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet publish não iniciou.");
        var stdout = process.StandardOutput.ReadToEndAsync(Cancellation);
        var stderr = process.StandardError.ReadToEndAsync(Cancellation);
        await process.WaitForExitAsync(Cancellation);
        var text = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, $"Falha ao publicar release de teste do worker:\n{text}");
    }

    private Process StartDefaultWorker(NotificationTestKeys keys, string connectionString, int healthPort, bool useSharedIdentityKeyRing)
    {
        var configuration = typeof(ServiceUpdateAndRecoveryTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .Cast<System.Reflection.AssemblyConfigurationAttribute>()
            .Single().Configuration;
        var dllPath = RepositoryRoot.Resolve("src", "Services", "Notifications", "Ouroboros.Notifications.Worker", "bin", configuration, "net10.0", "Ouroboros.Notifications.Worker.dll");
        return StartWorkerFromDll(dllPath, keys, connectionString, healthPort, useSharedIdentityKeyRing);
    }

    private Process StartPublishedWorker(string publishedDirectory, NotificationTestKeys keys, string connectionString, int healthPort, bool useSharedIdentityKeyRing)
    {
        var dllPath = Path.Combine(publishedDirectory, "Ouroboros.Notifications.Worker.dll");
        return StartWorkerFromDll(dllPath, keys, connectionString, healthPort, useSharedIdentityKeyRing);
    }

    private Process StartWorkerFromDll(string dllPath, NotificationTestKeys keys, string connectionString, int healthPort, bool useSharedIdentityKeyRing)
    {
        var combinedRingPath = keys.RingPath;
        if (useSharedIdentityKeyRing)
        {
            combinedRingPath = WriteCombinedPrivateKeyRing(keys);
        }

        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = keys.DirectoryPath,
        };
        start.ArgumentList.Add(dllPath);
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["Notifications__Health__Urls__0"] = $"http://127.0.0.1:{healthPort}";
        start.Environment["Notifications__Consumer__Enabled"] = "true";
        start.Environment["Notifications__Email__Provider"] = "PickupDirectory";
        start.Environment["Notifications__Email__PickupDirectory"] = keys.EmailsPath;
        start.Environment["Notifications__PrivateKeyRingPath"] = combinedRingPath;
        start.Environment["Notifications__FingerprintKeyRingPath"] = keys.FingerprintRingPath;
        start.Environment["ConnectionStrings__Notifications"] = connectionString;
        start.Environment["Notifications__Broker__Host"] = broker.Host;
        start.Environment["Notifications__Broker__Port"] = broker.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["Notifications__Broker__Password"] = broker.ConsumerPassword;
        start.Environment["Notifications__FailuresBroker__Host"] = broker.Host;
        start.Environment["Notifications__FailuresBroker__Port"] = broker.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["Notifications__FailuresBroker__Password"] = broker.FailuresPassword;
        return Process.Start(start) ?? throw new InvalidOperationException("Worker não iniciou.");
    }

    private static string WriteCombinedPrivateKeyRing(NotificationTestKeys keys)
    {
        using var testRing = JsonDocument.Parse(File.ReadAllText(TestSigningKeys.EmailPrivateKeyRingPath));
        using var localRing = JsonDocument.Parse(File.ReadAllText(keys.RingPath));
        var mergedKeys = testRing.RootElement.GetProperty("keys").EnumerateArray()
            .Concat(localRing.RootElement.GetProperty("keys").EnumerateArray())
            .Select(entry => new
            {
                keyId = entry.GetProperty("keyId").GetString(),
                privateKeyPath = entry.GetProperty("privateKeyPath").GetString(),
            })
            .ToArray();
        var combinedPath = Path.Combine(keys.DirectoryPath, "combined-private.json");
        File.WriteAllText(combinedPath, JsonSerializer.Serialize(new { keys = mergedKeys }));
        return combinedPath;
    }

    private IHost BuildNotificationsHostWithTransportSpy(NotificationTestKeys keys, string connectionString, List<Guid> sentMessageIds)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = keys.DirectoryPath,
            ApplicationName = typeof(ServiceUpdateAndRecoveryTests).Assembly.GetName().Name,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Notifications"] = connectionString,
            ["Notifications:Consumer:Enabled"] = "false",
            ["Notifications:Email:Provider"] = "PickupDirectory",
            ["Notifications:Email:PickupDirectory"] = keys.EmailsPath,
            ["Notifications:Email:From"] = "no-reply@ouroboros.local",
            ["Notifications:PrivateKeyRingPath"] = WriteCombinedPrivateKeyRing(keys),
            ["Notifications:FingerprintKeyRingPath"] = keys.FingerprintRingPath,
            ["Notifications:Broker:Host"] = broker.Host,
            ["Notifications:Broker:Port"] = broker.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Notifications:Broker:User"] = "notifications_consumer",
            ["Notifications:Broker:Password"] = broker.ConsumerPassword,
            ["Notifications:FailuresBroker:Host"] = broker.Host,
            ["Notifications:FailuresBroker:Port"] = broker.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Notifications:FailuresBroker:User"] = "notifications_failures",
            ["Notifications:FailuresBroker:Password"] = broker.FailuresPassword,
        });
        builder.Services.AddNotificationsService();
        builder.Services.AddSingleton<PickupEmailTransport>();
        builder.Services.AddSingleton<IEmailTransport>(sp => new SpyEmailTransport(sp.GetRequiredService<PickupEmailTransport>(), sentMessageIds));
        return builder.Build();
    }

    private async Task PublishRawAsync(byte[] body, Guid messageId)
    {
        await using var connection = await broker.ConnectAsync("identity_publisher", broker.IdentityPassword);
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), Cancellation);
        await channel.BasicPublishAsync(
            RabbitMqFixture.Exchange,
            "email.requested.v1",
            mandatory: true,
            new BasicProperties
            {
                Persistent = true,
                MessageId = messageId.ToString("D"),
                ContentType = "application/json",
            },
            body,
            Cancellation);
    }

    private static byte[] AddOptionalJsonField(byte[] envelopeJson, string propertyName, string propertyValue)
    {
        using var document = JsonDocument.Parse(envelopeJson);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WriteString(propertyName, propertyValue);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static async Task WaitForDeliveredEmailCountAsync(string emailsPath, int expectedCount)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            if (Directory.Exists(emailsPath) && Directory.GetFiles(emailsPath, "*.eml").Length >= expectedCount)
            {
                return;
            }

            await Task.Delay(100, timeout.Token);
        }
    }

    private static async Task WaitForHttpStatusAsync(HttpClient client, string path, HttpStatusCode expected)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            try
            {
                using var response = await client.GetAsync(path, timeout.Token);
                if (response.StatusCode == expected)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(150, timeout.Token);
        }
    }

    private static async Task StopProcessAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync(Cancellation);
    }

    private static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class SpyEmailTransport(PickupEmailTransport inner, List<Guid> sentMessageIds) : IEmailTransport
    {
        public async Task SendAsync(Guid messageId, EmailContent content, CancellationToken cancellationToken)
        {
            sentMessageIds.Add(messageId);
            await inner.SendAsync(messageId, content, cancellationToken);
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "RecoveryTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
