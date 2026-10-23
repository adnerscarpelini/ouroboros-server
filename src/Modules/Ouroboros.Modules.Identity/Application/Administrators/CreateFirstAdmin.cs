using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Ouroboros.Modules.Identity.Application.Security;
using Ouroboros.Modules.Identity.Contracts.Administrators;
using Ouroboros.Modules.Identity.Domain.Users;
using Ouroboros.Modules.Identity.Infrastructure.Persistence;
using Ouroboros.Modules.Settings.Contracts;

namespace Ouroboros.Modules.Identity.Application.Administrators;

internal sealed class CreateFirstAdmin(
    IdentityDbContext context,
    IPasswordHasher passwordHasher,
    ISystemParameters parameters) : ICreateFirstAdmin
{
    public const string AllowedEmailDomainParameter = "Identity:AdminInvitations:AllowedEmailDomain";

    // Serializa execuções simultâneas do comando: sem isso, duas execuções poderiam passar pela
    // verificação "não existe Admin" ao mesmo tempo e criar dois.
    private const string LockSql = "SELECT pg_advisory_xact_lock(hashtext('identity:create-first-admin'))";

    public async Task<CreateFirstAdminResult> ExecuteAsync(CreateFirstAdminRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || !IsEmail(User.NormalizeEmail(request.Email), out var email, out var host))
        {
            return new(CreateFirstAdminOutcome.InvalidEmail, "Informe um e-mail válido.");
        }

        string allowedDomain;
        try
        {
            // O Settings já valida o domínio em minúsculas; a normalização aqui protege a comparação.
            allowedDomain = (await parameters.GetAsync(AllowedEmailDomainParameter, cancellationToken)).Trim().ToLowerInvariant();
        }
        catch (ParameterNotFoundException)
        {
            return new(
                CreateFirstAdminOutcome.ConfigurationMissing,
                $"O parâmetro do sistema '{AllowedEmailDomainParameter}' não existe; aplique as migrations do Settings.");
        }

        if (!string.Equals(host, allowedDomain, StringComparison.Ordinal))
        {
            return new(
                CreateFirstAdminOutcome.EmailDomainNotAllowed,
                $"O e-mail de administrador precisa ser do domínio '{allowedDomain}' (subdomínios não são aceitos).");
        }

        if (PasswordPolicy.Validate(request.Password, email) is { } rule)
        {
            return new(CreateFirstAdminOutcome.PasswordRejected, rule);
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync(LockSql, cancellationToken);

        if (await context.Users.AnyAsync(user => user.Role == UserRole.Admin, cancellationToken))
        {
            return new(
                CreateFirstAdminOutcome.AdminAlreadyExists,
                "Já existe um administrador. Novos administradores entram por convite.");
        }

        if (await context.Users.AnyAsync(user => user.Email == email, cancellationToken))
        {
            return new(CreateFirstAdminOutcome.EmailAlreadyInUse, "Esse e-mail já pertence a outro usuário.");
        }

        var admin = User.Create(email, passwordHasher.Hash(request.Password), UserRole.Admin);
        context.Users.Add(admin);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new(CreateFirstAdminOutcome.Created, $"Administrador {email} criado.", admin.ExternalId);
    }

    private static bool IsEmail(string normalized, out string email, out string host)
    {
        email = normalized;
        host = "";
        if (!MailAddress.TryCreate(normalized, out var address) || address.Address != normalized)
        {
            return false;
        }

        host = address.Host;
        return true;
    }
}
