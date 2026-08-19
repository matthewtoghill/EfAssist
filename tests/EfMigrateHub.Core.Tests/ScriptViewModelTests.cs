using EfMigrateHub.App.ViewModels;
using EfMigrateHub.Core;

namespace EfMigrateHub.Core.Tests;

/// <summary>
/// The Script tab, driven with a fake runner. Concentrates on the range translation, where the file
/// ends up, and the idempotent gate.
/// </summary>
public class ScriptViewModelTests : IDisposable
{
    private const string Sql = "CREATE TABLE \"Blogs\" (\n    \"Id\" INTEGER NOT NULL\n);\n";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "EfMigrateHubTests", Guid.NewGuid().ToString("N"));

    private static readonly EfTarget Target = new(
        Project: @"C:\repo\src\Data\Data.csproj",
        StartupProject: @"C:\repo\src\Api\Api.csproj",
        Context: "BlogContext");

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

    /// <summary>Writes the SQL wherever <c>--output</c> points, so the tab has a real file to read.</summary>
    private sealed class FakeEf : IEfRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public string Provider { get; set; } = "Microsoft.EntityFrameworkCore.SqlServer";

        public bool ScriptFails { get; set; }

        public bool InfoFails { get; set; }

        public Task<EfResult> RunAsync(
            IReadOnlyList<string> args,
            string workingDirectory,
            IProgress<OutputLine>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(args);
            var key = string.Join(' ', args.Skip(1).Take(2));

            if (key == "dbcontext info")
            {
                return Task.FromResult(InfoFails
                    ? Failed("could not read the model")
                    : Data($$"""
                        {
                          "type": "Sample.BlogContext",
                          "providerName": "{{Provider}}",
                          "databaseName": "AppDb",
                          "dataSource": "localhost",
                          "options": "None"
                        }
                        """));
            }

            if (key == "migrations script")
            {
                if (ScriptFails)
                {
                    return Task.FromResult(Failed("Generating idempotent scripts is not supported."));
                }

                var output = args[args.ToList().IndexOf("--output") + 1];
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(output, Sql);
            }

            return Task.FromResult(new EfResult(0, [], "fake", "."));
        }

        private static EfResult Failed(string message) =>
            new(1, [new OutputLine(OutputChannel.Error, message)], "fake", ".");

        private static EfResult Data(string payload) => new(
            0,
            payload.Split('\n').Select(l => new OutputLine(OutputChannel.Data, l.TrimEnd('\r'))).ToArray(),
            "fake",
            ".");
    }

    private static List<MigrationInfo> Rows(params (string Name, bool? Applied)[] rows) =>
        [.. rows.Select((r, i) => new MigrationInfo($"2026010100000{i}_{r.Name}", r.Name, r.Name, r.Applied))];

    private (ScriptViewModel Tab, FakeEf Runner, List<ConfirmRequest> Confirms) Build(
        List<MigrationInfo>? migrations = null,
        bool confirmed = true,
        string? outputFolder = null,
        string? savePath = null)
    {
        var runner = new FakeEf();
        var session = new CommandSession(runner) { PostToUiThread = action => action() };
        var confirms = new List<ConfirmRequest>();
        var rows = migrations ?? Rows(("InitialCreate", true), ("AddBlogUrl", false));

        var tab = new ScriptViewModel(session, () => Target, () => rows, () => { })
        {
            ConfirmAsync = request =>
            {
                confirms.Add(request);
                return Task.FromResult(confirmed);
            },
            PickSaveFileAsync = (_, _) => Task.FromResult(savePath),
            OutputFolder = outputFolder ?? "",
        };

        tab.RefreshOptions();
        return (tab, runner, confirms);
    }

    private static IReadOnlyList<string> ScriptCall(FakeEf runner) =>
        runner.Calls.Last(a => string.Join(' ', a.Skip(1).Take(2)) == "migrations script");

    private static bool Scripted(FakeEf runner) =>
        runner.Calls.Any(a => string.Join(' ', a.Skip(1).Take(2)) == "migrations script");

    /// <summary>The positional FROM/TO arguments, which sit straight after the verb.</summary>
    private static List<string> Positional(IReadOnlyList<string> args) =>
        [.. args.Skip(3).TakeWhile(a => !a.StartsWith('-'))];

    // ---- Range ----

    [Fact]
    public async Task All_scripts_everything_by_passing_no_range()
    {
        var (tab, runner, _) = Build(outputFolder: _root);
        tab.Range = ScriptRange.All;

        await tab.GenerateCommand.ExecuteAsync(null);

        Assert.Empty(Positional(ScriptCall(runner)));
    }

    [Fact]
    public async Task Pending_scripts_from_the_last_applied_migration_forward()
    {
        var (tab, runner, _) = Build(
            Rows(("A", true), ("B", true), ("C", false)),
            outputFolder: _root);
        tab.Range = ScriptRange.Pending;

        await tab.GenerateCommand.ExecuteAsync(null);

        // From B, not C: the script has to include everything after the last applied migration.
        Assert.Equal(["B"], Positional(ScriptCall(runner)));
    }

    [Fact]
    public async Task Pending_with_nothing_applied_scripts_from_the_beginning()
    {
        var (tab, runner, _) = Build(Rows(("A", false), ("B", false)), outputFolder: _root);
        tab.Range = ScriptRange.Pending;

        await tab.GenerateCommand.ExecuteAsync(null);

        Assert.Empty(Positional(ScriptCall(runner)));
    }

    [Fact]
    public void Pending_warns_when_the_applied_state_was_never_fetched()
    {
        var (tab, _, _) = Build(Rows(("A", null), ("B", null)));

        tab.Range = ScriptRange.Pending;

        // Offline, so "pending" is a guess and the range could be wrong.
        Assert.True(tab.HasRangeWarning);
        Assert.Contains("unknown", tab.RangeWarning);

        tab.Range = ScriptRange.All;
        Assert.False(tab.HasRangeWarning);
    }

    [Fact]
    public async Task Custom_passes_both_endpoints()
    {
        var (tab, runner, _) = Build(outputFolder: _root);
        tab.Range = ScriptRange.Custom;
        tab.SelectedFrom = "InitialCreate";
        tab.SelectedTo = "AddBlogUrl";

        await tab.GenerateCommand.ExecuteAsync(null);

        Assert.Equal(["InitialCreate", "AddBlogUrl"], Positional(ScriptCall(runner)));
    }

    [Fact]
    public async Task Custom_with_only_an_end_point_still_sends_a_start()
    {
        // FROM and TO are positional, so a lone TO would be read as a FROM and script the wrong range.
        var (tab, runner, _) = Build(outputFolder: _root);
        tab.Range = ScriptRange.Custom;
        tab.SelectedTo = "AddBlogUrl";

        await tab.GenerateCommand.ExecuteAsync(null);

        Assert.Equal(["0", "AddBlogUrl"], Positional(ScriptCall(runner)));
    }

    [Fact]
    public void The_pickers_are_built_from_the_migrations_list()
    {
        var (tab, _, _) = Build();

        Assert.Equal(
            [ScriptViewModel.FromBeginning, "InitialCreate", "AddBlogUrl"],
            tab.FromOptions);
        Assert.Equal(
            ["InitialCreate", "AddBlogUrl", ScriptViewModel.ToLatest],
            tab.ToOptions);
    }

    [Fact]
    public void A_selection_that_no_longer_exists_falls_back_to_the_default()
    {
        var rows = Rows(("InitialCreate", true), ("AddBlogUrl", false));
        var session = new CommandSession(new FakeEf()) { PostToUiThread = action => action() };
        var tab = new ScriptViewModel(session, () => Target, () => rows, () => { });
        tab.RefreshOptions();
        tab.SelectedFrom = "AddBlogUrl";

        // The migration was removed since the pickers were built.
        rows.RemoveAt(1);
        tab.RefreshOptions();

        Assert.Equal(ScriptViewModel.FromBeginning, tab.SelectedFrom);
    }

    // ---- Idempotent gate ----

    [Fact]
    public async Task The_provider_is_probed_once_when_the_tab_is_opened()
    {
        var (tab, runner, _) = Build();

        await tab.OnActivatedAsync();
        await tab.OnActivatedAsync();

        Assert.Single(runner.Calls, a => a.Contains("dbcontext"));
        Assert.True(tab.CanUseIdempotent);
    }

    [Fact]
    public async Task Sqlite_cannot_use_idempotent_and_the_tooltip_says_why()
    {
        var (tab, runner, _) = Build();
        runner.Provider = "Microsoft.EntityFrameworkCore.Sqlite";
        tab.Idempotent = true;

        await tab.OnActivatedAsync();

        Assert.False(tab.CanUseIdempotent);
        Assert.Contains("does not support", tab.IdempotentTooltip);

        // Also untick it, so a provider switch cannot leave an unsupported flag armed.
        Assert.False(tab.Idempotent);
    }

    [Fact]
    public async Task An_unknown_provider_keeps_the_option_available()
    {
        // Better to attempt and surface EF's own error than to grey out something that would work.
        var (tab, runner, _) = Build();
        runner.InfoFails = true;

        await tab.OnActivatedAsync();

        Assert.True(tab.CanUseIdempotent);
    }

    [Fact]
    public async Task Idempotent_is_passed_only_when_ticked_and_supported()
    {
        var (tab, runner, _) = Build(outputFolder: _root);
        await tab.GenerateCommand.ExecuteAsync(null);
        Assert.DoesNotContain("--idempotent", ScriptCall(runner));

        tab.Idempotent = true;
        await tab.GenerateCommand.ExecuteAsync(null);
        Assert.Contains("--idempotent", ScriptCall(runner));
    }

    // ---- Destination ----

    [Fact]
    public async Task A_configured_folder_is_used_without_asking()
    {
        var (tab, runner, confirms) = Build(outputFolder: _root);

        await tab.GenerateCommand.ExecuteAsync(null);

        var expected = Path.Combine(_root, "BlogContext_0-to-latest.sql");
        Assert.Equal(expected, tab.GeneratedPath);
        Assert.True(File.Exists(expected));
        Assert.Empty(confirms);
    }

    [Fact]
    public async Task With_no_folder_configured_the_save_dialog_decides()
    {
        var chosen = Path.Combine(_root, "picked.sql");
        var (tab, _, confirms) = Build(savePath: chosen);

        await tab.GenerateCommand.ExecuteAsync(null);

        Assert.Equal(chosen, tab.GeneratedPath);
        // The OS dialog does its own overwrite prompt, so the app must not add a second one.
        Assert.Empty(confirms);
    }

    [Fact]
    public async Task Cancelling_the_save_dialog_generates_nothing()
    {
        var (tab, runner, _) = Build(savePath: null);

        await tab.GenerateCommand.ExecuteAsync(null);

        Assert.False(Scripted(runner));
        Assert.Null(tab.GeneratedPath);
    }

    [Fact]
    public async Task Overwriting_a_file_in_the_configured_folder_is_confirmed_first()
    {
        Directory.CreateDirectory(_root);
        var existing = Path.Combine(_root, "BlogContext_0-to-latest.sql");
        File.WriteAllText(existing, "-- hand edited");

        var (tab, runner, confirms) = Build(outputFolder: _root, confirmed: false);
        await tab.GenerateCommand.ExecuteAsync(null);

        Assert.Single(confirms);
        Assert.Contains("already exists", confirms[0].Message);
        Assert.False(Scripted(runner));
        Assert.Equal("-- hand edited", File.ReadAllText(existing));
    }

    [Fact]
    public async Task Accepting_the_overwrite_replaces_the_file()
    {
        Directory.CreateDirectory(_root);
        var existing = Path.Combine(_root, "BlogContext_0-to-latest.sql");
        File.WriteAllText(existing, "-- old");

        var (tab, _, confirms) = Build(outputFolder: _root, confirmed: true);
        await tab.GenerateCommand.ExecuteAsync(null);

        Assert.Single(confirms);
        Assert.Equal(Sql, File.ReadAllText(existing));
    }

    // ---- File name ----

    [Fact]
    public void The_suggested_name_follows_the_choices()
    {
        var (tab, _, _) = Build();

        Assert.Equal("BlogContext_0-to-latest.sql", tab.FileName);

        tab.Range = ScriptRange.Custom;
        tab.SelectedFrom = "InitialCreate";
        tab.SelectedTo = "AddBlogUrl";

        Assert.Equal("BlogContext_InitialCreate-to-AddBlogUrl.sql", tab.FileName);
    }

    [Fact]
    public void A_hand_typed_name_is_not_overwritten_by_later_choices()
    {
        var (tab, _, _) = Build();

        tab.FileName = "deploy-to-staging.sql";
        tab.Range = ScriptRange.Custom;
        tab.SelectedFrom = "InitialCreate";

        Assert.Equal("deploy-to-staging.sql", tab.FileName);

        // Reset puts the suggestion back and lets it track the choices again.
        tab.ResetFileNameCommand.Execute(null);
        Assert.Equal("BlogContext_InitialCreate-to-latest.sql", tab.FileName);
    }

    [Fact]
    public async Task A_name_without_an_extension_gets_one()
    {
        var (tab, _, _) = Build(outputFolder: _root);
        tab.FileName = "deploy";

        await tab.GenerateCommand.ExecuteAsync(null);

        Assert.Equal(Path.Combine(_root, "deploy.sql"), tab.GeneratedPath);
    }

    // ---- Result ----

    [Fact]
    public async Task The_generated_sql_is_read_back_byte_for_byte()
    {
        var (tab, runner, _) = Build(outputFolder: _root);

        await tab.GenerateCommand.ExecuteAsync(null);

        // Written to a file rather than scraped from stdout, so indentation and newlines survive.
        Assert.Equal(Sql, tab.Sql);
        Assert.Equal(File.ReadAllText(tab.GeneratedPath!), tab.Sql);
        Assert.Contains("--output", ScriptCall(runner));
    }

    [Fact]
    public async Task Open_and_reveal_only_become_available_after_a_script_exists()
    {
        var (tab, _, _) = Build(outputFolder: _root);

        Assert.False(tab.OpenCommand.CanExecute(null));
        Assert.False(tab.RevealCommand.CanExecute(null));
        Assert.False(tab.CopySqlCommand.CanExecute(null));

        await tab.GenerateCommand.ExecuteAsync(null);

        Assert.True(tab.OpenCommand.CanExecute(null));
        Assert.True(tab.RevealCommand.CanExecute(null));
        Assert.True(tab.CopySqlCommand.CanExecute(null));
    }

    [Fact]
    public async Task Open_and_reveal_hand_the_generated_path_to_the_shell()
    {
        var (tab, _, _) = Build(outputFolder: _root);
        var opened = new List<string>();
        var revealed = new List<string>();
        tab.OpenFileAsync = path => { opened.Add(path); return Task.CompletedTask; };
        tab.RevealFileAsync = path => { revealed.Add(path); return Task.CompletedTask; };

        await tab.GenerateCommand.ExecuteAsync(null);
        await tab.OpenCommand.ExecuteAsync(null);
        await tab.RevealCommand.ExecuteAsync(null);

        Assert.NotNull(tab.GeneratedPath);
        Assert.Equal([tab.GeneratedPath], opened);
        Assert.Equal([tab.GeneratedPath], revealed);
    }

    [Fact]
    public async Task A_failed_generation_leaves_the_viewer_alone()
    {
        var (tab, runner, _) = Build(outputFolder: _root);
        runner.ScriptFails = true;

        await tab.GenerateCommand.ExecuteAsync(null);

        Assert.Null(tab.GeneratedPath);
        Assert.Equal("", tab.Sql);
        Assert.False(tab.HasSql);
    }

    // ---- Settings ----

    [Fact]
    public void The_scripts_folder_round_trips_through_workspace_settings()
    {
        var (tab, _, _) = Build();
        tab.OutputFolder = _root;

        var saved = new WorkspaceSettings();
        tab.Store(saved);
        Assert.Equal(_root, saved.ScriptOutputFolder);

        var (reopened, _, _) = Build();
        reopened.Restore(saved);
        Assert.Equal(_root, reopened.OutputFolder);
        Assert.True(reopened.UsesConfiguredFolder);
    }

    [Fact]
    public void An_empty_folder_is_stored_as_null_rather_than_an_empty_string()
    {
        var (tab, _, _) = Build();
        var saved = new WorkspaceSettings { ScriptOutputFolder = _root };

        tab.OutputFolder = "   ";
        tab.Store(saved);

        Assert.Null(saved.ScriptOutputFolder);
    }

    [Fact]
    public void Nothing_can_be_generated_before_a_project_and_context_are_selected()
    {
        var session = new CommandSession(new FakeEf()) { PostToUiThread = action => action() };
        var tab = new ScriptViewModel(session, () => null, () => [], () => { });

        Assert.False(tab.IsReady);
        Assert.False(tab.GenerateCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_failed_generation_marks_the_sql_on_screen_as_out_of_date()
    {
        var (tab, runner, _) = Build(outputFolder: _root);
        await tab.GenerateCommand.ExecuteAsync(null);
        Assert.True(tab.HasSql);
        Assert.False(tab.IsStale);

        runner.ScriptFails = true;
        await tab.GenerateCommand.ExecuteAsync(null);

        // The previous script is still readable, but it is no longer the result of what the tab says.
        Assert.True(tab.HasSql);
        Assert.True(tab.IsStale);
        Assert.True(tab.ShowsStaleWarning);

        runner.ScriptFails = false;
        await tab.GenerateCommand.ExecuteAsync(null);

        Assert.False(tab.IsStale);
    }
}
