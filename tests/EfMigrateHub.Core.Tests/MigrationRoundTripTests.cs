using EfMigrateHub.App.ViewModels;
using EfMigrateHub.Core;

namespace EfMigrateHub.Core.Tests;

/// <summary>
/// The Phase 3 exit criteria, end to end: a real project, the real CLI, a real SQLite database, and
/// nothing driven except the view models the UI binds to.
/// </summary>
/// <remarks>
/// Slow — every step builds the sample project. It also mutates <c>samples/SampleEfApp</c>, so it
/// restores the fixture state in a finally block: two migrations with only the first applied. See
/// that project's README if it ever ends up dirty.
/// </remarks>
[Collection(SampleProjectCollection.Name)]
public class MigrationRoundTripTests : IDisposable
{
    private const string TripName = "EfMigrateHubRoundTrip";

    private readonly string _settingsPath = Path.Combine(
        Path.GetTempPath(), "EfMigrateHubTests", Guid.NewGuid().ToString("N"), "settings.json");

    private readonly List<ConfirmRequest> _confirmations = [];

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_settingsPath)!, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static string SamplePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EfMigrateHub.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new InvalidOperationException("repository root not found");
        return Path.Combine(root, "samples", "SampleEfApp");
    }

    [Fact]
    public async Task Add_then_apply_then_remove_works_against_a_real_database()
    {
        var sample = SamplePath();
        Assert.True(Directory.Exists(sample), $"sample project not found at {sample}");

        var context = SynchronizationContext.Current;
        var shell = new MainWindowViewModel(new EfRunner(), new AppSettings(), _settingsPath)
        {
            PostToUiThread = context is null
                ? action => action()
                : action => context.Post(_ => action(), null),
            PickFolderAsync = () => Task.FromResult<string?>(sample),
            ConfirmAsync = request =>
            {
                _confirmations.Add(request);
                return Task.FromResult(true);
            },
        };

        await shell.OpenFolderCommand.ExecuteAsync(null);

        Assert.False(shell.HasPreflightProblem, shell.PreflightProblem);
        Assert.Equal("BlogContext", shell.SelectedContext?.Name);

        var tab = shell.Migrations;

        try
        {
            // Opening the workspace loads the list, so this is the fixture state as found.
            Assert.False(tab.HasConnectionWarning, tab.ConnectionWarning);
            Assert.Equal(["InitialCreate", "AddBlogUrl"], tab.Migrations.Select(m => m.Name));
            Assert.Equal(MigrationState.Applied, tab.Migrations[0].State);
            Assert.Equal(MigrationState.Pending, tab.Migrations[1].State);

            // ---- Add ----
            tab.NewMigrationName = TripName;
            Assert.False(tab.HasNewMigrationNameError);
            await tab.AddCommand.ExecuteAsync(null);

            Assert.Equal(
                ["InitialCreate", "AddBlogUrl", TripName],
                tab.Migrations.Select(m => m.Name));
            Assert.Equal(MigrationState.Pending, tab.Migrations[2].State);

            // ---- Apply ----
            await tab.UpdateToLatestCommand.ExecuteAsync(null);

            Assert.All(tab.Migrations, m => Assert.Equal(MigrationState.Applied, m.State));
            Assert.Equal(3, tab.AppliedCount);
            Assert.Equal(0, tab.PendingCount);

            // ---- Remove ----
            // Applied, so Force is required; EF refuses otherwise. The confirmation must say so.
            tab.ForceRemove = true;
            await tab.RemoveCommand.ExecuteAsync(null);

            Assert.Contains(TripName, _confirmations.Last().Message);
            Assert.Equal(["InitialCreate", "AddBlogUrl"], tab.Migrations.Select(m => m.Name));
            Assert.False(
                Directory.EnumerateFiles(Path.Combine(sample, "Migrations"), $"*{TripName}*").Any(),
                "the migration files should be gone");
        }
        finally
        {
            await RestoreFixtureStateAsync(shell, tab, sample);
        }

        // The fixture is back to one applied migration and one pending.
        Assert.Equal(MigrationState.Applied, tab.Migrations[0].State);
        Assert.Equal(MigrationState.Pending, tab.Migrations[1].State);
    }

    /// <summary>
    /// Puts the sample back to InitialCreate-applied, AddBlogUrl-pending. Best effort: a failure here
    /// must not mask the real assertion failure that got us into the finally block.
    /// </summary>
    private static async Task RestoreFixtureStateAsync(
        MainWindowViewModel shell,
        MigrationsViewModel tab,
        string sample)
    {
        try
        {
            // Drop the round-trip migration if an assertion failed before Remove ran.
            if (tab.Migrations.Any(m => m.Name == TripName))
            {
                tab.ForceRemove = true;
                await tab.RemoveCommand.ExecuteAsync(null);
            }

            tab.SelectedMigration = tab.Migrations.FirstOrDefault(m => m.Name == "InitialCreate");
            if (tab.SelectedMigration is not null)
            {
                await tab.UpdateToSelectedCommand.ExecuteAsync(null);
            }

            await tab.RefreshCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not restore {sample}: {ex.Message}");
            Console.WriteLine(shell.Session.LastResult?.Diagnostics);
        }
    }
}
