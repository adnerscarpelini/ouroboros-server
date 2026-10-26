using System.Globalization;
using System.Text.Json;

namespace Ouroboros.Observability;

internal static class JsonValues
{
    // Números e booleanos como JSON; os demais valores (já sanitizados) como texto em cultura invariante.
    internal static void Write(Utf8JsonWriter json, string key, object? value)
    {
        switch (value)
        {
            case null: json.WriteNull(key); break;
            case bool flag: json.WriteBoolean(key, flag); break;
            case int or long or short or byte or uint or ushort or sbyte: json.WriteNumber(key, Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            case ulong number: json.WriteNumber(key, number); break;
            case decimal number: json.WriteNumber(key, number); break;
            case double or float when double.IsFinite(Convert.ToDouble(value, CultureInfo.InvariantCulture)):
                json.WriteNumber(key, Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            case IFormattable formattable: json.WriteString(key, formattable.ToString(null, CultureInfo.InvariantCulture)); break;
            default: json.WriteString(key, value.ToString()); break;
        }
    }
}
