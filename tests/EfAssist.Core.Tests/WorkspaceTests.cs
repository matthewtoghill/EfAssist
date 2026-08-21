using EfAssist.Core;

namespace EfAssist.Core.Tests;

public class WorkspaceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "EfAssistTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    /// <summary>Replays canned <c>dotnet sln list</c> output without launching a process.</summary>
    private sealed class FakeRunner(int exitCode, params string[] rawLines) : IEfRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<EfResult> RunAsync(
            IReadOnlyList<string> args,
            string workingDirectory,
            IProgress<OutputLine>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(args);
            var lines = rawLines.Select(OutputLine.Parse).ToArray();
            return Task.FromResult(new EfResult(exitCode, lines, "fake", workingDirectory));
        }
    }

    private static readonly string[] SlnListOutput =
    [
        "Project(s)",
        "----------",
        @"src\Api\Api.csproj",
        @"src\Data\Data.csproj",
    ];

    private string Write(string relativePath, string contents)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
        return full;
    }

    private const string LibraryCsproj =
        """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";

    private static string CsprojWith(string extra) =>
        $"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>{extra}</PropertyGroup></Project>""";

    private const string WebCsproj =
        """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework><UserSecretsId>fixture</UserSecretsId></PropertyGroup></Project>""";

    [Fact]
    public async Task Uses_dotnet_sln_list_rather_than_parsing_the_solution_file()
    {
        var solution = Write("Thing.slnx", "<Solution />");
        Write(@"src\Api\Api.csproj", LibraryCsproj);
        Write(@"src\Data\Data.csproj", LibraryCsproj);
        var runner = new FakeRunner(0, SlnListOutput);

        var workspace = await Workspace.DiscoverAsync(solution, runner);

        Assert.Equal(["sln", solution, "list"], runner.Calls.Single());
        Assert.Equal(["Api", "Data"], workspace.Projects.Select(p => p.Name));
        Assert.All(workspace.Projects, p => Assert.True(Path.IsPathFullyQualified(p.Path)));
    }

    [Fact]
    public async Task Finds_the_solution_when_given_a_folder()
    {
        var solution = Write("Thing.slnx", "<Solution />");
        Write(@"src\Api\Api.csproj", LibraryCsproj);
        Write(@"src\Data\Data.csproj", LibraryCsproj);

        var workspace = await Workspace.DiscoverAsync(_root, new FakeRunner(0, SlnListOutput));

        Assert.Equal(solution, workspace.SolutionPath);
    }

    [Fact]
    public async Task Falls_back_to_globbing_when_sln_list_fails()
    {
        Write("Thing.slnx", "<Solution />");
        Write(@"src\Api\Api.csproj", LibraryCsproj);
        Write(@"src\Api\bin\Debug\Stale.csproj", LibraryCsproj);

        var workspace = await Workspace.DiscoverAsync(_root, new FakeRunner(1, "error:   nope"));

        // Build output is skipped: a copy of a project under bin/ is not a project to pick.
        Assert.Equal(["Api"], workspace.Projects.Select(p => p.Name));
    }

    [Fact]
    public async Task A_single_project_path_is_a_valid_workspace()
    {
        var project = Write(@"src\Data\Data.csproj", LibraryCsproj);

        var workspace = await Workspace.DiscoverAsync(project, new FakeRunner(0));

        Assert.Null(workspace.SolutionPath);
        Assert.Equal("Data", workspace.Projects.Single().Name);
    }

    [Fact]
    public async Task Suggests_the_project_referencing_EntityFrameworkCore_Design_as_startup()
    {
        Write("Thing.slnx", "<Solution />");
        Write(@"src\Api\Api.csproj", CsprojWith(
            """</PropertyGroup><ItemGroup><PackageReference Include="Microsoft.EntityFrameworkCore.Design" /></ItemGroup><PropertyGroup>"""));
        Write(@"src\Data\Data.csproj", LibraryCsproj);

        var workspace = await Workspace.DiscoverAsync(_root, new FakeRunner(0, SlnListOutput));

        Assert.Equal("Api", workspace.SuggestedStartupProject?.Name);
    }

    [Fact]
    public async Task A_runnable_app_beats_a_data_library_that_also_references_Design()
    {
        Write("Thing.slnx", "<Solution />");

        // The common layout: the data library carries the Design package and the migrations, and the
        // web project carries the configuration — appsettings.json and the user secrets holding the
        // real connection string. Suggesting the library here hands EF a design-time host with no
        // configuration, so the connection string comes back empty and every refresh fails.
        Write(@"src\Data\Data.csproj", CsprojWith(
            """</PropertyGroup><ItemGroup><PackageReference Include="Microsoft.EntityFrameworkCore.Design" /></ItemGroup><PropertyGroup>"""));
        Write(@"src\Data\Migrations\20260101000000_InitialCreate.cs", "// migration");
        Write(@"src\Api\Api.csproj", WebCsproj);

        var workspace = await Workspace.DiscoverAsync(_root, new FakeRunner(0, SlnListOutput));

        Assert.Equal("Api", workspace.SuggestedStartupProject?.Name);
        Assert.Equal("Data", workspace.SuggestedMigrationsProject?.Name);
    }

    [Fact]
    public async Task Falls_back_to_an_executable_when_nothing_references_Design()
    {
        Write("Thing.slnx", "<Solution />");
        Write(@"src\Api\Api.csproj", LibraryCsproj);
        Write(@"src\Data\Data.csproj", CsprojWith("<OutputType>Exe</OutputType>"));

        var workspace = await Workspace.DiscoverAsync(_root, new FakeRunner(0, SlnListOutput));

        Assert.Equal("Data", workspace.SuggestedStartupProject?.Name);
    }

    [Fact]
    public async Task Suggests_the_project_that_already_holds_migrations()
    {
        Write("Thing.slnx", "<Solution />");
        Write(@"src\Api\Api.csproj", CsprojWith(
            """</PropertyGroup><ItemGroup><PackageReference Include="Microsoft.EntityFrameworkCore.Design" /></ItemGroup><PropertyGroup>"""));
        Write(@"src\Data\Data.csproj", LibraryCsproj);
        Write(@"src\Data\Migrations\20260101000000_InitialCreate.cs", "// migration");

        var workspace = await Workspace.DiscoverAsync(_root, new FakeRunner(0, SlnListOutput));

        Assert.Equal("Api", workspace.SuggestedStartupProject?.Name);
        Assert.Equal("Data", workspace.SuggestedMigrationsProject?.Name);
    }

    [Fact]
    public async Task Migrations_project_defaults_to_the_startup_project_when_none_has_migrations_yet()
    {
        Write("Thing.slnx", "<Solution />");
        Write(@"src\Api\Api.csproj", CsprojWith("<OutputType>Exe</OutputType>"));
        Write(@"src\Data\Data.csproj", LibraryCsproj);

        var workspace = await Workspace.DiscoverAsync(_root, new FakeRunner(0, SlnListOutput));

        Assert.Equal("Api", workspace.SuggestedMigrationsProject?.Name);
    }
}
