using System.Reflection;

namespace Ouroboros.Observability;

// Identifica o executável nos diagnósticos. Não é dado de negócio nem identidade de usuário.
public sealed record ServiceIdentity(string Name, string Version)
{
    internal static ServiceIdentity From(string name, Assembly assembly)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || !name.All(character => char.IsAsciiLetterLower(character) || character == '-'))
        {
            throw new ArgumentException("Nome de serviço inválido.", nameof(name));
        }
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
        return new ServiceIdentity(name, version);
    }
}
