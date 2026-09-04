using EfAssist.App.ViewModels;
using EfAssist.Core;

namespace EfAssist.Core.Tests;

/// <summary>
/// The detail pane beside the migrations list. The parts worth pinning are the ones that decide
/// whether a build happens and whether what is on screen still belongs to the selected migration.
/// </summary>
public class MigrationDetailViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "EfAssistTests", Guid.NewGuid().ToString("N"));

    private readonly List<MigrationInfo> _ordered =
    [
        new("20260101000000_InitialCreate", "InitialCreate", "InitialCreate", true),
        new("20260101000001_AddBlogUrl", "AddBlogUrl", "AddBlogUrl", false),
    ];

    public MigrationDetailViewModelTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Migrations"));
        File.WriteAllText(Path.Combine(_root, "Data.csproj"), "<Project />");
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

    private EfTarget Target => new(
        Project: Path.Combine(_root, "Data.csproj"),
        StartupProject: Path.Combine(_root, "Api.csproj"),
        Context: "BlogContext");

    private void WriteMigration(string id, string body) =>
        File.WriteAllText(Path.Combine(_root, "Migrations", id + ".cs"), body);

    /// <summary>Writes the SQL the CLI was asked for straight to the requested --output path.</summary>
    private sealed class FakeEf : IEfRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public bool Fails { get; set; }

        public Task<EfResult> RunAsync(
            IReadOnlyList<string> args,
            string workingDirectory,
            IProgress<OutputLine>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(args);

            if (Fails)
            {
                return Task.FromResult(new EfResult(
                    1, [new OutputLine(OutputChannel.Error, "script failed")], "fake", "."));
            }

            var output = OutputPath(args);
            var range = string.Join("-to-", args.Skip(3).Take(2));
            File.WriteAllText(output, $"-- SQL for {range}");

            return Task.FromResult(new EfResult(0, [], "fake", "."));
        }
    }

    /// <summary>The path the CLI was told to write to, which is the only place the SQL exists.</summary>
    private static string OutputPath(IReadOnlyList<string> args) =>
        args[args.ToList().IndexOf("--output") + 1];

    private (MigrationDetailViewModel Detail, FakeEf Runner) Build(
        EfTarget? target = null,
        Func<bool>? idempotentRequested = null,
        Func<bool>? canUseIdempotent = null,
        Func<Task>? ensureProviderKnownAsync = null)
    {
        var runner = new FakeEf();
        var session = new CommandSession(runner) { PostToUiThread = action => action() };
        var detail = new MigrationDetailViewModel(
            session,
            () => target ?? Target,
            () => _ordered,
            idempotentRequested,
            canUseIdempotent,
            ensureProviderKnownAsync);

        return (detail, runner);
    }

    private MigrationRow Row(int index) => new(index + 1, _ordered[index]);

    private static IReadOnlyList<string> ScriptCall(FakeEf runner) =>
        runner.Calls.Last(a => string.Join(' ', a.Skip(1).Take(2)) == "migrations script");

    // ---- Source ----

    [Fact]
    public void Selecting_a_migration_reads_its_source_without_running_anything()
    {
        WriteMigration("20260101000000_InitialCreate", "public partial class InitialCreate { }");
        var (detail, runner) = Build();

        detail.Show(Row(0));

        Assert.Equal("public partial class InitialCreate { }", detail.Source);
        Assert.Equal(detail.Source, detail.Text);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void A_missing_source_file_explains_itself_rather_than_showing_an_empty_pane()
    {
        var (detail, _) = Build();

        detail.Show(Row(0));

        Assert.True(detail.HasProblem);
        Assert.Contains("20260101000000_InitialCreate.cs", detail.Problem);
        Assert.False(detail.HasText);
        Assert.False(detail.OpenCommand.CanExecute(null));
    }

    [Fact]
    public void Deselecting_clears_the_pane()
    {
        WriteMigration("20260101000000_InitialCreate", "// source");
        var (detail, _) = Build();
        detail.Show(Row(0));

        detail.Show(null);

        Assert.False(detail.HasMigration);
        Assert.False(detail.HasText);
        Assert.False(detail.ShowSqlCommand.CanExecute(null));
    }

    // ---- SQL ----

    [Fact]
    public async Task The_first_migration_is_scripted_from_the_empty_database()
    {
        var (detail, runner) = Build();
        detail.Show(Row(0));

        await detail.ShowSqlCommand.ExecuteAsync(null);

        // "0" is EF's name for the empty database. Without it the CLI would read the id as a "from".
        Assert.Equal(
            ["ef", "migrations", "script", "0", "20260101000000_InitialCreate"],
            ScriptCall(runner).Take(5));
        Assert.Equal("-- SQL for 0-to-20260101000000_InitialCreate", detail.Sql);
        Assert.True(detail.IsShowingSql);
        Assert.Equal(detail.Sql, detail.Text);
    }

    [Fact]
    public async Task A_later_migration_is_scripted_from_the_one_before_it()
    {
        var (detail, runner) = Build();
        detail.Show(Row(1));

        await detail.ShowSqlCommand.ExecuteAsync(null);

        Assert.Equal(
            ["ef", "migrations", "script", "20260101000000_InitialCreate", "20260101000001_AddBlogUrl"],
            ScriptCall(runner).Take(5));
    }

    [Fact]
    public async Task The_script_is_written_somewhere_temporary_not_into_the_project()
    {
        var (detail, runner) = Build();
        detail.Show(Row(0));

        await detail.ShowSqlCommand.ExecuteAsync(null);

        var output = OutputPath(ScriptCall(runner));

        Assert.StartsWith(Path.GetTempPath(), output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_root, output);
    }

    [Fact]
    public async Task Regenerating_the_same_migration_reuses_the_cached_sql()
    {
        var (detail, runner) = Build();
        detail.Show(Row(0));
        await detail.ShowSqlCommand.ExecuteAsync(null);

        detail.ShowSourceCommand.Execute(null);
        await detail.ShowSqlCommand.ExecuteAsync(null);

        // A build per press would make flipping between the two views unusable.
        Assert.Single(runner.Calls);
        Assert.True(detail.IsShowingSql);
    }

    [Fact]
    public async Task Reselecting_a_migration_shows_its_cached_sql_without_rebuilding()
    {
        var (detail, runner) = Build();
        detail.Show(Row(0));
        await detail.ShowSqlCommand.ExecuteAsync(null);

        detail.Show(Row(1));
        detail.Show(Row(0));
        await detail.ShowSqlCommand.ExecuteAsync(null);

        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Selecting_another_migration_never_shows_the_previous_migrations_sql()
    {
        var (detail, _) = Build();
        detail.Show(Row(0));
        await detail.ShowSqlCommand.ExecuteAsync(null);

        detail.Show(Row(1));

        // The second migration has no SQL yet, so the pane drops back to the source rather than
        // leaving the first migration's SQL on screen under the second migration's name.
        Assert.False(detail.IsShowingSql);
        Assert.Equal("", detail.Sql);
    }

    [Fact]
    public async Task Reloading_the_migrations_list_drops_the_generated_sql()
    {
        var (detail, runner) = Build();
        detail.Show(Row(0));
        await detail.ShowSqlCommand.ExecuteAsync(null);

        // The files behind the list may have changed, so nothing generated from them survives.
        detail.InvalidateSql();
        await detail.ShowSqlCommand.ExecuteAsync(null);

        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public async Task A_failed_generation_leaves_the_source_showing()
    {
        WriteMigration("20260101000000_InitialCreate", "// source");
        var (detail, runner) = Build();
        runner.Fails = true;
        detail.Show(Row(0));

        await detail.ShowSqlCommand.ExecuteAsync(null);

        Assert.False(detail.IsShowingSql);
        Assert.Equal("// source", detail.Text);
    }

    [Fact]
    public async Task A_failure_is_not_cached_as_a_result()
    {
        var (detail, runner) = Build();
        runner.Fails = true;
        detail.Show(Row(0));
        await detail.ShowSqlCommand.ExecuteAsync(null);

        runner.Fails = false;
        await detail.ShowSqlCommand.ExecuteAsync(null);

        Assert.True(detail.IsShowingSql);
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public void Sql_cannot_be_generated_without_a_project_and_context()
    {
        var detail = new MigrationDetailViewModel(
            new CommandSession(new FakeEf()) { PostToUiThread = action => action() },
            () => null,
            () => _ordered);

        detail.Show(Row(0));

        Assert.False(detail.ShowSqlCommand.CanExecute(null));
        Assert.Contains("migrations project", detail.Problem);
    }

    // ---- Idempotent option ----

    [Fact]
    public async Task Idempotent_is_passed_when_the_shared_option_is_ticked_and_supported()
    {
        var (detail, runner) = Build(idempotentRequested: () => true, canUseIdempotent: () => true);
        detail.Show(Row(0));

        await detail.ShowSqlCommand.ExecuteAsync(null);

        Assert.Contains("--idempotent", ScriptCall(runner));
    }

    [Fact]
    public async Task Idempotent_is_not_passed_when_the_provider_does_not_support_it()
    {
        // Matches the Script tab's gate: ticked is not enough on its own, the provider has to allow it.
        var (detail, runner) = Build(idempotentRequested: () => true, canUseIdempotent: () => false);
        detail.Show(Row(0));

        await detail.ShowSqlCommand.ExecuteAsync(null);

        Assert.DoesNotContain("--idempotent", ScriptCall(runner));
    }

    [Fact]
    public async Task The_provider_is_probed_before_the_first_generation()
    {
        var probed = false;
        var (detail, _) = Build(ensureProviderKnownAsync: () =>
        {
            probed = true;
            return Task.CompletedTask;
        });
        detail.Show(Row(0));

        await detail.ShowSqlCommand.ExecuteAsync(null);

        Assert.True(probed);
    }

    [Fact]
    public async Task Idempotent_and_non_idempotent_sql_for_the_same_migration_are_cached_separately()
    {
        var idempotent = false;
        var (detail, runner) = Build(idempotentRequested: () => idempotent, canUseIdempotent: () => true);
        detail.Show(Row(0));

        await detail.ShowSqlCommand.ExecuteAsync(null);
        Assert.DoesNotContain("--idempotent", ScriptCall(runner));

        idempotent = true;
        await detail.ShowSqlCommand.ExecuteAsync(null);

        // A second CLI call, not the non-idempotent result served back under the ticked option.
        Assert.Equal(2, runner.Calls.Count);
        Assert.Contains("--idempotent", ScriptCall(runner));

        idempotent = false;
        await detail.ShowSqlCommand.ExecuteAsync(null);

        // Flipping back finds the first result still cached rather than rebuilding a third time.
        Assert.Equal(2, runner.Calls.Count);
    }

    // ---- Direction ----

    [Fact]
    public async Task The_down_script_runs_the_range_backwards()
    {
        var (detail, runner) = Build();
        detail.Show(Row(1));

        detail.ScriptDown = true;
        await detail.ShowSqlCommand.ExecuteAsync(null);

        // The same two migrations as the up script, the other way round: from this one back to the
        // one before it, which is what runs its Down method and nothing else.
        Assert.Equal(
            ["ef", "migrations", "script", "20260101000001_AddBlogUrl", "20260101000000_InitialCreate"],
            ScriptCall(runner).Take(5));
        Assert.True(detail.IsShowingSql);
    }

    [Fact]
    public async Task Rolling_back_the_first_migration_scripts_down_to_the_empty_database()
    {
        var (detail, runner) = Build();
        detail.Show(Row(0));

        detail.ScriptDown = true;
        await detail.ShowSqlCommand.ExecuteAsync(null);

        Assert.Equal(
            ["ef", "migrations", "script", "20260101000000_InitialCreate", "0"],
            ScriptCall(runner).Take(5));
    }

    [Fact]
    public async Task Flipping_the_direction_while_showing_sql_fetches_the_other_direction()
    {
        var (detail, runner) = Build();
        detail.Show(Row(1));
        await detail.ShowSqlCommand.ExecuteAsync(null);

        detail.ScriptDown = true;
        await detail.ShowSqlCommand.ExecutionTask!;

        // Otherwise the switch would simply relabel the up script already on screen.
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(
            "-- SQL for 20260101000001_AddBlogUrl-to-20260101000000_InitialCreate", detail.Sql);
    }

    [Fact]
    public async Task Flipping_the_direction_back_reuses_the_cached_sql()
    {
        var (detail, runner) = Build();
        detail.Show(Row(1));
        await detail.ShowSqlCommand.ExecuteAsync(null);

        detail.ScriptDown = true;
        await detail.ShowSqlCommand.ExecutionTask!;

        detail.ScriptUp = true;
        await detail.ShowSqlCommand.ExecutionTask!;

        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal("-- SQL for 20260101000000_InitialCreate-to-20260101000001_AddBlogUrl", detail.Sql);
    }

    [Fact]
    public void Flipping_the_direction_over_the_source_generates_nothing()
    {
        var (detail, runner) = Build();
        detail.Show(Row(1));

        detail.ScriptDown = true;

        // The switch is only on screen with the SQL, but nothing about it should imply a build.
        Assert.Empty(runner.Calls);
        Assert.False(detail.IsShowingSql);
    }

    [Fact]
    public async Task Up_and_down_sql_are_never_opened_from_the_same_file()
    {
        var (detail, _) = Build();
        detail.Show(Row(1));

        await detail.ShowSqlCommand.ExecuteAsync(null);
        var upPath = detail.SqlPath;

        detail.ScriptDown = true;
        await detail.ShowSqlCommand.ExecutionTask!;

        Assert.NotNull(upPath);
        Assert.NotNull(detail.SqlPath);
        Assert.NotEqual(upPath, detail.SqlPath);
    }

    [Fact]
    public async Task Reselecting_a_migration_keeps_the_chosen_direction()
    {
        var (detail, runner) = Build();
        detail.Show(Row(1));
        detail.ScriptDown = true;
        await detail.ShowSqlCommand.ExecuteAsync(null);

        detail.Show(Row(0));
        await detail.ShowSqlCommand.ExecuteAsync(null);

        // Someone reading rollback scripts is usually reading more than one of them.
        Assert.True(detail.ScriptDown);
        Assert.Equal(
            ["ef", "migrations", "script", "20260101000000_InitialCreate", "0"],
            ScriptCall(runner).Take(5));
    }

    // ---- Open file follows the current view ----

    [Fact]
    public void Open_opens_the_source_file_while_showing_source()
    {
        var opened = new List<string>();
        WriteMigration("20260101000000_InitialCreate", "// source");
        var (detail, _) = Build();
        detail.OpenFileAsync = (string path) => { opened.Add(path); return Task.CompletedTask; };
        detail.Show(Row(0));

        detail.OpenCommand.Execute(null);

        Assert.Equal([detail.SourcePath!], opened);
    }

    [Fact]
    public async Task Open_opens_the_generated_sql_file_while_showing_sql()
    {
        var opened = new List<string>();
        WriteMigration("20260101000000_InitialCreate", "// source");
        var (detail, _) = Build();
        detail.OpenFileAsync = (string path) => { opened.Add(path); return Task.CompletedTask; };
        detail.Show(Row(0));
        await detail.ShowSqlCommand.ExecuteAsync(null);

        detail.OpenCommand.Execute(null);

        Assert.Equal([detail.SqlPath!], opened);
        Assert.NotEqual(detail.SourcePath, detail.SqlPath);
    }

    [Fact]
    public async Task Switching_back_to_source_after_viewing_sql_opens_source_again()
    {
        var opened = new List<string>();
        WriteMigration("20260101000000_InitialCreate", "// source");
        var (detail, _) = Build();
        detail.OpenFileAsync = (string path) => { opened.Add(path); return Task.CompletedTask; };
        detail.Show(Row(0));
        await detail.ShowSqlCommand.ExecuteAsync(null);

        detail.ShowSourceCommand.Execute(null);
        detail.OpenCommand.Execute(null);

        Assert.Equal([detail.SourcePath!], opened);
    }

    [Fact]
    public async Task Idempotent_and_non_idempotent_sql_are_never_opened_from_the_same_file()
    {
        var idempotent = false;
        var (detail, _) = Build(idempotentRequested: () => idempotent, canUseIdempotent: () => true);
        detail.Show(Row(0));

        await detail.ShowSqlCommand.ExecuteAsync(null);
        var plainPath = detail.SqlPath;

        idempotent = true;
        await detail.ShowSqlCommand.ExecuteAsync(null);
        var idempotentPath = detail.SqlPath;

        // Otherwise generating the second variant would silently overwrite the file "Open file"
        // still thinks holds the first one.
        Assert.NotNull(plainPath);
        Assert.NotNull(idempotentPath);
        Assert.NotEqual(plainPath, idempotentPath);
    }

    [Fact]
    public void Open_is_disabled_once_nothing_is_selected()
    {
        WriteMigration("20260101000000_InitialCreate", "// source");
        var (detail, _) = Build();
        detail.Show(Row(0));

        detail.Show(null);

        Assert.False(detail.OpenCommand.CanExecute(null));
    }

    [Fact]
    public void Clearing_drops_everything_including_the_cache()
    {
        WriteMigration("20260101000000_InitialCreate", "// source");
        var (detail, _) = Build();
        detail.Show(Row(0));

        detail.Clear();

        Assert.False(detail.HasMigration);
        Assert.Equal("", detail.Source);
        Assert.Null(detail.SourcePath);
        Assert.False(detail.IsShowingSql);
    }
}
