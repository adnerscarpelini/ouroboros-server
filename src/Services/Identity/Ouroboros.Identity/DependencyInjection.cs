using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Ouroboros.BuildingBlocks.Persistence;
using Ouroboros.Identity.Application.Administrators;
using Ouroboros.Identity.Application.Notifications;
using Ouroboros.Identity.Application.Parameters;
using Ouroboros.Identity.Application.PasswordResets;
using Ouroboros.Identity.Application.Registrations;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Application.Sessions;
using Ouroboros.Identity.Application.Users;
using Ouroboros.Identity.Contracts;
using Ouroboros.Identity.Contracts.Administrators;
using Ouroboros.Identity.Contracts.PasswordResets;
using Ouroboros.Identity.Contracts.Registrations;
using Ouroboros.Identity.Contracts.Sessions;
using Ouroboros.Identity.Contracts.Users;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Email;
using Ouroboros.Identity.Infrastructure.Persistence;
using Ouroboros.Identity.Infrastructure.RateLimiting;
using Ouroboros.Identity.Infrastructure.Security;

namespace Ouroboros.Identity;

public static class DependencyInjection
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        // Validada na subida da API, depois que todas as fontes de configuração foram carregadas.
        services.AddOptions<IdentityDatabaseOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                // O banco próprio tem precedência; sem ele, vale o banco original (antes da transferência, ou depois de
                // voltar a ele). Remover ConnectionStrings:Identity é, portanto, o retorno à connection string anterior.
                var own = configuration.GetConnectionString(IdentityDatabaseOptions.ConnectionStringName);
                options.ConnectionString = string.IsNullOrWhiteSpace(own)
                    ? configuration.GetConnectionString(IdentityDatabaseOptions.LegacyConnectionStringName)
                    : own;
            })
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                $"A connection string 'ConnectionStrings:{IdentityDatabaseOptions.ConnectionStringName}' não foi configurada " +
                $"(nem a 'ConnectionStrings:{IdentityDatabaseOptions.LegacyConnectionStringName}' do banco original). " +
                "Em desenvolvimento, defina-a com dotnet user-secrets (veja docs/development.md).")
            .ValidateOnStart();

        AddTokenAuthentication(services);
        AddEmail(services);
        AuthRateLimiting.Add(services);

        services.AddSingleton<IPasswordHasher, AspNetCorePasswordHasher>();
        services.AddSingleton<UserPasswordVerifier>();
        services.AddScoped<ICreateFirstAdmin, CreateFirstAdmin>();
        services.AddScoped<IFirstAdminStore, FirstAdminStore>();
        services.AddScoped<ILogin, Login>();
        services.AddScoped<IRefreshSession, RefreshSession>();
        services.AddScoped<ILogout, Logout>();
        services.AddScoped<ILogoutAll, LogoutAll>();
        services.AddScoped<IGetCurrentUser, GetCurrentUser>();
        services.AddScoped<ICurrentUserReader, CurrentUserReader>();
        services.AddScoped<IRequestRegistration, RequestRegistration>();
        services.AddScoped<ICompleteRegistration, CompleteRegistration>();
        services.AddScoped<IRequestPasswordReset, RequestPasswordReset>();
        services.AddScoped<ICompletePasswordReset, CompletePasswordReset>();
        services.AddScoped<AccountLockout>();
        services.AddScoped<ISessionStore, SessionStore>();
        services.AddScoped<IRegistrationStore, RegistrationStore>();
        services.AddScoped<IPasswordResetStore, PasswordResetStore>();
        services.AddScoped<ISystemParameters, SystemParameters>();
        services.AddScoped<IParameterReader, ParameterReader>();
        services.AddScoped<IParameterPersistence, ParameterPersistence>();
        services.AddScoped<ParameterWriter>();

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

    // O Identity emite e valida os tokens. Cada público aceita só a própria audiência
    // (Authentication:Jwt:Audience e AdminAudience do appsettings do host).
    private static void AddTokenAuthentication(IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();
        services.AddOptions<JwtOptions>().BindConfiguration(JwtOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IAccessTokenIssuer, AccessTokenIssuer>();
        services.AddSingleton(provider => new SessionTokenOptions(
            provider.GetRequiredService<IOptions<JwtOptions>>().Value.RefreshTokenLifetime));

        // Dois esquemas, um por público. Um host com um só público usa o padrão; quem hospeda os dois
        // escolhe o esquema pelo listener (veja IdentityAuthenticationSchemes).
        services.AddAuthentication(IdentityAuthenticationSchemes.Customer)
            .AddJwtBearer(IdentityAuthenticationSchemes.Customer, _ => { })
            .AddJwtBearer(IdentityAuthenticationSchemes.Admin, _ => { });
        ConfigureBearer(services, IdentityAuthenticationSchemes.Customer, jwt => jwt.Audience);
        ConfigureBearer(services, IdentityAuthenticationSchemes.Admin, jwt => jwt.AdminAudience is { Length: > 0 } adminAudience ? adminAudience : jwt.Audience);

        // Mapeamento de papel para política: só aqui, no módulo dono das capacidades.
        services.AddAuthorization(options => options.AddPolicy(
            IdentityPolicies.AdminsInvite,
            policy => policy.RequireRole(nameof(UserRole.Admin))));
    }

    private static void ConfigureBearer(IServiceCollection services, string scheme, Func<JwtOptions, string> audience) =>
        services.AddOptions<JwtBearerOptions>(scheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;
                bearer.MapInboundClaims = false;

                // O 401 não explica por que o token foi recusado (assinatura, expiração etc.).
                bearer.IncludeErrorDetails = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = audience(jwt),
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
