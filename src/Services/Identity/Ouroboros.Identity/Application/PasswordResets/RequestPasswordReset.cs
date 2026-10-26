using System.Globalization;
using Microsoft.Extensions.Logging;
using Ouroboros.Identity.Application.Notifications;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Contracts;
using Ouroboros.Identity.Contracts.PasswordResets;
using Ouroboros.Identity.Domain.EmailTokens;
using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Application.PasswordResets;

internal sealed partial class RequestPasswordReset(
    IPasswordResetStore store,
    ISystemParameters parameters,
    IEmailIntentFactory intents,
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

        var (token, tokenHash) = OpaqueTokens.Generate();
        var now = timeProvider.GetUtcNow();
        try
        {
            var intent = intents.Create(new EmailMessage(
                email, "Redefinição de senha do Ouroboros",
                "Recebemos um pedido para redefinir a sua senha. Para escolher uma senha nova, acesse:\n\n" +
                $"{completionUrl}?token={Uri.EscapeDataString(token)}\n\n" +
                $"O link vale por {lifetime.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutos e só pode ser usado uma vez. " +
                "Use sempre o link da solicitação mais recente. A senha atual continua valendo até a redefinição ser concluída. " +
                "Se você não fez este pedido, ignore esta mensagem."), "password-reset", now, now.Add(lifetime));
            var user = await store.FindCustomerAsync(email, cancellationToken);
            if (user is not null)
            {
                await store.ReplacePendingTokenAsync(EmailToken.ForPasswordReset(user.Id, user.Email, tokenHash, now.Add(lifetime)), intent, now, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogIntentFailed(exception);
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha ao registrar intenção de e-mail; nenhum sucesso de entrega é inferido.")]
    private partial void LogIntentFailed(Exception exception);
}
