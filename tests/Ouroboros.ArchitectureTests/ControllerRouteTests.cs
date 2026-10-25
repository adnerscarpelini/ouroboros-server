extern alias identityapi;

using System.Reflection;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Ouroboros.ArchitectureTests.Dependencies;

namespace Ouroboros.ArchitectureTests;

// Convenção dos controllers de cada serviço, verificada sobre as actions que a API realmente registra: ficam em
// <Api>.Controllers.External (ou Admin).<Recurso>?, o serviço existe, declaram [ApiVersion] e usam a rota do seu
// público: api/v{version:apiVersion}/<service>/ no público externo e api/v{version:apiVersion}/admin/<service>/
// no administrativo.
public sealed class ControllerRouteTests(OuroborosApiFactory factory) : IClassFixture<OuroborosApiFactory>
{
    private const string VersionSegment = "api/v{version:apiVersion}/";

    [Fact]
    public void Controllers_of_the_identity_api_follow_the_convention()
    {
        var assembly = typeof(identityapi::Ouroboros.Identity.Api.IdentityApiEntryPoint).Assembly;
        var services = SourceTree.Current.Services
            .Select(project => project.Service!)
            .ToHashSet(StringComparer.Ordinal);

        var violations = factory.Services.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Where(action => action.ControllerTypeInfo.Assembly == assembly)
            .Select(action => FindViolation(action, assembly, services))
            .OfType<string>()
            .ToList();

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    private static string? FindViolation(ControllerActionDescriptor action, Assembly assembly, HashSet<string> services)
    {
        var controllerType = action.ControllerTypeInfo;
        var actionName = $"{controllerType.FullName}.{action.ActionName}";
        var rootNamespace = $"{assembly.GetName().Name}.Controllers.";

        var audience = controllerType.Namespace?.StartsWith(rootNamespace, StringComparison.Ordinal) == true
            ? controllerType.Namespace[rootNamespace.Length..]
            : null;
        var routePrefix = audience switch
        {
            "External" => "",
            "Admin" => "admin/",
            _ => null,
        };
        if (routePrefix is null)
        {
            return $"{actionName} não está em {rootNamespace}External nem em {rootNamespace}Admin.";
        }

        if (!controllerType.IsDefined(typeof(ApiVersionAttribute), inherit: true))
        {
            return $"{actionName} não declara [ApiVersion].";
        }

        var service = assembly.GetName().Name!.Split('.')[1];
        if (!services.Contains(service))
        {
            return $"{actionName} pertence a um serviço inexistente ({service}).";
        }

        var template = action.AttributeRouteInfo?.Template?.TrimStart('~', '/');
        var expectedPrefix = $"{VersionSegment}{routePrefix}{service.ToLowerInvariant()}/";
        if (template is null || !template.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return $"{actionName} usa a rota '{template}', fora de {expectedPrefix}.";
        }

        return null;
    }
}
