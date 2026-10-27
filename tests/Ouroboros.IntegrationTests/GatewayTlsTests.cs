extern alias admingateway;
extern alias externalgateway;

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using admingateway::Ouroboros.AdminGateway.Api;
using externalgateway::Ouroboros.ExternalGateway.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Ouroboros.IntegrationTests;

public sealed class GatewayTlsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Https_listener_negotiates_tls_with_the_configured_certificate(bool admin)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        // Schannel requer uma chave importada para autenticar o servidor no Windows.
        using var certificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);

        var project = admin ? "Ouroboros.AdminGateway.Api" : "Ouroboros.ExternalGateway.Api";
        string[] args = ["--environment", "Testing", "--contentRoot", RepositoryRoot.Resolve("src", "Gateways", project)];
        void Configure(WebApplicationBuilder builder)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Urls:0"] = "https://127.0.0.1:0",
                ["Gateway:AllowNonLoopback"] = "false",
                ["Gateway:Downstream:IdentityBaseUrl"] = "http://127.0.0.1:1",
            });
            builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(https => https.ServerCertificate = certificate));
        }

        await using var app = admin ? AdminGatewayHost.Build(args, Configure) : ExternalGatewayHost.Build(args, Configure);
        if (admin)
        {
            AdminGatewayHost.UsePipeline(app);
        }
        else
        {
            ExternalGatewayHost.UsePipeline(app);
        }

        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var handler = new HttpClientHandler
            {
                UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, presented, _, _) => presented?.Thumbprint == certificate.Thumbprint,
            };
            using var client = new HttpClient(handler) { BaseAddress = new Uri(Assert.Single(app.Urls)), Timeout = TimeSpan.FromSeconds(5) };
            Assert.Equal("https", client.BaseAddress.Scheme);
            using var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("{\"status\":\"Healthy\"}", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
