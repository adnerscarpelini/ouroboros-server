namespace Ouroboros.IntegrationTests;

// Localiza a raiz do repositório a partir do diretório de saída dos testes, para os testes que
// executam scripts versionados (provisionamento e transferência de banco).
internal static class RepositoryRoot
{
    private static readonly Lazy<string> Root = new(Find);

    public static string Resolve(params string[] relative) =>
        Path.Combine([Root.Value, .. relative]);

    private static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ouroboros.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Ouroboros.slnx não foi encontrado acima do diretório de testes.");
    }
}
