using System.Diagnostics;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Ouroboros.Hosting;

// Configuração HTTP comum aos dois hosts. Cada host continua registrando seus módulos no próprio
// Program.cs, então a composição de cada um fica visível ali.
public static class OuroborosHostExtensions
{
    // Papel fixo de administrador, o mesmo emitido no claim role dos tokens.
    public const string AdminRole = "Admin";

    public static WebApplicationBuilder AddOuroborosHost(this WebApplicationBuilder builder, HostAudience audience)
    {
        // Os controllers são públicos e ficam no próprio projeto de cada host, organizados por módulo;
        // a descoberta padrão do ASP.NET Core encontra só os do host.
        builder.Services.AddControllers();

        // A versão maior vem sempre na URL (api/v{version:apiVersion}/...) e não há versão implícita.
        builder.Services.AddApiVersioning(options =>
        {
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
            options.ReportApiVersions = true;
        }).AddMvc();

        // Todo erro sai em application/problem+json (RFC 9457) com o trace id W3C da requisição, o
        // mesmo dos logs. Falhas de negócio definem o próprio type nos controllers dos hosts.
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier);

        // Nega por padrão: um endpoint só é anônimo se declarar [AllowAnonymous]. No backoffice, além
        // de autenticado, o usuário precisa ter o papel Admin em todo endpoint, mesmo que o controller
        // não declare nenhuma política.
        builder.Services.AddAuthorization(options => options.FallbackPolicy = audience == HostAudience.Backoffice
            ? new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireRole(AdminRole).Build()
            : new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return builder;
    }

    public static WebApplication UseOuroborosHost(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();

        // Antes da autenticação, para recusar o excesso sem validar token. As políticas vêm dos módulos
        // e são aplicadas por endpoint com [EnableRateLimiting].
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        return app;
    }
}
