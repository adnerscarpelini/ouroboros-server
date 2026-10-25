using System.Globalization;
using Microsoft.Extensions.Logging;
using Ouroboros.Identity.Application.Notifications;
using Ouroboros.Identity.Application.Security;
using Ouroboros.Identity.Contracts;
using Ouroboros.Identity.Contracts.Registrations;
using Ouroboros.Identity.Domain.EmailTokens;
using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Application.Registrations;

internal sealed partial class RequestRegistration(
    IRegistrationStore store,
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
        if (await store.EmailExistsAsync(email, cancellationToken))
        {
            return RequestRegistrationResult.Accepted;
        }

        var (token, tokenHash) = OpaqueTokens.Generate();
        var now = timeProvider.GetUtcNow();
        await store.ReplacePendingTokenAsync(EmailToken.ForRegistration(email, tokenHash, now.Add(lifetime)), now, cancellationToken);

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
