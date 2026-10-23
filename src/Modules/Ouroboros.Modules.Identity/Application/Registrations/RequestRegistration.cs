using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ouroboros.Modules.Identity.Application.Notifications;
using Ouroboros.Modules.Identity.Application.Security;
using Ouroboros.Modules.Identity.Contracts.Registrations;
using Ouroboros.Modules.Identity.Domain.EmailTokens;
using Ouroboros.Modules.Identity.Domain.Users;
using Ouroboros.Modules.Identity.Infrastructure.Persistence;
using Ouroboros.Modules.Settings.Contracts;

namespace Ouroboros.Modules.Identity.Application.Registrations;

internal sealed partial class RequestRegistration(
    IdentityDbContext context,
    ISystemParameters parameters,
    IEmailSender emailSender,
    TimeProvider timeProvider,
    ILogger<RequestRegistration> logger) : IRequestRegistration
{
    public const string TokenLifetimeHoursParameter = "Identity:Registration:TokenLifetimeHours";
    public const string CompletionUrlParameter = "Identity:Registration:CompletionUrl";

    // Mesmo limite do padrão do parâmetro, conferido também aqui para um valor gravado direto no banco.
    private const int MaxTokenLifetimeHours = 168;

    public async Task<RequestRegistrationResult> ExecuteAsync(RequestRegistrationRequest request, CancellationToken cancellationToken)
    {
        // Os parâmetros são lidos antes de qualquer decisão: parâmetro ausente ou inválido falha igual
        // para qualquer e-mail.
        var lifetime = TimeSpan.FromHours(await ReadLifetimeHoursAsync(cancellationToken));
        var completionUrl = await parameters.GetAsync(CompletionUrlParameter, cancellationToken);

        var email = string.IsNullOrWhiteSpace(request.Email) ? "" : User.NormalizeEmail(request.Email);
        if (!EmailAddresses.IsValid(email))
        {
            return RequestRegistrationResult.InvalidEmail;
        }

        // E-mail já usado por uma conta: nada é gerado nem enviado, e a resposta é a mesma.
        if (await context.Users.AnyAsync(user => user.Email == email, cancellationToken))
        {
            return RequestRegistrationResult.Accepted;
        }

        var (token, tokenHash) = OpaqueTokens.Generate();
        var now = timeProvider.GetUtcNow();
        await using (var transaction = await context.Database.BeginTransactionAsync(cancellationToken))
        {
            // Serializa solicitações simultâneas para o mesmo e-mail, para sobrar um único token pendente.
            await context.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_xact_lock(hashtext('identity:registration:' || {email}))", cancellationToken);

            // Uma nova solicitação invalida os tokens de cadastro ainda pendentes desse e-mail.
            await context.EmailTokens
                .Where(pending => pending.Email == email
                    && pending.Purpose == EmailTokenPurpose.Registration
                    && pending.UsedAt == null
                    && pending.RevokedAt == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(pending => pending.RevokedAt, now)
                        .SetProperty(pending => pending.UpdatedAt, now),
                    cancellationToken);

            context.EmailTokens.Add(EmailToken.ForRegistration(email, tokenHash, now.Add(lifetime)));
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // Envio depois do commit. Uma falha não muda a resposta (senão revelaria que o e-mail está livre):
        // fica no log, sem o token, e a pessoa pode solicitar de novo.
        try
        {
            await emailSender.SendAsync(
                new EmailMessage(
                    email,
                    "Conclua o seu cadastro no Ouroboros",
                    $"Para concluir o seu cadastro, acesse o link abaixo e defina a sua senha:\n\n" +
                    $"{completionUrl}?token={Uri.EscapeDataString(token)}\n\n" +
                    $"O link vale por {lifetime.TotalHours.ToString(CultureInfo.InvariantCulture)} horas e só pode ser usado uma vez. " +
                    "Se você não pediu este cadastro, ignore esta mensagem."),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogEmailFailed(exception.GetType().Name);
        }

        return RequestRegistrationResult.Accepted;
    }

    private async Task<int> ReadLifetimeHoursAsync(CancellationToken cancellationToken)
    {
        var value = await parameters.GetAsync(TokenLifetimeHoursParameter, cancellationToken);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var hours) && hours is > 0 and <= MaxTokenLifetimeHours
            ? hours
            : throw new InvalidOperationException(
                $"O parâmetro do sistema '{TokenLifetimeHoursParameter}' deve ser um inteiro de 1 a {MaxTokenLifetimeHours}.");
    }

    // Sem o e-mail, o link nem a mensagem da exceção, que poderiam conter o token.
    [LoggerMessage(Level = LogLevel.Error, Message = "Falha ao enviar o e-mail de cadastro ({ExceptionType}).")]
    private partial void LogEmailFailed(string exceptionType);
}
