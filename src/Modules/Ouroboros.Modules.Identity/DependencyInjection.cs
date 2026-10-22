using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Ouroboros.Modules.Identity.Infrastructure.Persistence;

namespace Ouroboros.Modules.Identity;

public static class DependencyInjection
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        // Os controllers internal do módulo são descobertos a partir desta application part.
        services.AddControllers().AddApplicationPart(typeof(DependencyInjection).Assembly);

        // Validada na subida da API, depois que todas as fontes de configuração foram carregadas.
        services.AddOptions<IdentityDatabaseOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                options.ConnectionString = configuration.GetConnectionString(IdentityDatabaseOptions.ConnectionStringName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                $"A connection string 'ConnectionStrings:{IdentityDatabaseOptions.ConnectionStringName}' não foi configurada. " +
                "Em desenvolvimento, defina-a com dotnet user-secrets (veja o README).")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AuditTimestampsInterceptor>();

        // O histórico de migrations fica no schema do próprio módulo.
        services.AddDbContext<IdentityDbContext>((provider, options) => options
            .UseNpgsql(
                provider.GetRequiredService<IOptions<IdentityDatabaseOptions>>().Value.ConnectionString,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", IdentityDbContext.Schema))
            .AddInterceptors(provider.GetRequiredService<AuditTimestampsInterceptor>()));

        return services;
    }
}
