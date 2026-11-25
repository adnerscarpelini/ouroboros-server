extern alias webclient;

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ouroboros.ArchitectureTests.Dependencies;
using webclient::Ouroboros.WebClient;

namespace Ouroboros.ArchitectureTests;

// Fronteiras do web client (docs/architecture/web-client.md) no código real: projeto em src/Clients que só referencia a
// observabilidade, pastas classificadas pelas regras de área, subida recusada sem gateway válido ou com connection
// strings, health sanitizado refletindo o gateway e artefatos de implantação.
public sealed class WebClientArchitectureTests
{
    private const string WebClientAssembly = "Ouroboros.WebClient";
    private const string GatewayBaseUrlKey = "WebClient:Gateway:BaseUrl";
    private static readonly string Root = SourceProjects.Current.Root;
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void Web_client_is_a_client_outside_services_that_references_only_observability()
    {
        var project = Assert.Single(SourceProjects.Current.Projects, project => project.Name == WebClientAssembly);

        Assert.Equal(ProjectKind.Client, project.Kind);
        Assert.Null(project.Service);
        Assert.Equal(["Ouroboros.Observability"], project.References);
        Assert.DoesNotContain(SourceProjects.Current.Projects, other => other.References.Contains(WebClientAssembly));
    }

    // As regras de pasta só valem se o código real for classificado: cada área existente tem tipos localizados nela.
    [Theory]
    [InlineData("Shell")]
    [InlineData("Infrastructure")]
    [InlineData("Components")]
    [InlineData("Features.Identity")]
    public void Web_client_folders_are_classified_as_areas(string area)
    {
        var tree = SourceTree.Current;
        var scan = tree.Scans.Single(scan => scan.Name == WebClientAssembly);

        Assert.Contains(scan.Types, type => tree.Locate(type.Id).ClientArea == area);
    }

