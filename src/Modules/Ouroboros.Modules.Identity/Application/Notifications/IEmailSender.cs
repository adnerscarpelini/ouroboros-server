namespace Ouroboros.Modules.Identity.Application.Notifications;

// Porta do módulo para envio de e-mail; o adaptador fica em Infrastructure. Os casos de uso chamam o
// envio só depois de gravar a transação: se falhar, o usuário refaz a solicitação (sem outbox).
internal interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

// Mensagem em texto simples. O corpo pode conter links com tokens: nunca vai para o log.
internal sealed record EmailMessage
{
    public EmailMessage(string to, string subject, string body)
    {
        To = SingleLine(to, nameof(to));
        Subject = SingleLine(subject, nameof(subject));
        Body = body;
    }

    public string To { get; }

    public string Subject { get; }

    public string Body { get; }

    // Destinatário e assunto viram cabeçalhos: uma quebra de linha permitiria injetar outros
    // cabeçalhos (por exemplo, um Bcc), em qualquer adaptador.
    private static string SingleLine(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.AsSpan().IndexOfAny('\r', '\n') >= 0
            ? throw new ArgumentException("O valor não pode conter quebras de linha.", parameterName)
            : value;
    }
}
