using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Ouroboros.BuildingBlocks.Persistence;
using Ouroboros.Modules.Settings.Application.Parameters;
using Ouroboros.Modules.Settings.Contracts;
using Ouroboros.Modules.Settings.Infrastructure.Persistence;

namespace Ouroboros.Modules.Settings;

public static class DependencyInjection
{
    public static IServiceCollection AddSettingsModule(this IServiceCollection services)
    {
        // Validada na subida da API, depois que todas as fontes de configuração foram carregadas.
        services.AddOptions<SettingsDatabaseOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                options.ConnectionString = configuration.GetConnectionString(SettingsDatabaseOptions.ConnectionStringName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                $"A connection string 'ConnectionStrings:{SettingsDatabaseOptions.ConnectionStringName}' não foi configurada. " +
                "Em desenvolvimento, defina-a com dotnet user-secrets (veja o README).")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<AuditTimestampsInterceptor>();

        // O histórico de migrations fica no schema do próprio módulo.
        services.AddDbContext<SettingsDbContext>((provider, options) => options
            .UseNpgsql(
                provider.GetRequiredService<IOptions<SettingsDatabaseOptions>>().Value.ConnectionString,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", SettingsDbContext.Schema))
            .AddInterceptors(provider.GetRequiredService<AuditTimestampsInterceptor>()));

        services.AddScoped<ISystemParameters, SystemParameters>();
        services.AddScoped<ParameterWriter>();

        return services;
    }
}
