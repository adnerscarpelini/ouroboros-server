using System.Diagnostics;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Ouroboros.Hosting;

// Mecanismos técnicos HTTP comuns às APIs dos serviços: controllers, versionamento por URL, erros em
// ProblemDetails, política de autorização padrão e ordem do pipeline. Não conhece serviços, bancos, papéis
// nem regras de negócio: cada API registra o próprio serviço e define suas políticas por endpoint.
public static class OuroborosHostExtensions
{
    public static WebApplicationBuilder AddOuroborosHost(this WebApplicationBuilder builder)
    {
        // Os controllers são públicos e ficam no projeto da API do serviço; a descoberta padrão do
        // ASP.NET Core encontra só os dessa API.
        builder.Services.AddControllers(options => options.Filters.Add<HttpFailureResultFilter>());
        builder.Services.AddExceptionHandler<SafeExceptionHandler>();

        // A versão maior vem sempre na URL (api/v{version:apiVersion}/...) e não há versão implícita.
        builder.Services.AddApiVersioning(options =>
        {
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
            options.ReportApiVersions = true;
        }).AddMvc();

        // Todo erro sai em application/problem+json (RFC 9457) com o trace id W3C da requisição, o
        // mesmo dos logs. Falhas de negócio definem o próprio type nos controllers da API.
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            context.ProblemDetails.Extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
            HttpFailureResultFilter.Capture(context.HttpContext, context.ProblemDetails);
        });

        // Nega por padrão: um endpoint só é anônimo se declarar [AllowAnonymous]. Exigências além de estar
        // autenticado (papéis, políticas) pertencem à API do serviço.
        builder.Services.AddAuthorization(options => options.FallbackPolicy =
            new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        builder.Services.AddOuroborosHealthChecks();

        return builder;
    }

    public static WebApplication UseOuroborosHost(this WebApplication app)
    {
        app.UseMiddleware<HttpFailureDiagnostics>();
        app.UseExceptionHandler(new ExceptionHandlerOptions { SuppressDiagnosticsCallback = _ => true });
        app.UseStatusCodePages();

        // Antes da autenticação, para recusar o excesso sem validar token. As políticas vêm do serviço
        // e são aplicadas por endpoint com [EnableRateLimiting].
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOuroborosHealthChecks();
        app.MapControllers();
        return app;
    }
}
