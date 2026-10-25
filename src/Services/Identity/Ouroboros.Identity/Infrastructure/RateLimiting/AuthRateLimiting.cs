using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ouroboros.Identity.Infrastructure.RateLimiting;

// Limite de requisições por IP dos endpoints anônimos de autenticação, com o rate limiter nativo do
// ASP.NET Core. Os contadores ficam em memória, por instância (não há proxy reverso nem réplicas).
internal static partial class AuthRateLimiting
{
    public static void Add(IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<AuthRateLimitOptions>, AuthRateLimitOptionsValidator>();
        services.AddOptions<AuthRateLimitOptions>().BindConfiguration(AuthRateLimitOptions.SectionName).ValidateOnStart();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                var http = context.HttpContext;
                var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AuthRateLimiting));
                LogRejected(logger, http.Request.Path, ClientAddress(http));

                // Retry-After (RFC 6585) com os segundos até o fim da janela, e corpo em ProblemDetails com traceId.
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = http,
                    ProblemDetails =
                    {
                        Type = "https://www.rfc-editor.org/rfc/rfc6585#section-4",
                        Title = "Muitas requisições. Tente novamente mais tarde.",
                        Status = StatusCodes.Status429TooManyRequests,
                    },
                });
            };
        });

        // Cada política configurada vira uma política nomeada, com o próprio contador por IP.
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<AuthRateLimitOptions>>((rateLimiter, auth) =>
        {
            foreach (var (name, policy) in auth.Value.Policies)
            {
                rateLimiter.AddPolicy(name, http => RateLimitPartition.GetFixedWindowLimiter(
                    ClientAddress(http),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = policy.PermitLimit,
                        Window = policy.Window,
                        QueueLimit = 0,
                    }));
            }
        });
    }

    private static string ClientAddress(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "desconhecido";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Limite de requisições excedido em {Path} pelo IP {ClientAddress}.")]
    private static partial void LogRejected(ILogger logger, string path, string clientAddress);
}
