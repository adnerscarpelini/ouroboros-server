using System.Globalization;
using Ouroboros.Identity.Application.Notifications;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Contracts;
using Ouroboros.Identity.Domain.Administrators;
using Ouroboros.Identity.Domain.Notifications;

namespace Ouroboros.Identity.Application.Administrators;

internal sealed class InvitationEmails(ISystemParameters parameters, IEmailIntentFactory emails)
{
    internal const string LifetimeParameter = "Identity:AdminInvitations:TokenLifetimeHours";
    internal const string CompletionUrlParameter = "Identity:AdminInvitations:CompletionUrl";

    internal async Task<EmailIntent> ApproveAsync(AdminInvitation invitation, Guid? decidedBy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var raw = await parameters.GetAsync(LifetimeParameter, cancellationToken);
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var hours) || hours is < 1 or > 168)
        { throw new InvalidOperationException("Validade do convite inválida."); }
        var url = await parameters.GetAsync(CompletionUrlParameter, cancellationToken);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
        { throw new InvalidOperationException("URL de conclusão do convite inválida."); }
        var (token, hash) = OpaqueTokens.Generate();
        var expiresAt = now.AddHours(hours);
        invitation.Approve(hash, expiresAt, decidedBy, now);
        return emails.Create(new(invitation.Email, "Convite de administrador do Ouroboros",
            $"Defina a sua senha na rede administrativa: {url}?token={Uri.EscapeDataString(token)}\nO link é de uso único e vale até {expiresAt:O}."),
            "admin-invitation", now, expiresAt);
    }

    internal IEnumerable<EmailIntent> Notices(IReadOnlyList<AdminRecipient> admins, AdminInvitation invitation, string action, DateTimeOffset now) =>
        admins.Select(admin => emails.Create(new(admin.Email, "Convite de administrador",
            $"Convite {invitation.ExternalId}: {action}; autor {invitation.InvitedBy}; data {now:O}."), "admin-notice", now, now.AddHours(24)));
}
