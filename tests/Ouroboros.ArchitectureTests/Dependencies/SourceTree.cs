using System.Reflection;
using System.Runtime.Loader;

namespace Ouroboros.ArchitectureTests.Dependencies;

// Os assemblies compilados de todos os projetos de src e o localizador que os classifica. O csproj de testes
// grava em architecture-assemblies.txt o caminho de cada um (ver WriteArchitectureAssemblyManifest).
internal sealed class SourceTree : ICodeLocator
{
    private const string ManifestFile = "architecture-assemblies.txt";

    private readonly Dictionary<string, ProjectInfo> _projects;

    private readonly Dictionary<string, string> _paths;

    private SourceTree(SourceProjects projects, IReadOnlyList<AssemblyScan> scans, Dictionary<string, string> paths)
    {
        Projects = projects;
        Scans = scans;
        _paths = paths;
        _projects = projects.Projects.ToDictionary(project => project.Name);
    }

    public static SourceTree Current { get; } = Load();

    public SourceProjects Projects { get; }

    public IReadOnlyList<AssemblyScan> Scans { get; }

    public IEnumerable<ProjectInfo> Services => Projects.Projects.Where(project => project.Kind == ProjectKind.Service);

    public CodeLocation Locate(TypeId type)
    {
        if (!_projects.TryGetValue(type.Assembly, out var project))
        {
            return new CodeLocation(CodeKind.External, null, null, type.Assembly);
        }

        var kind = project.Kind switch
        {
            ProjectKind.Service => CodeKind.Service,
            ProjectKind.Executable => CodeKind.Executable,
            _ => CodeKind.Shared,
        };
        // Só bibliotecas seguem <assembly>.<Camada>; os namespaces de um executável (controllers, comandos) são livres.
        var layer = project.Kind == ProjectKind.Executable
            ? null
            : DependencyRules.LayerOf(type.Namespace, project.Name, sharedCode: project.Kind == ProjectKind.Shared);
        return new CodeLocation(kind, project.Service, layer, project.Name);
    }

    // Para os testes que usam reflexão: um serviço que nenhum host referencia ainda não está no contexto de carga dos testes,
    // então é carregado do caminho compilado.
    public Assembly Load(string assemblyName)
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == assemblyName);
        return loaded ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(_paths[assemblyName]);
    }

    // Assemblies que o manifesto lista e que não correspondem a nenhum projeto de src, ou o contrário.
    public IEnumerable<string> MissingAssemblies() => Projects.Projects
        .Select(project => project.Name)
        .Except(Scans.Select(scan => scan.Name))
        .Select(name => $"{name}: sem assembly compilado no manifesto")
        .Concat(Scans.Select(scan => scan.Name).Except(Projects.Projects.Select(project => project.Name)).Select(name => $"{name}: assembly fora de src"));

    private static SourceTree Load()
    {
        var manifest = Path.Combine(AppContext.BaseDirectory, ManifestFile);
        if (!File.Exists(manifest))
        {
            throw new FileNotFoundException($"{ManifestFile} não encontrado; compile o projeto de testes de arquitetura.", manifest);
        }

        var paths = File.ReadAllLines(manifest).Where(line => line.Length > 0).ToDictionary(path => Path.GetFileNameWithoutExtension(path)!, path => path);
        var scans = paths.Values
            .Select(AssemblyScanner.Scan)
            .OrderBy(scan => scan.Name, StringComparer.Ordinal)
            .ToList();
        return new SourceTree(SourceProjects.Current, scans, paths);
    }
}
