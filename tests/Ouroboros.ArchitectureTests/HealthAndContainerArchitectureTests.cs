using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Ouroboros.ArchitectureTests.Dependencies;
using Ouroboros.Hosting;

namespace Ouroboros.ArchitectureTests;

public sealed class HealthAndContainerArchitectureTests(OuroborosApiFactory factory)
    : IClassFixture<OuroborosApiFactory>
{
    private static readonly string Root = SourceProjects.Current.Root;
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Liveness_endpoint_is_anonymous_and_returns_healthy_on_both_listeners(bool adminListener)
    {
        using var client = factory.CreateClient();
        if (adminListener)
        {
            client.DefaultRequestHeaders.Add(OuroborosApiFactory.ListenerHeader, OuroborosApiFactory.AdminListener);
        }

        using var response = await client.GetAsync(OuroborosHealthExtensions.LivePath, Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"status\":\"Healthy\"}", await response.Content.ReadAsStringAsync(Cancellation));
    }

    [Theory]
    [InlineData(OuroborosHealthExtensions.ReadyPath)]
    [InlineData(OuroborosHealthExtensions.HealthPath)]
    public async Task Readiness_endpoint_never_exposes_secrets_connection_strings_or_exception_messages(string path)
    {
        const string SecretPassword = "S3nh@SuperSecretaHealthCheck98!";
        const string SecretConnection = "Host=internal-db.local;Username=identity_runtime;Password=" + SecretPassword;
        const string InternalMessage = "NpgsqlException: password authentication failed for user identity_runtime";

        await using var custom = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddHealthChecks()
                    .AddCheck(
                        "leaking-dependency",
                        new ThrowingLeakingHealthCheck(SecretConnection, InternalMessage),
                        tags: ["ready"])));

        using var client = custom.CreateClient();
        using var response = await client.GetAsync(path, Cancellation);
        var body = await response.Content.ReadAsStringAsync(Cancellation);
        var headers = string.Join('\n', response.Headers.Select(header => $"{header.Key}:{string.Join(',', header.Value)}"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"status\":\"Unhealthy\"}", body);
        foreach (var secret in new[] { SecretPassword, SecretConnection, InternalMessage, "leaking-dependency", "identity_runtime" })
        {
            Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, headers, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Service_dockerfiles_use_non_root_user_version_commit_args_and_isolated_build_contexts()
    {
        var identityDockerfile = File.ReadAllText(Path.Combine(Root, "src", "Services", "Identity", "Ouroboros.Identity.Api", "Dockerfile"));
        var notificationsDockerfile = File.ReadAllText(Path.Combine(Root, "src", "Services", "Notifications", "Ouroboros.Notifications.Worker", "Dockerfile"));

        foreach (var dockerfile in new[] { identityDockerfile, notificationsDockerfile })
        {
            Assert.Contains("ARG APP_VERSION=", dockerfile, StringComparison.Ordinal);
            Assert.Contains("ARG SOURCE_REVISION=", dockerfile, StringComparison.Ordinal);
            Assert.Contains("/p:Version=${APP_VERSION}", dockerfile, StringComparison.Ordinal);
            Assert.Contains("/p:SourceRevisionId=${SOURCE_REVISION}", dockerfile, StringComparison.Ordinal);
            Assert.Contains("org.opencontainers.image.version=\"${APP_VERSION}-${SOURCE_REVISION}\"", dockerfile, StringComparison.Ordinal);
            Assert.Contains("USER $APP_UID", dockerfile, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("src/Services/Notifications", identityDockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("src/Services/Identity", notificationsDockerfile, StringComparison.Ordinal);

        foreach (var ignorePath in new[]
        {
            Path.Combine(Root, "src", "Services", "Identity", "Ouroboros.Identity.Api", "Dockerfile.dockerignore"),
            Path.Combine(Root, "src", "Services", "Notifications", "Ouroboros.Notifications.Worker", "Dockerfile.dockerignore"),
        })
        {
            var entries = File.ReadAllLines(ignorePath).Select(line => line.Trim()).ToHashSet(StringComparer.Ordinal);
            Assert.Contains(".env", entries);
            Assert.Contains("**/*.env", entries);
            Assert.Contains("**/*.pem", entries);
            Assert.Contains("**/*.key", entries);
            Assert.Contains(".local", entries);
            Assert.Contains("backups", entries);
        }
    }

    [Fact]
    public void Compose_keeps_admin_listener_unexposed_migrations_explicit_and_credentials_isolated()
    {
        var compose = File.ReadAllText(Path.Combine(Root, "compose.yaml"));

        Assert.Contains("identity-api:", compose, StringComparison.Ordinal);
        Assert.Contains("notifications-worker:", compose, StringComparison.Ordinal);
        Assert.Contains("migrate-identity:", compose, StringComparison.Ordinal);
        Assert.Contains("migrate-notifications:", compose, StringComparison.Ordinal);
        Assert.Contains("profiles: [\"migrate\"]", compose, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:${IDENTITY_PUBLIC_PORT:-5141}:5141", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("5142:5142", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("5142}:5142", compose, StringComparison.Ordinal);
        Assert.Contains("Identity__Listeners__Admin__Urls__0: \"http://127.0.0.1:5142\"", compose, StringComparison.Ordinal);
        Assert.Contains("Identity__Listeners__Admin__AllowNonLoopback: \"false\"", compose, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:5141/health/ready", compose, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:5143/health/ready", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void Ci_workflow_builds_both_service_images_with_version_and_commit_tag()
    {
        var workflow = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "ci.yml"));

        Assert.Contains("src/Services/Identity/Ouroboros.Identity.Api/Dockerfile", workflow, StringComparison.Ordinal);
        Assert.Contains("src/Services/Notifications/Ouroboros.Notifications.Worker/Dockerfile", workflow, StringComparison.Ordinal);
        Assert.Contains("--build-arg APP_VERSION=", workflow, StringComparison.Ordinal);
        Assert.Contains("--build-arg SOURCE_REVISION=", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void No_documented_command_or_script_removes_docker_volumes()
    {
        var files = Directory.EnumerateFiles(Path.Combine(Root, "docs"), "*.md", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "scripts"), "*", SearchOption.AllDirectories))
            .Append(Path.Combine(Root, "compose.yaml"));

        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            Assert.DoesNotContain("docker compose down -v", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("docker compose down --volumes", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("docker volume rm", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("docker volume prune", content, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class ThrowingLeakingHealthCheck(string connectionString, string internalMessage) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException($"{internalMessage} ({connectionString})");
    }
}
