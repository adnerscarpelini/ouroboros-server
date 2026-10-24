using System.Xml.Linq;

namespace Ouroboros.ArchitectureTests.Dependencies;

internal enum ProjectKind
{
    // Biblioteca de um serviço (domínio, aplicação, infraestrutura e contratos).
    Service,

    // Projeto que produz um processo: API, worker ou host agregador legado.
    Executable,

    // Código técnico compartilhado, como BuildingBlocks e Hosting.
    Shared,
}

// Service é o serviço proprietário, deduzido do caminho (src/Services/<Serviço>/... ou src/Modules/Ouroboros.Modules.<Serviço>).
// Um executável fora dessas pastas, como os hosts agregadores, não pertence a nenhum serviço.
internal sealed record ProjectInfo(string Name, ProjectKind Kind, string? Service, IReadOnlyList<string> References);

// Descobre os projetos pela árvore de src, e não por uma lista mantida à mão: um serviço, API ou worker novo
// entra nas regras assim que o .csproj existe.
internal sealed class SourceProjects
{
    private const string SolutionFile = "Ouroboros.slnx";

    private SourceProjects(string root, IReadOnlyList<ProjectInfo> projects, IReadOnlyList<string> danglingReferences, IReadOnlyList<string> solutionProblems)
    {
        Root = root;
        Projects = projects;
        DanglingReferences = danglingReferences;
        SolutionProblems = solutionProblems;
    }

    public static SourceProjects Current { get; } = Discover();

    public string Root { get; }

    public IReadOnlyList<ProjectInfo> Projects { get; }

    // "Projeto -> caminho" de ProjectReference que não aponta para um projeto existente de src.
    public IReadOnlyList<string> DanglingReferences { get; }

    // Divergências entre a solution e os projetos de src.
    public IReadOnlyList<string> SolutionProblems { get; }

    public ProjectInfo? ByAssembly(string assemblyName) => Projects.FirstOrDefault(project => project.Name == assemblyName);

    private static SourceProjects Discover()
    {
        var root = FindRoot();
        var source = Path.Combine(root, "src");
        var files = Directory.EnumerateFiles(source, "*.csproj", SearchOption.AllDirectories)
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToList();
        var byPath = files.ToDictionary(file => Path.GetFullPath(file), file => file, StringComparer.OrdinalIgnoreCase);

        var projects = new List<ProjectInfo>();
        var dangling = new List<string>();
        foreach (var file in files)
        {
            var document = XDocument.Load(file);
            var references = new List<string>();
            foreach (var include in document.Descendants("ProjectReference").Select(reference => reference.Attribute("Include")?.Value).OfType<string>())
            {
                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, include.Replace('\\', '/')));
                if (byPath.ContainsKey(target))
                {
                    references.Add(NameOf(target));
                }
                else
                {
                    dangling.Add($"{NameOf(file)} -> {include}");
                }
            }

            var relative = Path.GetRelativePath(source, file).Replace('\\', '/').Split('/');
            projects.Add(new ProjectInfo(NameOf(file), KindOf(document, relative), ServiceOf(relative), references));
        }

        return new SourceProjects(root, projects, dangling, CompareWithSolution(root, byPath.Keys));
    }

    // Convenção do repositório: o assembly tem o nome do arquivo do projeto.
    private static string NameOf(string projectFile) => Path.GetFileNameWithoutExtension(projectFile);

    private static ProjectKind KindOf(XDocument document, string[] relativePath)
    {
        var sdk = document.Root?.Attribute("Sdk")?.Value ?? "";
        var outputType = document.Descendants("OutputType").FirstOrDefault()?.Value ?? "";
        if (sdk.EndsWith(".Web", StringComparison.Ordinal) || sdk.EndsWith(".Worker", StringComparison.Ordinal)
            || outputType is "Exe" or "WinExe")
        {
            return ProjectKind.Executable;
        }

        return ServiceOf(relativePath) is null ? ProjectKind.Shared : ProjectKind.Service;
    }

    private static string? ServiceOf(string[] relativePath) => relativePath switch
    {
        ["Services", var service, _, ..] => service,
        ["Modules", var module, _, ..] => module[(module.LastIndexOf('.') + 1)..],
        _ => null,
    };

    private static List<string> CompareWithSolution(string root, IEnumerable<string> projectFiles)
    {
        var solutionPaths = XDocument.Load(Path.Combine(root, SolutionFile)).Descendants("Project")
            .Select(project => project.Attribute("Path")?.Value)
            .OfType<string>()
            .Select(path => Path.GetFullPath(Path.Combine(root, path.Replace('\\', '/'))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceFiles = projectFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return
        [
            .. sourceFiles.Except(solutionPaths).Select(path => $"{Path.GetRelativePath(root, path)} está em src, mas não na solution"),
            .. solutionPaths.Where(path => !File.Exists(path)).Select(path => $"{Path.GetRelativePath(root, path)} está na solution, mas não existe"),
        ];
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFile)))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"{SolutionFile} não encontrado acima de {AppContext.BaseDirectory}.");
    }
}
