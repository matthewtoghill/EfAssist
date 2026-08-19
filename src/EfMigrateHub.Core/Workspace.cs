namespace EfMigrateHub.Core;

public sealed record ProjectRef(string Name, string Path);

/// <param name="SuggestedStartupProject">A guess, not a decision. Every selection stays overridable.</param>
public sealed record WorkspaceInfo(
    string Path,
    string? SolutionPath,
    IReadOnlyList<ProjectRef> Projects,
    ProjectRef? SuggestedStartupProject,
    ProjectRef? SuggestedMigrationsProject);

/// <summary>
/// Finds the projects in a solution or folder, and guesses which are the startup and migrations
/// projects.
/// </summary>
public static class Workspace
{
    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];
    private static readonly string[] SolutionExtensions = [".slnx", ".sln"];

    /// <param name="path">A solution file, a project file, or a folder containing either.</param>
    public static async Task<WorkspaceInfo> DiscoverAsync(
        string path,
        IEfRunner runner,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);

        var extension = Path.GetExtension(path);

        if (ProjectExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return Describe(path, solutionPath: null, [ToProjectRef(path)]);
        }

        var solutionPath = SolutionExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? path
            : FindSolution(path);

        if (solutionPath is not null)
        {
            var fromSolution = await ListSolutionProjectsAsync(solutionPath, runner, cancellationToken)
                .ConfigureAwait(false);

            // An empty result means `dotnet sln list` failed or the solution genuinely has no
            // projects; either way the folder glob is a better answer than nothing.
            if (fromSolution.Count > 0)
            {
                return Describe(path, solutionPath, fromSolution);
            }
        }

        var root = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
        return Describe(path, solutionPath, GlobProjects(root));
    }

    private static string? FindSolution(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        // .slnx first: if a folder has both, the new format is the one being maintained.
        return SolutionExtensions
            .SelectMany(ext => Directory.EnumerateFiles(directory, "*" + ext))
            .FirstOrDefault();
    }

    private static async Task<List<ProjectRef>> ListSolutionProjectsAsync(
        string solutionPath,
        IEfRunner runner,
        CancellationToken cancellationToken)
    {
        // `dotnet sln list` reads both .sln and .slnx, so we never parse a solution format ourselves.
        var result = await runner.RunAsync(
            ["sln", solutionPath, "list"],
            Path.GetDirectoryName(solutionPath)!,
            progress: null,
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return [];
        }

        var solutionDirectory = Path.GetDirectoryName(solutionPath)!;

        // Output is a localised two-line header followed by relative project paths. Selecting lines
        // that look like project paths skips the header without depending on its wording.
        return result.Lines
            .Select(l => l.Text.Trim())
            .Where(IsProjectPath)
            .Select(relative => ToProjectRef(Path.GetFullPath(
                Path.Combine(solutionDirectory, relative))))
            .ToList();
    }

    private static List<ProjectRef> GlobProjects(string root) =>
        ProjectExtensions
            .SelectMany(ext => Directory.EnumerateFiles(root, "*" + ext, SearchOption.AllDirectories))
            .Where(p => !IsBuildOutput(p, root))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(ToProjectRef)
            .ToList();

    private static bool IsBuildOutput(string projectPath, string root)
    {
        var relative = Path.GetRelativePath(root, projectPath);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);

        return segments.Any(s =>
            s.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsProjectPath(string line) =>
        line.Length > 0 && ProjectExtensions.Any(ext => line.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    private static ProjectRef ToProjectRef(string path) =>
        new(Path.GetFileNameWithoutExtension(path), path);

    private static WorkspaceInfo Describe(
        string path,
        string? solutionPath,
        IReadOnlyList<ProjectRef> projects)
    {
        var startup = GuessStartupProject(projects);
        var migrations = GuessMigrationsProject(projects) ?? startup;
        return new WorkspaceInfo(path, solutionPath, projects, startup, migrations);
    }

    /// <summary>
    /// The startup project is the one EF builds and loads, so it needs
    /// Microsoft.EntityFrameworkCore.Design. Failing that, an executable is a better guess than a
    /// class library.
    /// </summary>
    private static ProjectRef? GuessStartupProject(IReadOnlyList<ProjectRef> projects)
    {
        // ponytail: substring match on the raw project file rather than an XML/MSBuild evaluation.
        // Deliberate — it also catches central package management, where the version lives in
        // Directory.Packages.props. Upgrade to MSBuild evaluation only if false positives appear.
        var withDesignPackage = projects.FirstOrDefault(p =>
            ReadProject(p).Contains("Microsoft.EntityFrameworkCore.Design", StringComparison.OrdinalIgnoreCase));

        if (withDesignPackage is not null)
        {
            return withDesignPackage;
        }

        var executable = projects.FirstOrDefault(p =>
        {
            var text = ReadProject(p);
            return text.Contains("<OutputType>Exe</OutputType>", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase);
        });

        return executable ?? projects.FirstOrDefault();
    }

    /// <summary>The migrations project is wherever migration files already live.</summary>
    private static ProjectRef? GuessMigrationsProject(IReadOnlyList<ProjectRef> projects) =>
        projects.FirstOrDefault(p =>
        {
            var directory = Path.GetDirectoryName(p.Path);
            if (directory is null)
            {
                return false;
            }

            var migrations = Path.Combine(directory, "Migrations");
            return Directory.Exists(migrations)
                && Directory.EnumerateFiles(migrations, "*.cs").Any();
        });

    private static string ReadProject(ProjectRef project)
    {
        try
        {
            return File.ReadAllText(project.Path);
        }
        catch (IOException)
        {
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
    }
}
