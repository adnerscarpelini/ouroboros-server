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
using Ouroboros.Identity.Contracts.Parameters;
using Ouroboros.Identity.Contracts.PasswordResets;
using Ouroboros.Identity.Contracts.Registrations;
using Ouroboros.Identity.Contracts.Sessions;
using Ouroboros.Identity.Contracts.Users;
using Ouroboros.Identity.Domain.Users;
using Ouroboros.Identity.Infrastructure.Messaging;
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
        services.AddSingleton<IValidateOptions<EmailIntentOptions>, EmailIntentOptionsValidator>();
        services.AddOptions<EmailIntentOptions>().BindConfiguration(EmailIntentOptions.SectionName).ValidateOnStart();
        services.AddSingleton<ProtectedEmailIntentFactory>();
        services.AddSingleton<IEmailIntentFactory>(provider => provider.GetRequiredService<ProtectedEmailIntentFactory>());
        services.AddHostedService<EmailIntentProtectionStartupCheck>();
        services.AddHostedService<EmailIntentMaintenance>();
        services.AddSingleton<IValidateOptions<EmailPublisherOptions>, EmailPublisherOptionsValidator>();
        services.AddOptions<EmailPublisherOptions>().BindConfiguration(EmailPublisherOptions.SectionName).ValidateOnStart();
        services.AddSingleton<EmailOutboxMetrics>();
        services.AddScoped<EmailOutboxStore>();
        services.AddHostedService<EmailOutboxPublisher>();
        AuthRateLimiting.Add(services);

        services.AddSingleton<IPasswordHasher, AspNetCorePasswordHasher>();
        services.AddSingleton<UserPasswordVerifier>();
        services.AddScoped<ICreateFirstAdmin, CreateFirstAdmin>();
        services.AddScoped<IFirstAdminStore, FirstAdminStore>();
        services.AddScoped<IAdminDirectory, AdminDirectory>();
        services.AddScoped<AdminAuthorization>();
        services.AddScoped<InvitationEmails>();
        services.AddScoped<InvitationApprovers>();
        services.AddScoped<IDecideAdminInvitation, DecideAdminInvitation>();
        services.AddScoped<IAdminInvitationStore, AdminInvitationStore>();
        services.AddScoped<IInviteAdmin, InviteAdmin>();
        services.AddScoped<ICompleteAdminInvitation, CompleteAdminInvitation>();
        services.AddScoped<ILogin, Login>();
        services.AddScoped<IRefreshSession, RefreshSession>();
        services.AddScoped<ILogout, Logout>();
        services.AddScoped<ILogoutAll, LogoutAll>();
        services.AddScoped<IGetCurrentUser, GetCurrentUser>();
        services.AddScoped<IChangePassword, ChangePassword>();
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
        services.AddScoped<IListParameters, ListParameters>();
        services.AddScoped<IParameterListReader, ParameterListReader>();
        services.AddScoped<IGetParameterHistory, GetParameterHistory>();
        services.AddScoped<IParameterHistoryReader, ParameterHistoryReader>();
        services.AddScoped<IChangeParameter, ChangeParameter>();
        services.AddScoped<IParameterAdministrationReader, ParameterAdministrationReader>();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<AuditTimestampsInterceptor>();

        // O histórico de migrations fica no schema do próprio módulo.
        services.AddDbContext<IdentityDbContext>((provider, options) => options
            .UseNpgsql(
                provider.GetRequiredService<IOptions<IdentityDatabaseOptions>>().Value.ConnectionString,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", IdentityDbContext.Schema))
            .AddInterceptors(provider.GetRequiredService<AuditTimestampsInterceptor>()));
        services.AddHealthChecks()
            .AddCheck<IdentityReadinessHealthCheck>("identity-db", tags: ["ready"]);

        return services;
    }

    // Execução explícita de migrations pelo comando operacional; nunca chamada na subida da API.
    public static async Task MigrateDatabaseAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string de migration de Identity ausente.");
        }

        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        if (string.Equals(builder.Username, "identity_runtime", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A role identity_runtime não pode aplicar migrations; use identity_migrator.");
        }

        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", IdentityDbContext.Schema))
            .Options;
        await using var context = new IdentityDbContext(options);
        await context.Database.MigrateAsync(cancellationToken);
    }

    // O Identity emite e valida os tokens. Cada público aceita só a própria audiência
    // (Authentication:Jwt:Audience e AdminAudience do appsettings do host).
    private static void AddTokenAuthentication(IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();
        services.AddOptions<JwtOptions>().BindConfiguration(JwtOptions.SectionName).ValidateOnStart();
        services.AddSingleton<TokenKeyRing>();
        services.AddSingleton<ITokenPublicKeys>(provider => provider.GetRequiredService<TokenKeyRing>());
        services.AddHostedService<SigningKeysStartupCheck>();
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
        services.AddAuthorization(options =>
        {
            options.AddPolicy(IdentityPolicies.AdminsInvite, policy => policy.RequireRole(nameof(UserRole.Admin)));
            options.AddPolicy(IdentityPolicies.ParametersRead, policy => policy.RequireRole(nameof(UserRole.Admin)));
            options.AddPolicy(IdentityPolicies.ParametersWrite, policy => policy.RequireRole(nameof(UserRole.Admin)));
        });
    }

    private static void ConfigureBearer(IServiceCollection services, string scheme, Func<JwtOptions, string> audience) =>
        services.AddOptions<JwtBearerOptions>(scheme)
            .Configure<IOptions<JwtOptions>, TokenKeyRing>((bearer, jwtOptions, keys) =>
            {
                var jwt = jwtOptions.Value;
                bearer.MapInboundClaims = false;

                // O 401 não explica por que o token foi recusado (assinatura, expiração etc.).
                bearer.IncludeErrorDetails = false;
                bearer.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var principal = context.Principal!;
                        var kind = principal.FindFirst(IdentityClaims.PrincipalType)?.Value;
                        var algorithm = ((Microsoft.IdentityModel.JsonWebTokens.JsonWebToken)context.SecurityToken).Alg;
                        // Somente tokens HS256 da janela legada podem omitir o discriminador novo.
                        if (principal.FindAll(IdentityClaims.PrincipalType).Skip(1).Any()
                            || principal.FindAll(IdentityClaims.Subject).Count() != 1
                            || principal.FindAll(IdentityClaims.Session).Count() != 1
                            || principal.FindAll(IdentityClaims.Role).Count() != 1
                            || (kind != "user" && !(kind is null && algorithm == SecurityAlgorithms.HmacSha256))
                            || !Guid.TryParse(principal.FindFirst(IdentityClaims.Subject)?.Value, out var subject) || subject == Guid.Empty
                            || !Guid.TryParse(principal.FindFirst(IdentityClaims.Session)?.Value, out var session) || session == Guid.Empty
                            || principal.FindFirst(IdentityClaims.Role)?.Value is not ("Customer" or "Admin"))
                        {
                            context.Fail("Principal inválido.");
                        }
                        return Task.CompletedTask;
                    },
                };
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = audience(jwt),
                    IssuerSigningKeyResolver = (_, token, kid, _) => keys.Resolve(kid, ((Microsoft.IdentityModel.JsonWebTokens.JsonWebToken)token).Alg),
                    TryAllIssuerSigningKeys = false,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.HmacSha256],
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

}
