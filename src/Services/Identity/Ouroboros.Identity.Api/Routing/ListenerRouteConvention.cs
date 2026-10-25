using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Ouroboros.Identity.Api.Listeners;

namespace Ouroboros.Identity.Api.Routing;

// O prefixo da rota decide o público do endpoint, e não a lembrança de cada controller:
//  - api/v{version}/admin/...: existe só no listener administrativo e exige autenticação e papel Admin, mesmo
//    sem política declarada (um [AllowAnonymous] explícito, como no login, continua valendo);
//  - qualquer outra rota: existe só no listener público.
// Os tokens [controller] e [action] são resolvidos e todas as rotas do controller são avaliadas: se qualquer uma
// for administrativa, o endpoint é administrativo (na dúvida, nunca público).
public sealed partial class ListenerRouteConvention : IApplicationModelConvention
{
    public const string AdminRole = "Admin";

    public static bool IsAdminRoute(string? template) =>
        template is not null && AdminRoute().IsMatch(template.TrimStart('~', '/'));

    public void Apply(ApplicationModel application)
    {
        foreach (var controller in application.Controllers)
        {
            foreach (var action in controller.Actions)
            {
                foreach (var selector in action.Selectors)
                {
                    if (Templates(controller, action, selector).Any(IsAdminRoute))
                    {
                        selector.EndpointMetadata.Add(new ListenerMetadata(ListenerRole.Admin));
                        selector.EndpointMetadata.Add(new AuthorizeAttribute { Roles = AdminRole });
                    }
                    else
                    {
                        selector.EndpointMetadata.Add(new ListenerMetadata(ListenerRole.Public));
                    }
                }
            }
        }
    }

    private static IEnumerable<string> Templates(ControllerModel controller, ActionModel action, SelectorModel selector)
    {
        var values = new Dictionary<string, string?>
        {
            ["controller"] = controller.ControllerName,
            ["action"] = action.ActionName,
        };

        var controllerRoutes = controller.Selectors
            .Select(controllerSelector => controllerSelector.AttributeRouteModel)
            .DefaultIfEmpty(null);
        foreach (var controllerRoute in controllerRoutes)
        {
            var combined = AttributeRouteModel.CombineAttributeRouteModel(controllerRoute, selector.AttributeRouteModel);
            if (combined?.Template is { } template)
            {
                yield return AttributeRouteModel.ReplaceTokens(template, values, null) ?? template;
            }
        }
    }

    [GeneratedRegex(@"^api/v[^/]+/admin(/|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AdminRoute();
}