    // A área vem do namespace; por isso o namespace de cada arquivo C# precisa ser o da pasta, e os componentes Razor
    // não podem redefini-lo com @namespace. Program.cs (top-level) é o único arquivo sem namespace.
    [Fact]
    public void Web_client_namespaces_match_their_folders()
    {
        var projectDirectory = Path.Combine(Root, "src", "Clients", WebClientAssembly);
        var sources = Directory.EnumerateFiles(projectDirectory, "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".razor", StringComparison.Ordinal))
            .Where(file => !IsBuildOutput(Path.GetRelativePath(projectDirectory, file)))
            .ToList();
        Assert.NotEmpty(sources);

        var mismatches = new List<string>();
        foreach (var file in sources)
        {
            var relative = Path.GetRelativePath(projectDirectory, file).Replace('\\', '/');
            var content = File.ReadAllText(file);
            if (file.EndsWith(".razor", StringComparison.Ordinal))
            {
                if (content.Contains("@namespace", StringComparison.Ordinal))
                {
                    mismatches.Add($"{relative}: @namespace redefine a área do componente");
                }

                continue;
            }

            var declared = System.Text.RegularExpressions.Regex.Matches(content, @"(?m)^\s*namespace\s+([\w.]+)").Select(match => match.Groups[1].Value).ToList();
            var folder = Path.GetDirectoryName(relative)!.Replace('/', '.').Replace('\\', '.');
            var expected = folder.Length == 0 ? WebClientAssembly : $"{WebClientAssembly}.{folder}";
            if (relative == "Program.cs" ? declared.Count != 0 : declared.Count != 1 || declared[0] != expected)
            {
                mismatches.Add($"{relative}: namespace {string.Join(", ", declared)}; esperado {(relative == "Program.cs" ? "nenhum" : expected)}");
            }
        }

        Assert.Empty(mismatches);
    }

    private static bool IsBuildOutput(string relativePath) =>
        relativePath.Split('/', '\\')[0] is "bin" or "obj";

    [Theory]
    [InlineData("http://gateway.local:5151/api")]
    [InlineData("http://user:secret@gateway.local:5151")]
    [InlineData("http://gateway.local:5151/?x=1")]
    [InlineData("ftp://gateway.local")]
    [InlineData("gateway.local:5151")]
    [InlineData("")]
    public void Startup_fails_without_a_valid_gateway_origin(string baseUrl)
    {
        using var factory = CreateFactory(new() { [GatewayBaseUrlKey] = baseUrl });

        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("WebClient:Gateway:BaseUrl", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_fails_when_connection_strings_are_configured()
    {
        using var factory = CreateFactory(new() { ["ConnectionStrings:Identity"] = "Host=localhost;Database=identity" });

        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("ConnectionStrings", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Home_page_renders_with_the_shell()
    {
        using var factory = CreateFactory(gateway: new StubGateway(HttpStatusCode.OK));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/", Cancellation);
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<html lang=\"pt-BR\">", body, StringComparison.Ordinal);
        Assert.Contains("Ouroboros", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Liveness_does_not_depend_on_the_gateway()
    {
        using var factory = CreateFactory(gateway: new StubGateway(HttpStatusCode.ServiceUnavailable));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live", Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"status\":\"Healthy\"}", await response.Content.ReadAsStringAsync(Cancellation));
    }

    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/health")]
    public async Task Readiness_reflects_the_gateway_readiness(string path)
    {
        var gateway = new StubGateway(HttpStatusCode.OK);
        using var factory = CreateFactory(gateway: gateway);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"status\":\"Healthy\"}", await response.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal(new Uri("http://gateway.test:5151/health/ready"), Assert.Single(gateway.Requests));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(null)]
    public async Task Readiness_is_unhealthy_and_sanitized_when_the_gateway_is_not_ready(HttpStatusCode? gatewayStatus)
    {
        using var factory = CreateFactory(gateway: new StubGateway(gatewayStatus));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready", Cancellation);
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"status\":\"Unhealthy\"}", body);
        Assert.DoesNotContain("gateway.test", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Dockerfile_is_versioned_non_root_and_copies_only_the_client_and_observability()
    {
        var dockerfile = File.ReadAllText(Path.Combine(Root, "src", "Clients", "Ouroboros.WebClient", "Dockerfile"));

        Assert.Contains("ARG APP_VERSION=", dockerfile, StringComparison.Ordinal);
        Assert.Contains("ARG SOURCE_REVISION=", dockerfile, StringComparison.Ordinal);
        Assert.Contains("/p:Version=${APP_VERSION}", dockerfile, StringComparison.Ordinal);
        Assert.Contains("/p:SourceRevisionId=${SOURCE_REVISION}", dockerfile, StringComparison.Ordinal);
        Assert.Contains("org.opencontainers.image.version=\"${APP_VERSION}-${SOURCE_REVISION}\"", dockerfile, StringComparison.Ordinal);
        Assert.Contains("USER $APP_UID", dockerfile, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "src/Services", "src/Gateways", "Ouroboros.BuildingBlocks", "Ouroboros.Messaging", "Ouroboros.Hosting" })
        {
            Assert.DoesNotContain(forbidden, dockerfile, StringComparison.Ordinal);
        }

        var ignore = File.ReadAllLines(Path.Combine(Root, "src", "Clients", "Ouroboros.WebClient", "Dockerfile.dockerignore"))
            .Select(line => line.Trim()).ToHashSet(StringComparer.Ordinal);
        Assert.Superset(new HashSet<string>([".env", "**/*.env", "**/*.pem", "**/*.key", ".local", "backups"], StringComparer.Ordinal), ignore);
    }

    [Fact]
    public void Compose_runs_the_web_client_against_the_external_gateway_without_databases_or_broker()
    {
        var compose = File.ReadAllText(Path.Combine(Root, "compose.yaml"));
        var service = ComposeService(compose, "web-client");

        Assert.Contains("profiles: [\"webclient\"]", service, StringComparison.Ordinal);
        Assert.Contains("WebClient__Gateway__BaseUrl: \"http://external-gateway:5151\"", service, StringComparison.Ordinal);
        Assert.Contains("web-client-keys:/var/lib/ouroboros-web-client/keys", service, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:${WEB_CLIENT_PORT:-5161}:5161", service, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:5161/health/ready", service, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "ConnectionStrings", "postgres", "rabbitmq", "identity-api", "admin-gateway" })
        {
            Assert.DoesNotContain(forbidden, service, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Ci_workflow_builds_the_web_client_image()
    {
        var workflow = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "ci.yml"));

        Assert.Contains("src/Clients/Ouroboros.WebClient/Dockerfile", workflow, StringComparison.Ordinal);
    }

    // Trecho do serviço no compose.yaml: da chave "  <nome>:" até o próximo serviço ou seção de topo.
    private static string ComposeService(string compose, string name)
    {
        var lines = compose.Split('\n');
        var start = Array.FindIndex(lines, line => line.TrimEnd('\r') == $"  {name}:");
        Assert.True(start >= 0, $"Serviço {name} ausente do compose.yaml.");
        var end = Array.FindIndex(lines, start + 1, line =>
            (line.StartsWith("  ", StringComparison.Ordinal) && !line.StartsWith("   ", StringComparison.Ordinal) && line.TrimEnd('\r').EndsWith(':') && !line.TrimStart().StartsWith('#'))
            || (line.Length > 0 && !char.IsWhiteSpace(line[0])));
        return string.Join('\n', lines[start..(end < 0 ? lines.Length : end)]);
    }

    private static WebApplicationFactory<WebClientEntryPoint> CreateFactory(
        Dictionary<string, string?>? settings = null, StubGateway? gateway = null)
    {
        var configuration = new Dictionary<string, string?> { [GatewayBaseUrlKey] = "http://gateway.test:5151" };
        foreach (var (key, value) in settings ?? [])
        {
            configuration[key] = value;
        }

        return new WebApplicationFactory<WebClientEntryPoint>().WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in configuration)
            {
                builder.UseSetting(key, value);
            }

            if (gateway is not null)
            {
                builder.ConfigureTestServices(services =>
                    services.AddHttpClient("gateway").ConfigurePrimaryHttpMessageHandler(() => gateway));
            }
        });
    }

    // Gateway controlado: responde o status configurado ou, sem status, falha como conexão recusada.
    private sealed class StubGateway(HttpStatusCode? status) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request.RequestUri!);
            }

            return status is { } code
                ? Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent("""{"status":"x"}""") })
                : throw new HttpRequestException("Connection refused (gateway.test:5151)");
        }
    }
}
