using System.Net.Mail;
using System.Text.RegularExpressions;

namespace Ouroboros.Modules.Identity.Domain.Users;

// Regra estrita de e-mail das contas, a mesma dos parâmetros de Identity. O MailAddress sozinho aceita
// nome de exibição, aspas, comentários, espaços, domínio literal e ponto final, e reescreve o endereço
// (por exemplo, "vic tim@example.com" vira "tim@example.com"): um link poderia ir para outra caixa, e
// um endereço equivalente ao de uma conta existente escaparia da verificação de e-mail já usado.
internal static partial class EmailAddresses
{
    // Recebe o e-mail já normalizado (User.NormalizeEmail).
    public static bool IsValid(string normalizedEmail) =>
        normalizedEmail.Length <= User.EmailMaxLength
        && MailAddress.TryCreate(normalizedEmail, out var address)
        && address.Address == normalizedEmail
        && string.IsNullOrEmpty(address.DisplayName)
        && LocalPart().IsMatch(address.User)
        && DomainName().IsMatch(address.Host);

    // Parte local em ASCII, sem aspas nem espaços.
    [GeneratedRegex(@"\A[a-z0-9._%+-]{1,64}\z")]
    private static partial Regex LocalPart();

    // Domínio DNS com pelo menos um ponto, sem ponto final nem literal de IP.
    [GeneratedRegex(@"\A(?=.{1,253}\z)([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}\z")]
    private static partial Regex DomainName();
}
