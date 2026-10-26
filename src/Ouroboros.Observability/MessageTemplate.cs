using System.Globalization;
using System.Text;

namespace Ouroboros.Observability;

// Renderiza um template de log ("Processamento de {MessageId}: {State}.") com valores já sanitizados. Chaves
// duplas ({{ e }}) são literais; um nome sem valor correspondente permanece como no template.
internal static class MessageTemplate
{
    internal static string Render(string template, IReadOnlyList<KeyValuePair<string, object?>> values)
    {
        var text = new StringBuilder(template.Length + 64);
        for (var index = 0; index < template.Length; index++)
        {
            var character = template[index];
            if (character is '{' or '}' && index + 1 < template.Length && template[index + 1] == character)
            {
                text.Append(character);
                index++;
                continue;
            }
            var end = character == '{' ? template.IndexOf('}', index + 1) : -1;
            if (end < 0) { text.Append(character); continue; }
            var hole = template.AsSpan(index + 1, end - index - 1);
            var separator = hole.IndexOfAny(',', ':');
            var name = (separator < 0 ? hole : hole[..separator]).ToString();
            var format = separator >= 0 && hole[separator] == ':' ? hole[(separator + 1)..].ToString() : null;
            var match = values.FirstOrDefault(pair => pair.Key == name);
            if (match.Key is null) { text.Append(template, index, end - index + 1); }
            else
            {
                text.Append(match.Value switch
                {
                    null => "(null)",
                    IFormattable formattable => formattable.ToString(format, CultureInfo.InvariantCulture),
                    var value => value.ToString(),
                });
            }
            index = end;
        }
        return text.ToString();
    }
}
