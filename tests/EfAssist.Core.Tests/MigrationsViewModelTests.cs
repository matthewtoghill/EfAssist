using EfAssist.App.ViewModels;
using EfAssist.Core;

namespace EfAssist.Core.Tests;

/// <summary>
/// The Migrations tab, driven with a fake runner. Concentrates on the parts that could do damage:
/// what gets confirmed, what gets sent to the CLI, and whether applied state is ever asserted
/// without having asked the database.
/// </summary>
public class MigrationsViewModelTests
{
    private static readonly EfTarget Target = new(
        Project: @"C:\repo\src\Data\Data.csproj",
        StartupProject: @"C:\repo\src\Api\Api.csproj",
        Context: "BlogContext");

    /// <summary>Answers <c>dotnet ef</c> commands from in-memory state.</summary>
    private sealed class FakeEf : IEfRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        /// <summary>Rows returned by <c>migrations list</c>: name and applied state.</summary>
        public List<(string Name, bool Applied)> Rows { get; set; } =
            [("InitialCreate", true), ("AddBlogUrl", false)];

        /// <summary>Command prefixes to fail, e.g. "migrations add".</summary>
        public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);

        /// <summary>When true, only a connected list fails — the offline one succeeds.</summary>
        public bool DatabaseUnreachable { get; set; }

        public string DatabaseName { get; set; } = "AppDb";

        public string Provider { get; set; } = "Microsoft.EntityFrameworkCore.SqlServer";

        public Task<EfResult> RunAsync(
            IReadOnlyList<string> args,
            string workingDirectory,
            IProgress<OutputLine>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(args);
            var key = string.Join(' ', args.Skip(1).Take(2));

            if (Failing.Contains(key))
            {
                return Result(new EfResult(
                    1, [new OutputLine(OutputChannel.Error, $"{key} failed")], "fake", "."));
            }

            var offline = args.Contains("--no-connect");

            if (key == "migrations list")
            {
                if (DatabaseUnreachable && !offline)
                {
                    return Result(new EfResult(
                        1,
                        [new OutputLine(OutputChannel.Error, "A network-related error occurred.")],
                        "fake",
                        "."));
                }

                return Result(Data(MigrationsJson(offline)));
            }

            if (key == "dbcontext info")
            {
                return Result(Data($$"""
                    {
                      "type": "Sample.BlogContext",
                      "providerName": "{{Provider}}",
                      "databaseName": "{{DatabaseName}}",
                      "dataSource": "localhost",
                      "options": "None"
                    }
                    """));
            }

            return Result(new EfResult(0, [], "fake", "."));
        }

        private static Task<EfResult> Result(EfResult result) => Task.FromResult(result);

        private static EfResult Data(string payload) => new(
            0,
            payload.Split('\n').Select(l => new OutputLine(OutputChannel.Data, l.TrimEnd('\r'))).ToArray(),
            "fake",
            ".");

        private string MigrationsJson(bool offline) => "[" + string.Join(",", Rows.Select((r, i) =>
            $$"""
            {
              "id": "2026010100000{{i}}_{{r.Name}}",
              "name": "{{r.Name}}",
              "safeName": "{{r.Name}}",
              "applied": {{(offline ? "null" : r.Applied ? "true" : "false")}}
            }
            """)) + "]";
    }

    /// <summary>Records what was asked and answers with a fixed decision.</summary>
    private sealed class FakeConfirm(bool answer)
    {
        public List<ConfirmRequest> Requests { get; } = [];

        public ConfirmRequest? Last => Requests.LastOrDefault();

        public Task<bool> AskAsync(ConfirmRequest request)
        {
            Requests.Add(request);
            return Task.FromResult(answer);
        }
    }

    private static (MigrationsViewModel Tab, FakeEf Runner, FakeConfirm Confirm, List<int> Persists)
        Build(bool confirmed = true, EfTarget? target = null)
    {
        var runner = new FakeEf();
        var session = new CommandSession(runner) { PostToUiThread = action => action() };
        var persists = new List<int>();
        var confirm = new FakeConfirm(confirmed);
        var tab = new MigrationsViewModel(session, () => target ?? Target, () => persists.Add(1))
        {
            ConfirmAsync = confirm.AskAsync,
        };

        return (tab, runner, confirm, persists);
    }

    private static IReadOnlyList<string> LastCall(FakeEf runner, string key) =>
        runner.Calls.Last(a => string.Join(' ', a.Skip(1).Take(2)) == key);

    private static bool Called(FakeEf runner, string key) =>
        runner.Calls.Any(a => string.Join(' ', a.Skip(1).Take(2)) == key);

    // ---- Listing ----

    [Fact]
    public async Task Lists_migrations_with_their_applied_state()
    {
        var (tab, _, _, _) = Build();

        await tab.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(["InitialCreate", "AddBlogUrl"], tab.Migrations.Select(m => m.Name));
        Assert.Equal(MigrationState.Applied, tab.Migrations[0].State);
        Assert.Equal(MigrationState.Pending, tab.Migrations[1].State);
        Assert.Equal(1, tab.AppliedCount);
        Assert.Equal(1, tab.PendingCount);
    }

    [Fact]
    public async Task Offline_listing_reports_unknown_rather_than_pending()
    {
        var (tab, runner, _, _) = Build();
        tab.Offline = true;

        await tab.RefreshCommand.ExecuteAsync(null);

        Assert.Contains("--no-connect", LastCall(runner, "migrations list"));
        Assert.All(tab.Migrations, m => Assert.Equal(MigrationState.Unknown, m.State));
        Assert.Equal(0, tab.PendingCount);
    }

    [Fact]
    public async Task An_unreachable_database_falls_back_to_an_offline_list_with_a_warning()
    {
        var (tab, runner, _, _) = Build();
        runner.DatabaseUnreachable = true;

        await tab.RefreshCommand.ExecuteAsync(null);

        // The names are still useful; the applied column must not pretend to know anything.
        Assert.Equal(["InitialCreate", "AddBlogUrl"], tab.Migrations.Select(m => m.Name));
        Assert.All(tab.Migrations, m => Assert.Equal(MigrationState.Unknown, m.State));
        Assert.True(tab.HasConnectionWarning);
        Assert.Contains("could not be reached", tab.ConnectionWarning);
    }

    [Fact]
    public async Task A_failure_that_is_not_the_database_reports_the_real_error()
    {
        var (tab, runner, _, _) = Build();
        runner.Failing.Add("migrations list");

        await tab.RefreshCommand.ExecuteAsync(null);

        Assert.Empty(tab.Migrations);
        Assert.False(tab.HasConnectionWarning);
    }

    [Fact]
    public async Task A_refresh_keeps_the_selected_migration()
    {
        var (tab, _, _, _) = Build();
        await tab.RefreshCommand.ExecuteAsync(null);
        tab.SelectedMigration = tab.Migrations[1];

        await tab.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("AddBlogUrl", tab.SelectedMigration?.Name);
    }

    [Theory]
    [InlineData(DiscoveryMode.Manual, false)]
    [InlineData(DiscoveryMode.Cached, true)]
    [InlineData(DiscoveryMode.Auto, true)]
    [InlineData(DiscoveryMode.AutoNoBuildFirst, true)]
    public async Task Refresh_mode_decides_whether_opening_loads_the_list(DiscoveryMode mode, bool loads)
    {
        var (tab, runner, _, _) = Build();
        tab.RefreshMode = mode;

        await tab.LoadForContextAsync();

        Assert.Equal(loads, Called(runner, "migrations list"));
    }

    [Fact]
    public async Task Cached_mode_reuses_a_remembered_list_without_running_anything()
    {
        var (tab, runner, _, _) = Build();
        tab.Restore(new WorkspaceSettings
        {
            MigrationRefresh = DiscoveryMode.Cached,
            KnownMigrations = [new MigrationInfo("1_A", "A", "A", true)],
        });

        await tab.LoadForContextAsync();

        Assert.False(Called(runner, "migrations list"));
        Assert.Single(tab.Migrations);
    }

    // ---- Remembered state ----

    [Fact]
    public void A_remembered_list_never_claims_a_migration_is_applied()
    {
        var (tab, _, _, _) = Build();

        // Even if an old settings file does contain applied state, it must not be believed.
        tab.Restore(new WorkspaceSettings
        {
            KnownMigrations =
            [
                new MigrationInfo("1_A", "A", "A", true),
                new MigrationInfo("2_B", "B", "B", false),
            ],
        });

        Assert.All(tab.Migrations, m => Assert.Equal(MigrationState.Unknown, m.State));
    }

    [Fact]
    public async Task Applied_state_is_never_written_to_settings()
    {
        var (tab, _, _, _) = Build();
        await tab.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(MigrationState.Applied, tab.Migrations[0].State);

        var saved = new WorkspaceSettings();
        tab.Store(saved);

        Assert.Equal(2, saved.KnownMigrations.Count);
        Assert.All(saved.KnownMigrations, m => Assert.Null(m.Applied));
    }

    // ---- Adding ----

    [Fact]
    public async Task Adding_passes_the_name_and_then_refreshes()
    {
        var (tab, runner, _, _) = Build();
        await tab.RefreshCommand.ExecuteAsync(null);
        tab.NewMigrationName = "AddPostTitle";

        await tab.AddCommand.ExecuteAsync(null);

        var add = LastCall(runner, "migrations add");
        Assert.Equal(["ef", "migrations", "add", "AddPostTitle"], add.Take(4));
        Assert.Equal("", tab.NewMigrationName);

        // The list must reflect what just happened, so a refresh follows every mutation.
        Assert.True(runner.Calls.FindLastIndex(a => a.Contains("list")) >
                    runner.Calls.FindLastIndex(a => a.Contains("add")));
    }

    [Fact]
    public async Task Adding_passes_an_output_directory_when_one_is_given()
    {
        var (tab, runner, _, _) = Build();
        tab.NewMigrationName = "AddPostTitle";
        tab.OutputDirectory = "Persistence/Migrations";

        await tab.AddCommand.ExecuteAsync(null);

        var add = LastCall(runner, "migrations add");
        Assert.Equal("Persistence/Migrations", add[add.ToList().IndexOf("--output-dir") + 1]);
    }

    [Fact]
    public async Task An_invalid_name_blocks_the_command_before_a_build_happens()
    {
        var (tab, runner, _, _) = Build();

        tab.NewMigrationName = "2ndTry";

        Assert.True(tab.HasNewMigrationNameError);
        Assert.False(tab.AddCommand.CanExecute(null));

        await tab.AddCommand.ExecuteAsync(null);
        Assert.False(Called(runner, "migrations add"));
    }

    [Fact]
    public async Task A_duplicate_name_is_rejected_against_the_current_list()
    {
        var (tab, runner, _, _) = Build();
        await tab.RefreshCommand.ExecuteAsync(null);

        tab.NewMigrationName = "AddBlogUrl";

        Assert.Contains("already exists", tab.NewMigrationNameError);
        Assert.False(tab.AddCommand.CanExecute(null));
    }

    [Fact]
    public async Task Adding_a_migration_is_not_confirmed_because_it_touches_no_database()
    {
        var (tab, runner, confirm, _) = Build();
        tab.NewMigrationName = "AddPostTitle";

        await tab.AddCommand.ExecuteAsync(null);

        Assert.Empty(confirm.Requests);
        Assert.True(Called(runner, "migrations add"));
    }

    // ---- Removing ----

    [Fact]
    public async Task Removing_confirms_by_naming_the_most_recent_migration()
    {
        var (tab, runner, confirm, _) = Build();
        await tab.RefreshCommand.ExecuteAsync(null);

        await tab.RemoveCommand.ExecuteAsync(null);

        Assert.Contains("AddBlogUrl", confirm.Last!.Message);
        Assert.True(Called(runner, "migrations remove"));
    }

    [Fact]
    public async Task Declining_the_confirmation_removes_nothing()
    {
        var (tab, runner, confirm, _) = Build(confirmed: false);
        await tab.RefreshCommand.ExecuteAsync(null);

        await tab.RemoveCommand.ExecuteAsync(null);

        Assert.Single(confirm.Requests);
        Assert.False(Called(runner, "migrations remove"));
    }

    [Fact]
    public async Task Force_is_only_passed_when_it_is_ticked_and_the_warning_says_what_it_does()
    {
        var (tab, runner, confirm, _) = Build();
        await tab.RefreshCommand.ExecuteAsync(null);

        await tab.RemoveCommand.ExecuteAsync(null);
        Assert.DoesNotContain("--force", LastCall(runner, "migrations remove"));

        tab.ForceRemove = true;
        await tab.RemoveCommand.ExecuteAsync(null);

        Assert.Contains("--force", LastCall(runner, "migrations remove"));
        Assert.Contains("reverted in the database", confirm.Last!.Detail);
    }

    [Fact]
    public async Task With_no_dialog_wired_up_a_destructive_action_does_not_proceed()
    {
        // No confirmation means no consent, so this must fail closed rather than assume yes.
        var (tab, runner, _, _) = Build();
        tab.ConfirmAsync = null;
        await tab.RefreshCommand.ExecuteAsync(null);

        await tab.RemoveCommand.ExecuteAsync(null);

        Assert.False(Called(runner, "migrations remove"));
    }

    // ---- Applying ----

    [Fact]
    public async Task Applying_forward_is_confirmed_and_names_what_would_be_applied()
    {
        var (tab, runner, confirm, _) = Build();
        await tab.RefreshCommand.ExecuteAsync(null);

        await tab.UpdateToLatestCommand.ExecuteAsync(null);

        // A misclick on "Update to latest" would otherwise run migrations against whatever database
        // the startup project currently points at, with no way back.
        Assert.Single(confirm.Requests);
        Assert.Equal("Apply migrations", confirm.Last!.Title);
        Assert.Contains("BlogContext", confirm.Last.Message);
        Assert.Contains("AddBlogUrl", confirm.Last.Detail);
        Assert.DoesNotContain("InitialCreate", confirm.Last.Detail);

        var update = LastCall(runner, "database update");
        Assert.Equal(["ef", "database", "update"], update.Take(3));
    }

    [Fact]
    public async Task Declining_a_forward_apply_changes_nothing()
    {
        var (tab, runner, _, _) = Build(confirmed: false);
        await tab.RefreshCommand.ExecuteAsync(null);

        await tab.UpdateToLatestCommand.ExecuteAsync(null);

        Assert.False(Called(runner, "database update"));
    }

    [Fact]
    public async Task A_forward_apply_with_nothing_outstanding_says_so()
    {
        var (tab, runner, confirm, _) = Build();
        runner.Rows = [("A", true), ("B", true)];
        await tab.RefreshCommand.ExecuteAsync(null);

        await tab.UpdateToLatestCommand.ExecuteAsync(null);

        Assert.Contains("no changes", confirm.Last!.Detail);
    }

    [Fact]
    public async Task Rolling_back_confirms_and_names_what_would_be_reverted()
    {
        var (tab, runner, confirm, _) = Build();
        runner.Rows = [("A", true), ("B", true), ("C", true)];
        await tab.RefreshCommand.ExecuteAsync(null);
        tab.SelectedMigration = tab.Migrations[0];

        await tab.UpdateToSelectedCommand.ExecuteAsync(null);

        Assert.Single(confirm.Requests);
        Assert.Contains("B", confirm.Last!.Detail);
        Assert.Contains("C", confirm.Last.Detail);
        Assert.Equal("A", LastCall(runner, "database update")[3]);
    }

    [Fact]
    public async Task Moving_forward_to_a_pending_migration_confirms_as_an_apply_not_a_rollback()
    {
        var (tab, runner, confirm, _) = Build();
        runner.Rows = [("A", true), ("B", false), ("C", false)];
        await tab.RefreshCommand.ExecuteAsync(null);
        tab.SelectedMigration = tab.Migrations[1];

        await tab.UpdateToSelectedCommand.ExecuteAsync(null);

        // Nothing applied sits after B, so this only moves forward: confirmed, but not as data loss.
        Assert.Single(confirm.Requests);
        Assert.Equal("Apply migrations", confirm.Last!.Title);
        Assert.Contains("B", confirm.Last.Detail);
        Assert.DoesNotContain("reverted", confirm.Last.Detail);
        Assert.True(Called(runner, "database update"));
    }

    [Fact]
    public async Task An_unknown_applied_state_still_confirms_rather_than_guessing()
    {
        // Offline, so we cannot tell whether the later migrations are applied. Guessing "pending"
        // here would skip the warning before a possible destructive rollback.
        var (tab, _, confirm, _) = Build();
        tab.Offline = true;
        await tab.RefreshCommand.ExecuteAsync(null);
        tab.SelectedMigration = tab.Migrations[0];

        await tab.UpdateToSelectedCommand.ExecuteAsync(null);

        Assert.Single(confirm.Requests);
        Assert.Contains("unknown", confirm.Last!.Detail);
    }

    [Fact]
    public async Task Reverting_everything_confirms_first_and_targets_zero()
    {
        var (tab, runner, confirm, _) = Build();
        await tab.RefreshCommand.ExecuteAsync(null);

        await tab.RevertAllCommand.ExecuteAsync(null);

        Assert.Single(confirm.Requests);
        Assert.Equal("0", LastCall(runner, "database update")[3]);
    }

    [Fact]
    public async Task Declining_a_rollback_leaves_the_database_alone()
    {
        var (tab, runner, _, _) = Build(confirmed: false);
        await tab.RefreshCommand.ExecuteAsync(null);

        await tab.RevertAllCommand.ExecuteAsync(null);

        Assert.False(Called(runner, "database update"));
    }

    // ---- Dropping ----

    [Fact]
    public async Task Dropping_asks_EF_for_the_database_name_and_gates_on_typing_it()
    {
        var (tab, runner, confirm, _) = Build();
        runner.DatabaseName = "OrdersDb";

        await tab.DropDatabaseCommand.ExecuteAsync(null);

        Assert.True(Called(runner, "dbcontext info"));
        Assert.Equal("OrdersDb", confirm.Last!.RequiredTypedValue);
        Assert.Contains("OrdersDb", confirm.Last.Message);
        Assert.True(Called(runner, "database drop"));
    }

    [Fact]
    public async Task A_sqlite_database_is_confirmed_by_its_file_rather_than_the_word_main()
    {
        // SQLite reports "main" as the database name, which would make the gate meaningless.
        var sqlite = new FakeEf { DatabaseName = "main", Provider = "Microsoft.EntityFrameworkCore.Sqlite" };
        var session = new CommandSession(sqlite) { PostToUiThread = action => action() };
        var confirm = new FakeConfirm(false);
        var tab = new MigrationsViewModel(session, () => Target, () => { })
        {
            ConfirmAsync = confirm.AskAsync,
        };

        await tab.DropDatabaseCommand.ExecuteAsync(null);

        Assert.Equal("localhost", confirm.Last!.RequiredTypedValue);
    }

    [Fact]
    public async Task Declining_the_drop_confirmation_drops_nothing()
    {
        var (tab, runner, confirm, _) = Build(confirmed: false);

        await tab.DropDatabaseCommand.ExecuteAsync(null);

        Assert.Single(confirm.Requests);
        Assert.False(Called(runner, "database drop"));
    }

    [Fact]
    public async Task Without_a_database_name_the_drop_is_not_offered_at_all()
    {
        // An ungated drop is not on the menu: no name means no way to confirm, so no drop.
        var (tab, runner, confirm, _) = Build();
        runner.Failing.Add("dbcontext info");

        await tab.DropDatabaseCommand.ExecuteAsync(null);

        Assert.Empty(confirm.Requests);
        Assert.False(Called(runner, "database drop"));
    }

    // ---- Sort order ----

    [Fact]
    public async Task The_list_is_oldest_first_by_default_and_can_be_flipped()
    {
        var (tab, _, _, _) = Build();
        await tab.RefreshCommand.ExecuteAsync(null);

        Assert.False(tab.SortNewestFirst);
        Assert.Equal(["InitialCreate", "AddBlogUrl"], tab.Migrations.Select(m => m.Name));

        tab.ToggleSortOrderCommand.Execute(null);

        Assert.True(tab.SortNewestFirst);
        Assert.Equal(["AddBlogUrl", "InitialCreate"], tab.Migrations.Select(m => m.Name));
    }

    [Fact]
    public async Task Sorting_newest_first_does_not_change_which_migration_Remove_targets()
    {
        // EF can only remove the most recent migration. If the display order decided that, flipping
        // the sort would silently point Remove at the oldest one.
        var (tab, runner, confirm, _) = Build();
        await tab.RefreshCommand.ExecuteAsync(null);
        tab.SortNewestFirst = true;

        Assert.Equal("AddBlogUrl", tab.LastMigration?.Name);

        await tab.RemoveCommand.ExecuteAsync(null);

        Assert.Contains("AddBlogUrl", confirm.Last!.Message);
        Assert.True(Called(runner, "migrations remove"));
    }

    [Fact]
    public async Task Sorting_newest_first_does_not_change_a_rollback_warning()
    {
        var (tab, _, confirm, _) = Build();

        await tab.RefreshCommand.ExecuteAsync(null);
        tab.SortNewestFirst = true;
        tab.SelectedMigration = tab.Migrations.Single(m => m.Name == "InitialCreate");

        await tab.UpdateToSelectedCommand.ExecuteAsync(null);

        // AddBlogUrl is pending, so rolling back to InitialCreate undoes nothing: still an apply.
        Assert.Equal("Apply migrations", confirm.Last!.Title);
    }

    [Fact]
    public void The_sort_order_is_remembered_across_the_app_not_per_workspace()
    {
        var display = new DisplaySettings();
        var session = new CommandSession(new FakeEf()) { PostToUiThread = action => action() };
        var tab = new MigrationsViewModel(session, () => Target, () => { }, display);

        tab.SortNewestFirst = true;

        Assert.True(display.SortNewestFirst);
        Assert.Equal("↓", tab.SortGlyph);

        var reopened = new MigrationsViewModel(session, () => Target, () => { }, display);
        Assert.True(reopened.SortNewestFirst);
    }

    [Fact]
    public async Task Remembered_migrations_are_stored_chronologically_whatever_the_display_order()
    {
        var (tab, _, _, _) = Build();
        await tab.RefreshCommand.ExecuteAsync(null);
        tab.SortNewestFirst = true;

        var saved = new WorkspaceSettings();
        tab.Store(saved);

        Assert.Equal(["InitialCreate", "AddBlogUrl"], saved.KnownMigrations.Select(m => m.Name));
    }

    [Fact]
    public async Task Rows_are_numbered_from_one_in_the_order_they_are_applied()
    {
        var (tab, runner, _, _) = Build();
        runner.Rows = [("A", true), ("B", true), ("C", false)];

        await tab.RefreshCommand.ExecuteAsync(null);

        Assert.Equal([1, 2, 3], tab.Migrations.Select(m => m.Index));
        Assert.Equal(["A", "B", "C"], tab.Migrations.Select(m => m.Name));
    }

    [Fact]
    public async Task Row_numbers_stay_with_their_migration_when_the_sort_flips()
    {
        // The number describes where a migration sits in the sequence, not where it sits on screen.
        var (tab, runner, _, _) = Build();
        runner.Rows = [("A", true), ("B", true), ("C", false)];
        await tab.RefreshCommand.ExecuteAsync(null);

        tab.SortNewestFirst = true;

        Assert.Equal(["C", "B", "A"], tab.Migrations.Select(m => m.Name));
        Assert.Equal([3, 2, 1], tab.Migrations.Select(m => m.Index));
        Assert.Equal(1, tab.Migrations.Single(m => m.Name == "A").Index);
    }

    [Fact]
    public void A_remembered_list_is_numbered_too()
    {
        var (tab, _, _, _) = Build();

        tab.Restore(new WorkspaceSettings
        {
            KnownMigrations =
            [
                new MigrationInfo("1_A", "A", "A", null),
                new MigrationInfo("2_B", "B", "B", null),
            ],
        });

        Assert.Equal([1, 2], tab.Migrations.Select(m => m.Index));
    }

    // ---- Guards ----

    [Fact]
    public void Nothing_can_run_before_a_project_and_context_are_selected()
    {
        var runner = new FakeEf();
        var session = new CommandSession(runner) { PostToUiThread = action => action() };
        var tab = new MigrationsViewModel(session, () => null, () => { });

        Assert.False(tab.IsReady);
        Assert.False(tab.RefreshCommand.CanExecute(null));
        Assert.False(tab.UpdateToLatestCommand.CanExecute(null));
        Assert.False(tab.DropDatabaseCommand.CanExecute(null));
    }

    [Fact]
    public async Task Every_command_carries_the_selected_context()
    {
        var (tab, runner, _, _) = Build();

        await tab.RefreshCommand.ExecuteAsync(null);

        var list = LastCall(runner, "migrations list");
        Assert.Equal("BlogContext", list[list.ToList().IndexOf("--context") + 1]);
    }

    // ---- Stale state ----

    [Fact]
    public async Task A_failed_refresh_keeps_the_list_but_marks_it_out_of_date()
    {
        var (tab, runner, _, _) = Build();
        await tab.RefreshCommand.ExecuteAsync(null);
        Assert.False(tab.IsStale);

        runner.Failing.Add("migrations list");
        await tab.RefreshCommand.ExecuteAsync(null);

        // The names are still worth reading; what must not survive is the impression they are current.
        Assert.Equal(["InitialCreate", "AddBlogUrl"], tab.Migrations.Select(m => m.Name));
        Assert.True(tab.IsStale);
        Assert.True(tab.ShowsStaleWarning);
    }

    [Fact]
    public async Task A_successful_refresh_clears_the_out_of_date_mark()
    {
        var (tab, runner, _, _) = Build();
        runner.Failing.Add("migrations list");
        await tab.RefreshCommand.ExecuteAsync(null);
        Assert.True(tab.IsStale);

        runner.Failing.Clear();
        await tab.RefreshCommand.ExecuteAsync(null);

        Assert.False(tab.IsStale);
        Assert.False(tab.ShowsStaleWarning);
    }

    [Fact]
    public async Task A_failed_database_update_leaves_the_list_marked_out_of_date()
    {
        var (tab, runner, _, _) = Build();
        await tab.RefreshCommand.ExecuteAsync(null);

        runner.Failing.Add("database update");
        await tab.UpdateToLatestCommand.ExecuteAsync(null);

        // An update that fails part way through has still changed the database, so applied state on
        // screen cannot be trusted until it is refetched.
        Assert.True(tab.IsStale);
    }

    [Fact]
    public async Task A_failed_add_leaves_the_list_marked_out_of_date()
    {
        var (tab, runner, _, _) = Build();
        await tab.RefreshCommand.ExecuteAsync(null);

        runner.Failing.Add("migrations add");
        tab.NewMigrationName = "AddThing";
        await tab.AddCommand.ExecuteAsync(null);

        Assert.True(tab.IsStale);
    }

    [Fact]
    public async Task An_empty_list_does_not_show_a_stale_warning_with_nothing_to_distrust()
    {
        var (tab, runner, _, _) = Build();
        runner.Failing.Add("migrations list");

        await tab.RefreshCommand.ExecuteAsync(null);

        Assert.True(tab.IsStale);
        Assert.False(tab.ShowsStaleWarning);
    }
}
