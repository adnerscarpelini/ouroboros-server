extern alias backoffice;

using System.Reflection;
using Asp.Versioning;
using backoffice::Ouroboros.Backoffice.Api;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Ouroboros.ArchitectureTests.Dependencies;

namespace Ouroboros.ArchitectureTests;

// Convenção dos controllers de cada host, verificada sobre as actions que o host realmente registra:
// ficam em <Host>.Controllers.<Module>, o módulo existe, declaram [ApiVersion] e usam a rota do seu
// público: api/v{version:apiVersion}/<module>/ na API externa e
// api/v{version:apiVersion}/admin/<module>/ no backoffice.
public sealed class ControllerRouteTests(OuroborosApiFactory external, BackofficeApiFactory backoffice)
    : IClassFixture<OuroborosApiFactory>, IClassFixture<BackofficeApiFactory>
{
    private const string VersionSegment = "api/v{version:apiVersion}/";

    [Fact]
    public void External_host_controllers_follow_the_convention() =>
        AssertConvention(external.Services, typeof(Program).Assembly, routePrefix: "");

    [Fact]
    public void Backoffice_host_controllers_follow_the_convention() =>
        AssertConvention(backoffice.Services, typeof(BackofficeApiEntryPoint).Assembly, routePrefix: "admin/");

    private static void AssertConvention(IServiceProvider services, Assembly hostAssembly, string routePrefix)
    {
        var modules = SourceTree.Current.Services
            .Select(project => project.Service!)
            .ToHashSet(StringComparer.Ordinal);

        var violations = services.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Where(action => action.ControllerTypeInfo.Assembly == hostAssembly)
            .Select(action => FindViolation(action, hostAssembly, modules, routePrefix))
            .OfType<string>()
            .ToList();

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    private static string? FindViolation(
        ControllerActionDescriptor action,
        Assembly hostAssembly,
        HashSet<string> modules,
        string routePrefix)
    {
        var controllerType = action.ControllerTypeInfo;
        var actionName = $"{controllerType.FullName}.{action.ActionName}";
        var controllersNamespace = $"{hostAssembly.GetName().Name}.Controllers.";

        var module = controllerType.Namespace?.StartsWith(controllersNamespace, StringComparison.Ordinal) == true
            ? controllerType.Namespace[controllersNamespace.Length..]
            : null;
        if (module is null || module.Contains('.', StringComparison.Ordinal) || !modules.Contains(module))
        {
            return $"{actionName} não está em {controllersNamespace}<Module> de um módulo existente.";
        }

        if (!controllerType.IsDefined(typeof(ApiVersionAttribute), inherit: true))
        {
            return $"{actionName} não declara [ApiVersion].";
        }

        var template = action.AttributeRouteInfo?.Template?.TrimStart('~', '/');
        var expectedPrefix = $"{VersionSegment}{routePrefix}{module.ToLowerInvariant()}/";
        if (template is null || !template.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return $"{actionName} usa a rota '{template}', fora de {expectedPrefix}.";
        }

        return null;
    }
}
