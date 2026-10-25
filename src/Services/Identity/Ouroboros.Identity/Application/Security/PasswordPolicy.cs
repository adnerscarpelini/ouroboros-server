using System.Reflection;
using System.Text;

namespace Ouroboros.Identity.Application.Security;

// Política de senha (NIST SP 800-63B-4): só tamanho, lista de senhas comuns e contexto do usuário,
// sem regras de composição. O tamanho é contado em code points depois da normalização NFKC; senha
// acima do limite é recusada, nunca truncada.
internal static class PasswordPolicy
{
    public const int MinimumLength = 15;
    public const int MaximumLength = 128;
    public const int MinimumEmailLocalPartToCheck = 4;

    private const string ProductName = "ouroboros";

    private static readonly Lazy<HashSet<string>> s_commonPasswords = new(LoadCommonPasswords);

    // Mesma normalização usada antes de gerar e de verificar o hash, para que a senha digitada com
    // outra forma Unicode (por exemplo, acento composto) continue valendo.
    public static string Normalize(string password) => password.Normalize(NormalizationForm.FormKC);

    // Devolve a regra violada, ou null quando a senha é aceita.
    public static string? Validate(string password, string email)
    {
        var normalized = Normalize(password);
        var length = normalized.EnumerateRunes().Count();
        if (length < MinimumLength)
        {
            return $"A senha deve ter pelo menos {MinimumLength} caracteres.";
        }

        if (length > MaximumLength)
        {
            return $"A senha deve ter no máximo {MaximumLength} caracteres.";
        }

        var lower = normalized.ToLowerInvariant();
        if (s_commonPasswords.Value.Contains(lower))
        {
            return "A senha está na lista de senhas comuns. Escolha outra.";
        }

        if (lower.Contains(ProductName, StringComparison.Ordinal))
        {
            return "A senha não pode conter o nome do sistema.";
        }

        var localPart = email.Split('@')[0].Trim().ToLowerInvariant();
        return localPart.Length >= MinimumEmailLocalPartToCheck && lower.Contains(localPart, StringComparison.Ordinal)
            ? "A senha não pode conter a parte local do e-mail."
            : null;
    }

    // Lista local (recurso embutido), sem consulta externa. Comparada sem diferenciar maiúsculas.
    private static HashSet<string> LoadCommonPasswords()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Ouroboros.Identity.Application.Security.common-passwords.txt")
            ?? throw new InvalidOperationException("Lista de senhas comuns não encontrada.");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var passwords = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            var entry = line.Trim();
            if (entry.Length > 0 && !entry.StartsWith('#'))
            {
                passwords.Add(Normalize(entry).ToLowerInvariant());
            }
        }

        return passwords;
    }
}
