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
    IEmailIntentFactory intents,
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

        var (token, tokenHash) = OpaqueTokens.Generate();
        var now = timeProvider.GetUtcNow();
        try
        {
            // Preparação local antes da consulta de existência: falha de proteção não vira oráculo.
            var intent = intents.Create(new EmailMessage(
                email, "Conclua o seu cadastro no Ouroboros",
                $"Para concluir o seu cadastro, acesse o link abaixo e defina a sua senha:\n\n" +
                $"{completionUrl}?token={Uri.EscapeDataString(token)}\n\n" +
                $"O link vale por {lifetime.TotalHours.ToString(CultureInfo.InvariantCulture)} horas e só pode ser usado uma vez. " +
                "Use sempre o link da solicitação mais recente. Se você não pediu este cadastro, ignore esta mensagem."),
                "registration", now, now.Add(lifetime));
            if (!await store.EmailExistsAsync(email, cancellationToken))
            {
                await store.ReplacePendingTokenAsync(EmailToken.ForRegistration(email, tokenHash, now.Add(lifetime)), intent, now, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogIntentFailed(exception);
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha ao registrar intenção de e-mail; nenhum sucesso de entrega é inferido.")]
    private partial void LogIntentFailed(Exception exception);
}
