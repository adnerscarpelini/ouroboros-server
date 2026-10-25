using System.Globalization;
using System.Net.Mail;
using System.Text.RegularExpressions;

namespace Ouroboros.Modules.Identity.Domain.Parameters;

// Regras de validação do valor de um parâmetro, conforme o tipo e o padrão (regex) gravados na tabela.
// As âncoras usam \A e \z: em regex .NET, $ também casa antes de um \n final, o que deixaria passar
// "adnerscarpelini.com\n" e quebraria as comparações exatas de quem lê o parâmetro.
internal static partial class ParameterValueRules
{
    // Devolve a regra violada, ou null quando o valor é válido.
    public static string? Validate(ParameterDataType dataType, string? validationPattern, string value)
    {
        if (value.Any(IsInvisibleOrControl))
        {
            return "não pode conter quebras de linha, caracteres de controle nem caracteres invisíveis";
        }

        var typeError = dataType switch
        {
            ParameterDataType.String => null,
            ParameterDataType.Integer => long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
                ? null
                : "deve ser um número inteiro",
            ParameterDataType.Boolean => value is "true" or "false" ? null : "deve ser 'true' ou 'false'",
            ParameterDataType.Email => IsEmail(value) ? null : "deve ser um e-mail válido, em minúsculas",
            ParameterDataType.EmailList => IsEmailList(value)
                ? null
                : "deve ser uma lista de e-mails válidos, em minúsculas, separados por vírgula, sem repetições",
            ParameterDataType.Domain => DomainName().IsMatch(value) ? null : "deve ser um domínio válido, sem '@' e em minúsculas",
            _ => "tem um tipo desconhecido",
        };
        if (typeError is not null)
        {
            return typeError;
        }

        return validationPattern is not null
            && !Regex.IsMatch(value, $@"\A(?:{validationPattern})\z", RegexOptions.None, TimeSpan.FromSeconds(1))
            ? $"não respeita o padrão '{validationPattern}'"
            : null;
    }

    // Lista de e-mails separada por vírgula; espaços em volta de cada e-mail são ignorados.
    public static IReadOnlyList<string> SplitEmailList(string value) =>
        value.Split(',', StringSplitOptions.TrimEntries);

    // Parte local em ASCII, sem aspas; host com domínio válido.
    private static bool IsEmail(string value) =>
        value == value.Trim()
        && value == value.ToLowerInvariant()
        && MailAddress.TryCreate(value, out var address)
        && address.Address == value
        && LocalPart().IsMatch(address.User)
        && DomainName().IsMatch(address.Host);

    // Controle (\n, \t), formato (por exemplo, zero-width space) e separadores de linha e parágrafo.
    private static bool IsInvisibleOrControl(char character) =>
        char.IsControl(character)
        || CharUnicodeInfo.GetUnicodeCategory(character)
            is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    [GeneratedRegex(@"\A[a-z0-9._%+-]{1,64}\z")]
    private static partial Regex LocalPart();

    private static bool IsEmailList(string value)
    {
        var emails = SplitEmailList(value);
        return emails.All(IsEmail) && emails.Distinct(StringComparer.Ordinal).Count() == emails.Count;
    }

    [GeneratedRegex(@"\A(?=.{1,253}\z)([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}\z")]
    private static partial Regex DomainName();
}
