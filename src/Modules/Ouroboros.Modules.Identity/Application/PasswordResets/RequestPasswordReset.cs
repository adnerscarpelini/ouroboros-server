using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ouroboros.Modules.Identity.Application.Notifications;
using Ouroboros.Modules.Identity.Application.Security;
using Ouroboros.Modules.Identity.Contracts.PasswordResets;
using Ouroboros.Modules.Identity.Domain.EmailTokens;
using Ouroboros.Modules.Identity.Domain.Users;
using Ouroboros.Modules.Identity.Infrastructure.Persistence;
using Ouroboros.Modules.Settings.Contracts;

namespace Ouroboros.Modules.Identity.Application.PasswordResets;

internal sealed partial class RequestPasswordReset(
    IdentityDbContext context,
    ISystemParameters parameters,
    IEmailSender emailSender,
    TimeProvider timeProvider,
    ILogger<RequestPasswordReset> logger) : IRequestPasswordReset
{
    public const string TokenLifetimeMinutesParameter = "Identity:PasswordReset:TokenLifetimeMinutes";
    public const string CompletionUrlParameter = "Identity:PasswordReset:CompletionUrl";

    // Mesmo limite do padrão do parâmetro, conferido também aqui para um valor gravado direto no banco.
    private const int MaxTokenLifetimeMinutes = 1440;

    public async Task<RequestPasswordResetResult> ExecuteAsync(RequestPasswordResetRequest request, CancellationToken cancellationToken)
    {
        // Os parâmetros são lidos antes de qualquer decisão: parâmetro ausente ou inválido falha igual
        // para qualquer e-mail.
        var lifetime = TimeSpan.FromMinutes(await ReadLifetimeMinutesAsync(cancellationToken));
        var completionUrl = await parameters.GetAsync(CompletionUrlParameter, cancellationToken);

        var email = string.IsNullOrWhiteSpace(request.Email) ? "" : User.NormalizeEmail(request.Email);
        if (!EmailAddresses.IsValid(email))
        {
            return RequestPasswordResetResult.InvalidEmail;
        }

        // Só uma conta de cliente ativa recebe o link. Contas de backoffice não são recuperadas pela API
        // pública. Nos outros casos, nada é gerado nem enviado e a resposta é a mesma.
        var user = await context.Users
            .AsNoTracking()
            .Where(user => user.Email == email && user.IsActive && user.Role == UserRole.Customer)
            .Select(user => new { user.Id, user.Email })
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            return RequestPasswordResetResult.Accepted;
        }

        var (token, tokenHash) = OpaqueTokens.Generate();
        var now = timeProvider.GetUtcNow();
        await using (var transaction = await context.Database.BeginTransactionAsync(cancellationToken))
        {
            // Serializa solicitações simultâneas da mesma conta, para sobrar um único token pendente.
            await context.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_xact_lock(hashtext('identity:password-reset:' || {user.Id}))", cancellationToken);

            // Uma nova solicitação invalida os tokens de redefinição ainda pendentes da conta.
            await context.EmailTokens
                .Where(pending => pending.UserId == user.Id
                    && pending.Purpose == EmailTokenPurpose.PasswordReset
                    && pending.UsedAt == null
                    && pending.RevokedAt == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(pending => pending.RevokedAt, now)
                        .SetProperty(pending => pending.UpdatedAt, now),
                    cancellationToken);

            context.EmailTokens.Add(EmailToken.ForPasswordReset(user.Id, user.Email, tokenHash, now.Add(lifetime)));
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // O link vai para o e-mail gravado na conta. Envio depois do commit; uma falha não muda a resposta
        // (senão revelaria que a conta existe): fica no log, sem o token, e a pessoa pode pedir de novo.
        try
        {
            await emailSender.SendAsync(
                new EmailMessage(
                    user.Email,
                    "Redefinição de senha do Ouroboros",
                    "Recebemos um pedido para redefinir a sua senha. Para escolher uma senha nova, acesse:\n\n" +
                    $"{completionUrl}?token={Uri.EscapeDataString(token)}\n\n" +
                    $"O link vale por {lifetime.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutos e só pode ser usado uma vez. " +
                    "A senha atual continua valendo até a redefinição ser concluída. Se você não fez este pedido, ignore esta mensagem."),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogEmailFailed(exception.GetType().Name);
        }

        return RequestPasswordResetResult.Accepted;
    }

    private async Task<int> ReadLifetimeMinutesAsync(CancellationToken cancellationToken)
    {
        var value = await parameters.GetAsync(TokenLifetimeMinutesParameter, cancellationToken);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes is > 0 and <= MaxTokenLifetimeMinutes
            ? minutes
            : throw new InvalidOperationException(
                $"O parâmetro do sistema '{TokenLifetimeMinutesParameter}' deve ser um inteiro de 1 a {MaxTokenLifetimeMinutes}.");
    }

    // Sem o e-mail, o link nem a mensagem da exceção, que poderiam conter o token.
    [LoggerMessage(Level = LogLevel.Error, Message = "Falha ao enviar o e-mail de redefinição de senha ({ExceptionType}).")]
    private partial void LogEmailFailed(string exceptionType);
}
