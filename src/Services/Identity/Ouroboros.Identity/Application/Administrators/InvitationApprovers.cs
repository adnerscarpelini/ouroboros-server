using Microsoft.Extensions.Logging;
using Ouroboros.Identity.Contracts;
using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Application.Administrators;

internal sealed partial class InvitationApprovers(ISystemParameters parameters, ILogger<InvitationApprovers> logger)
{
    internal async Task<IReadOnlyList<AdminRecipient>> ReadAsync(IReadOnlyList<AdminRecipient> admins, Guid requester, CancellationToken cancellationToken)
    {
        var domain = await parameters.GetAsync(CreateFirstAdmin.AllowedEmailDomainParameter, cancellationToken);
        var raw = await parameters.GetAsync("Identity:AdminInvitations:ApproverEmails", cancellationToken);
        var configured = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(email => email.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        var valid = configured.Where(email => EmailAddresses.IsValid(email) && email[(email.LastIndexOf('@') + 1)..] == domain).ToHashSet(StringComparer.Ordinal);
        if (valid.Count != configured.Length) { LogInvalidApprovers(logger, configured.Length - valid.Count); }
        return admins.Where(admin => admin.Id != requester && valid.Contains(admin.Email)).ToArray();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Configuração de aprovadores contém {Count} entradas inválidas ou fora do domínio permitido.")]
    private static partial void LogInvalidApprovers(ILogger logger, int count);
}
