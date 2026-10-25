extern alias identityapi;

using System.Net;
using System.Net.Http.Headers;
using identityapi::Ouroboros.Identity.Api.Listeners;
using identityapi::Ouroboros.Identity.Api.Routing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Ouroboros.ArchitectureTests.Dependencies;

namespace Ouroboros.ArchitectureTests;

// A separação entre os públicos não é mais ausência de código (dois executáveis), e sim configuração: listener,
// audiência e política. Estes testes verificam a convenção de rota que a sustenta e que uma rota administrativa
// chamada no listener público (ou uma externa no administrativo) é indistinguível de uma rota inexistente.
public sealed class HostSeparationTests(OuroborosApiFactory external)
    : IClassFixture<OuroborosApiFactory>
{
    private const string NonexistentAdminRoute = "/api/v1/admin/does-not-exist";

    private const string NonexistentExternalRoute = "/api/v1/does-not-exist";

    private static readonly string[] Methods = ["GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS", "HEAD"];

    public static TheoryData<string, string> AdminEndpoints => Endpoints(admin: true);

    public static TheoryData<string, string> PublicEndpoints => Endpoints(admin: false);

    [Fact]
    public void Every_admin_prefixed_endpoint_is_admin_only_and_requires_the_admin_role()
    {
        var admin = RouteEndpoints(external.Services)
            .Where(endpoint => ListenerRouteConvention.IsAdminRoute(endpoint.RoutePattern.RawText))
            .ToList();

        var violations = admin
            .Where(endpoint => endpoint.Metadata.GetMetadata<ListenerMetadata>() is not { Role: ListenerRole.Admin }
                || !endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(data => data.Roles == ListenerRouteConvention.AdminRole))
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToList();

        Assert.NotEmpty(admin);
        Assert.Empty(violations);
    }

    [Fact]
    public void Every_other_endpoint_is_public_only()
    {
        var violations = RouteEndpoints(external.Services)
            .Where(endpoint => !ListenerRouteConvention.IsAdminRoute(endpoint.RoutePattern.RawText))
            .Where(endpoint => endpoint.Metadata.GetMetadata<ListenerMetadata>() is not { Role: ListenerRole.Public })
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToList();

        Assert.Empty(violations);
    }

    // Todo controller administrativo da API está sob o prefixo administrativo, e nenhum externo está.
    [Fact]
    public void Controllers_follow_the_admin_and_external_route_prefixes()
    {
        var violations = external.Services.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Where(action => action.ControllerTypeInfo.Assembly == ApiAssembly)
            .Select(action =>
            {
                var admin = ListenerRouteConvention.IsAdminRoute(action.AttributeRouteInfo?.Template);
                return (IsIn(action, "Admin"), IsIn(action, "External"), admin) switch
                {
                    (true, _, true) or (_, true, false) => null,
                    _ => $"{action.ControllerTypeInfo.FullName}.{action.ActionName}: rota '{action.AttributeRouteInfo?.Template}' fora do prefixo do seu público.",
                };
            })
            .OfType<string>()
            .ToList();

        Assert.Empty(violations);
    }

    // A descoberta padrão ignora controller internal sem avisar: na API, todo controller é público.
    [Fact]
    public void Api_controllers_are_public()
    {
        var hidden = ApiAssembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract && !type.IsPublic)
            .Select(type => type.FullName);

        Assert.Empty(hidden);
    }

    // As bibliotecas dos serviços não publicam controllers, então não aparecem como application part da API.
    [Fact]
    public void Service_libraries_are_not_application_parts()
    {
        var libraries = SourceTree.Current.Services.Select(project => project.Name).ToHashSet(StringComparer.Ordinal);

        var parts = external.Services.GetRequiredService<ApplicationPartManager>().ApplicationParts
            .Select(part => part.Name)
            .Where(libraries.Contains);

        Assert.Empty(parts);
    }

    // As bibliotecas dos serviços expõem só casos de uso (Contracts); os controllers ficam na API do serviço.
    [Fact]
    public void Service_libraries_do_not_declare_controllers()
    {
        var libraries = SourceTree.Current.Services.Select(project => project.Name).ToList();

        Assert.NotEmpty(libraries);
        Assert.All(libraries, name => Assert.DoesNotContain(
            SourceTree.Current.Load(name).GetTypes(),
            type => typeof(ControllerBase).IsAssignableFrom(type)));
    }

    [Theory]
    [MemberData(nameof(AdminEndpoints))]
    public async Task Admin_route_on_the_public_listener_looks_like_a_nonexistent_route(string method, string path)
    {
        using var client = external.CreateClient();
        var token = TestTokens.Create(TestTokens.ExternalAudience, "Customer");

        foreach (var bearer in new string?[] { null, token })
        {
            var actual = await SendAsync(client, method, path, bearer);
            var nonexistent = await SendAsync(client, method, NonexistentAdminRoute, bearer);

            Assert.Equal(nonexistent, actual);
        }
    }

    [Theory]
    [MemberData(nameof(PublicEndpoints))]
    public async Task External_route_on_the_admin_listener_looks_like_a_nonexistent_route(string method, string path)
    {
        using var client = external.CreateClient();
        client.DefaultRequestHeaders.Add(OuroborosApiFactory.ListenerHeader, OuroborosApiFactory.AdminListener);
        var token = TestTokens.Create(TestTokens.BackofficeAudience, "Admin");

        foreach (var bearer in new string?[] { null, token })
        {
            var actual = await SendAsync(client, method, path, bearer);
            var nonexistent = await SendAsync(client, method, NonexistentExternalRoute, bearer);

            Assert.Equal(nonexistent, actual);
        }
    }

    private static System.Reflection.Assembly ApiAssembly => typeof(identityapi::Ouroboros.Identity.Api.IdentityApiEntryPoint).Assembly;

    // Rotas concretas (sem parâmetros de rota além da versão) de cada público, descobertas do próprio host.
    private static TheoryData<string, string> Endpoints(bool admin)
    {
        using var factory = new OuroborosApiFactory();
        var data = new TheoryData<string, string>();
        var seen = new HashSet<(string, string)>();
        foreach (var endpoint in RouteEndpoints(factory.Services).Where(endpoint => ListenerRouteConvention.IsAdminRoute(endpoint.RoutePattern.RawText) == admin))
        {
            var path = endpoint.RoutePattern.RawText!.Replace("{version:apiVersion}", "1", StringComparison.Ordinal);
            if (path.Contains('{', StringComparison.Ordinal))
            {
                continue;
            }

            // Todos os métodos, não só o declarado: um método não suportado não pode revelar a rota com 405.
            foreach (var method in Methods)
            {
                if (seen.Add((method, path)))
                {
                    data.Add(method, "/" + path.TrimStart('/'));
                }
            }
        }

        return data;
    }

    private static IEnumerable<RouteEndpoint> RouteEndpoints(IServiceProvider services) => services
        .GetRequiredService<EndpointDataSource>().Endpoints
        .OfType<RouteEndpoint>()
        .Where(endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo.Assembly == ApiAssembly);

    private static bool IsIn(ControllerActionDescriptor action, string area) =>
        action.ControllerTypeInfo.Namespace?.EndsWith($".Controllers.{area}", StringComparison.Ordinal) == true;

    // Status, desafio de autenticação e tipo de conteúdo: o que um cliente observa de uma rota que não existe.
    private static async Task<(HttpStatusCode Status, string Challenge, string? ContentType)> SendAsync(
        HttpClient client, string method, string path, string? token)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return (
            response.StatusCode,
            string.Join(",", response.Headers.WwwAuthenticate.Select(header => header.ToString())),
            response.Content.Headers.ContentType?.MediaType);
    }
}
