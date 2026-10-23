using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Ouroboros.BuildingBlocks.Persistence;
using Ouroboros.Modules.Identity.Application.Administrators;
using Ouroboros.Modules.Identity.Application.Notifications;
using Ouroboros.Modules.Identity.Application.Registrations;
using Ouroboros.Modules.Identity.Application.Security;
using Ouroboros.Modules.Identity.Application.Sessions;
using Ouroboros.Modules.Identity.Application.Users;
using Ouroboros.Modules.Identity.Contracts;
using Ouroboros.Modules.Identity.Contracts.Administrators;
using Ouroboros.Modules.Identity.Contracts.Registrations;
using Ouroboros.Modules.Identity.Contracts.Sessions;
using Ouroboros.Modules.Identity.Contracts.Users;
using Ouroboros.Modules.Identity.Domain.Users;
using Ouroboros.Modules.Identity.Infrastructure.Email;
using Ouroboros.Modules.Identity.Infrastructure.Persistence;
using Ouroboros.Modules.Identity.Infrastructure.RateLimiting;
using Ouroboros.Modules.Identity.Infrastructure.Security;

namespace Ouroboros.Modules.Identity;

public static class DependencyInjection
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        // Validada na subida da API, depois que todas as fontes de configuração foram carregadas.
        services.AddOptions<IdentityDatabaseOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                options.ConnectionString = configuration.GetConnectionString(IdentityDatabaseOptions.ConnectionStringName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                $"A connection string 'ConnectionStrings:{IdentityDatabaseOptions.ConnectionStringName}' não foi configurada. " +
                "Em desenvolvimento, defina-a com dotnet user-secrets (veja o README).")
            .ValidateOnStart();

        AddTokenAuthentication(services);
        AddEmail(services);
        AuthRateLimiting.Add(services);

        services.AddSingleton<IPasswordHasher, AspNetCorePasswordHasher>();
        services.AddSingleton<UserPasswordVerifier>();
        services.AddScoped<ICreateFirstAdmin, CreateFirstAdmin>();
        services.AddScoped<ILogin, Login>();
        services.AddScoped<IRefreshSession, RefreshSession>();
        services.AddScoped<ILogout, Logout>();
        services.AddScoped<ILogoutAll, LogoutAll>();
        services.AddScoped<IGetCurrentUser, GetCurrentUser>();
        services.AddScoped<IRequestRegistration, RequestRegistration>();
        services.AddScoped<ICompleteRegistration, CompleteRegistration>();
        services.AddScoped<AccountLockout>();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<AuditTimestampsInterceptor>();

        // O histórico de migrations fica no schema do próprio módulo.
        services.AddDbContext<IdentityDbContext>((provider, options) => options
            .UseNpgsql(
                provider.GetRequiredService<IOptions<IdentityDatabaseOptions>>().Value.ConnectionString,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", IdentityDbContext.Schema))
            .AddInterceptors(provider.GetRequiredService<AuditTimestampsInterceptor>()));

        return services;
    }

    // O Identity emite e valida os tokens. Cada host aceita só a própria audiência
    // (Authentication:Jwt:Audience do seu appsettings).
    private static void AddTokenAuthentication(IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();
        services.AddOptions<JwtOptions>().BindConfiguration(JwtOptions.SectionName).ValidateOnStart();
        services.AddSingleton<AccessTokenIssuer>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;
                bearer.MapInboundClaims = false;

                // O 401 não explica por que o token foi recusado (assinatura, expiração etc.).
                bearer.IncludeErrorDetails = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(jwt.SigningKeyBytes),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = IdentityClaims.Subject,
                    RoleClaimType = IdentityClaims.Role,
                };
            });

        // Mapeamento de papel para política: só aqui, no módulo dono das capacidades.
        services.AddAuthorization(options => options.AddPolicy(
            IdentityPolicies.AdminsInvite,
            policy => policy.RequireRole(nameof(UserRole.Admin))));
    }

    // O provedor de e-mail vem da configuração (Identity:Email:Provider). O adaptador local só é aceito
    // em Development; sem provedor, enviar é erro explícito.
    private static void AddEmail(IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>();
        services.AddOptions<EmailOptions>().BindConfiguration(EmailOptions.SectionName).ValidateOnStart();
        services.AddSingleton<PickupDirectoryEmailSender>();
        services.AddSingleton<UnconfiguredEmailSender>();
        services.AddSingleton<IEmailSender>(provider =>
            provider.GetRequiredService<IOptions<EmailOptions>>().Value.Provider == EmailProvider.PickupDirectory
                ? provider.GetRequiredService<PickupDirectoryEmailSender>()
                : provider.GetRequiredService<UnconfiguredEmailSender>());
    }
}
