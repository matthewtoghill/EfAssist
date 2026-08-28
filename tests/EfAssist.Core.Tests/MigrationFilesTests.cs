using EfAssist.Core;

namespace EfAssist.Core.Tests;

/// <summary>
/// Finding the file behind a migration is a convention-based search, so these pin the convention:
/// which file wins, which directories are never looked in, and what happens when nothing matches.
/// </summary>
public class MigrationFilesTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "EfAssistTests", Guid.NewGuid().ToString("N"));

    private string ProjectPath => Path.Combine(_root, "Data.csproj");

    public MigrationFilesTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(ProjectPath, "<Project />");
    }

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

    private string Write(string relativePath, string content = "// migration")
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Finds_the_migration_in_the_conventional_folder()
    {
        var expected = Write(@"Migrations\20260101000000_InitialCreate.cs");

        Assert.Equal(
            expected,
            MigrationFiles.FindSource(ProjectPath, "20260101000000_InitialCreate"));
    }

    [Fact]
    public void Finds_a_migration_written_to_a_custom_output_directory()
    {
        // --output-dir puts the file wherever the user asked, and nothing records where that was.
        var expected = Write(@"Data\Schema\History\20260101000000_InitialCreate.cs");

        Assert.Equal(
            expected,
            MigrationFiles.FindSource(ProjectPath, "20260101000000_InitialCreate"));
    }

    [Fact]
    public void Ignores_the_designer_file()
    {
        Write(@"Migrations\20260101000000_InitialCreate.Designer.cs");

        // The Designer holds the model snapshot, not Up and Down, and its name does not match.
        Assert.Null(MigrationFiles.FindSource(ProjectPath, "20260101000000_InitialCreate"));
    }

    [Fact]
    public void Never_returns_a_copy_from_a_build_output_folder()
    {
        Write(@"obj\Debug\net10.0\Migrations\20260101000000_InitialCreate.cs", "// stale copy");
        var expected = Write(@"Migrations\20260101000000_InitialCreate.cs");

        Assert.Equal(
            expected,
            MigrationFiles.FindSource(ProjectPath, "20260101000000_InitialCreate"));
    }

    [Fact]
    public void Returns_null_when_only_a_build_output_copy_exists()
    {
        Write(@"bin\Debug\net10.0\20260101000000_InitialCreate.cs", "// stale copy");

        Assert.Null(MigrationFiles.FindSource(ProjectPath, "20260101000000_InitialCreate"));
    }

    [Fact]
    public void Returns_null_when_nothing_matches()
    {
        Write(@"Migrations\20260101000000_SomethingElse.cs");

        Assert.Null(MigrationFiles.FindSource(ProjectPath, "20260101000000_InitialCreate"));
    }

    [Theory]
    [InlineData("", "20260101000000_InitialCreate")]
    [InlineData(@"C:\repo\Data.csproj", "")]
    public void Returns_null_rather_than_throwing_on_empty_input(string project, string id) =>
        Assert.Null(MigrationFiles.FindSource(project, id));

    [Fact]
    public void Accepts_a_project_directory_as_well_as_a_project_file()
    {
        var expected = Write(@"Migrations\20260101000000_InitialCreate.cs");

        Assert.Equal(
            expected,
            MigrationFiles.FindSource(_root, "20260101000000_InitialCreate"));
    }

    [Fact]
    public void Missing_project_directory_is_not_an_error()
    {
        var missing = Path.Combine(_root, "gone", "Data.csproj");

        Assert.Null(MigrationFiles.FindSource(missing, "20260101000000_InitialCreate"));
    }

    // ---- Script cache path ----

    [Fact]
    public void Script_cache_path_is_under_temp_and_named_for_the_migration()
    {
        var path = MigrationFiles.ScriptCachePath(ProjectPath, "BlogContext", "20260101000000_Init");

        Assert.StartsWith(Path.GetTempPath(), path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("20260101000000_Init.sql", Path.GetFileName(path));
    }

    [Fact]
    public void Script_cache_path_is_stable_for_the_same_project_and_context()
    {
        Assert.Equal(
            MigrationFiles.ScriptCachePath(ProjectPath, "BlogContext", "20260101000000_Init"),
            MigrationFiles.ScriptCachePath(ProjectPath, "BlogContext", "20260101000000_Init"));
    }

    [Fact]
    public void Different_contexts_and_projects_do_not_share_a_cache_folder()
    {
        // Two contexts in one solution can have identically named migrations, and reading one
        // context's SQL under the other's name would be a silently wrong answer.
        var blog = MigrationFiles.ScriptCachePath(ProjectPath, "BlogContext", "20260101000000_Init");
        var audit = MigrationFiles.ScriptCachePath(ProjectPath, "AuditContext", "20260101000000_Init");
        var otherProject = MigrationFiles.ScriptCachePath(
            Path.Combine(_root, "Other.csproj"), "BlogContext", "20260101000000_Init");

        Assert.NotEqual(blog, audit);
        Assert.NotEqual(blog, otherProject);
    }

    // ---- Update preview path ----

    [Fact]
    public void An_update_preview_path_names_its_range_and_shares_the_preview_folder()
    {
        var path = MigrationFiles.UpdatePreviewPath(ProjectPath, "BlogContext", "Init", "AddUrl");

        Assert.Equal("update_Init_to_AddUrl.sql", Path.GetFileName(path));
        Assert.Equal(
            Path.GetDirectoryName(MigrationFiles.ScriptCachePath(ProjectPath, "BlogContext", "Init")),
            Path.GetDirectoryName(path));
    }

    [Fact]
    public void An_update_preview_path_spells_out_an_absent_end_of_the_range()
    {
        // Null means "from the empty database" and "to the latest". Writing both of those to the same
        // file name as some other range would mean reading one range's SQL as another's.
        Assert.Equal(
            "update_0_to_latest.sql",
            Path.GetFileName(MigrationFiles.UpdatePreviewPath(ProjectPath, "BlogContext", null, null)));

        Assert.NotEqual(
            MigrationFiles.UpdatePreviewPath(ProjectPath, "BlogContext", "A", null),
            MigrationFiles.UpdatePreviewPath(ProjectPath, "BlogContext", null, "A"));
    }

    [Fact]
    public void An_update_preview_cannot_collide_with_a_single_migration_and_survives_a_stray_separator()
    {
        Assert.NotEqual(
            MigrationFiles.ScriptCachePath(ProjectPath, "BlogContext", "Init"),
            MigrationFiles.UpdatePreviewPath(ProjectPath, "BlogContext", "Init", null));

        // Migration names are C# identifiers, so this should never happen — but these values reach a
        // path, and a path is not the place to discover that an assumption was wrong.
        var path = MigrationFiles.UpdatePreviewPath(ProjectPath, "BlogContext", @"..\evil", "x");
        Assert.Equal(
            Path.GetDirectoryName(MigrationFiles.ScriptCachePath(ProjectPath, "BlogContext", "Init")),
            Path.GetDirectoryName(path));
    }
}
