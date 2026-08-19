using EfMigrateHub.App.ViewModels;
using EfMigrateHub.Core;

namespace EfMigrateHub.Core.Tests;

/// <summary>
/// The Phase 4 exit criteria, end to end: the real CLI, the real sample project, both destination
/// modes, and a byte comparison against what <c>dotnet ef</c> produces when invoked directly.
/// </summary>
/// <remarks>Slow — generating a script builds the sample. Read-only: nothing here mutates it.</remarks>
[Collection(SampleProjectCollection.Name)]
public class ScriptGenerationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "EfMigrateHubTests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_root, "settings.json");

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
    public async Task Generates_the_same_sql_the_cli_does_and_both_destinations_work()
    {
        var sample = SamplePath();
        Assert.True(Directory.Exists(sample), $"sample project not found at {sample}");
        Directory.CreateDirectory(_root);

        var context = SynchronizationContext.Current;
        var shell = new MainWindowViewModel(new EfRunner(), new AppSettings(), SettingsPath)
        {
            PostToUiThread = context is null
                ? action => action()
                : action => context.Post(_ => action(), null),
            PickFolderAsync = () => Task.FromResult<string?>(sample),
            ConfirmAsync = _ => Task.FromResult(true),
        };

        await shell.OpenFolderCommand.ExecuteAsync(null);
        Assert.False(shell.HasPreflightProblem, shell.PreflightProblem);
        Assert.Equal("BlogContext", shell.SelectedContext?.Name);

        var tab = shell.Script;

        // Activation is what triggers the provider probe. The shell fires this when the Script tab is
        // selected; awaiting it directly here keeps the assertions below deterministic, and the
        // SelectedTabIndex wiring is covered separately by a fast test.
        await tab.OnActivatedAsync();

        // The sample is SQLite, which genuinely cannot do idempotent scripts.
        Assert.NotNull(tab.ProviderDetails);
        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", tab.ProviderDetails.ProviderName);
        Assert.False(tab.CanUseIdempotent);

        // ---- Destination mode 1: a configured scripts folder ----
        var folder = Path.Combine(_root, "scripts");
        tab.OutputFolder = folder;
        tab.Range = ScriptRange.All;

        Assert.Equal("BlogContext_0-to-latest.sql", tab.FileName);

        await tab.GenerateCommand.ExecuteAsync(null);

        var generated = Path.Combine(folder, "BlogContext_0-to-latest.sql");
        Assert.Equal(generated, tab.GeneratedPath);
        Assert.True(File.Exists(generated), shell.Session.LastResult?.Diagnostics);
        Assert.Contains("__EFMigrationsHistory", tab.Sql);
        Assert.Equal(File.ReadAllText(generated), tab.Sql);

        // ---- Matches the CLI invoked directly ----
        // Hand-written arguments, not EfArgs, so this is an independent check rather than a
        // restatement of what the app already believes.
        var reference = Path.Combine(_root, "reference.sql");
        var direct = await new EfRunner().RunAsync(
            [
                "ef", "migrations", "script",
                "--prefix-output", "--no-color",
                "--project", Path.Combine(sample, "SampleEfApp.csproj"),
                "--context", "BlogContext",
                "--output", reference,
            ],
            sample);

        Assert.True(direct.Success, direct.Diagnostics);
        Assert.Equal(File.ReadAllBytes(reference), File.ReadAllBytes(generated));

        // ---- Destination mode 2: Save As ----
        var chosen = Path.Combine(_root, "saved-as.sql");
        tab.OutputFolder = "";
        tab.PickSaveFileAsync = (suggested, _) =>
        {
            Assert.Equal("BlogContext_0-to-latest.sql", suggested);
            return Task.FromResult<string?>(chosen);
        };

        await tab.GenerateCommand.ExecuteAsync(null);

        Assert.Equal(chosen, tab.GeneratedPath);
        Assert.Equal(File.ReadAllBytes(reference), File.ReadAllBytes(chosen));

        // ---- Post-generation actions ----
        var opened = new List<string>();
        var revealed = new List<string>();
        tab.OpenFileAsync = path => { opened.Add(path); return Task.CompletedTask; };
        tab.RevealFileAsync = path => { revealed.Add(path); return Task.CompletedTask; };
        string? copied = null;
        shell.CopyToClipboardAsync = text => { copied = text; return Task.CompletedTask; };

        await tab.OpenCommand.ExecuteAsync(null);
        await tab.RevealCommand.ExecuteAsync(null);
        await tab.CopySqlCommand.ExecuteAsync(null);

        Assert.Equal([chosen], opened);
        Assert.Equal([chosen], revealed);
        Assert.Equal(tab.Sql, copied);
    }

    [Fact]
    public async Task A_pending_range_scripts_only_what_is_not_yet_applied()
    {
        var sample = SamplePath();
        Directory.CreateDirectory(_root);

        var context = SynchronizationContext.Current;
        var shell = new MainWindowViewModel(new EfRunner(), new AppSettings(), SettingsPath)
        {
            PostToUiThread = context is null
                ? action => action()
                : action => context.Post(_ => action(), null),
            PickFolderAsync = () => Task.FromResult<string?>(sample),
            ConfirmAsync = _ => Task.FromResult(true),
        };

        await shell.OpenFolderCommand.ExecuteAsync(null);

        // The fixture has InitialCreate applied and AddBlogUrl pending.
        Assert.Equal(1, shell.Migrations.AppliedCount);
        Assert.Equal(1, shell.Migrations.PendingCount);

        var tab = shell.Script;
        tab.RefreshOptions();
        tab.OutputFolder = Path.Combine(_root, "scripts");
        tab.Range = ScriptRange.Pending;

        Assert.False(tab.HasRangeWarning);
        Assert.Equal("BlogContext_InitialCreate-to-latest.sql", tab.FileName);

        await tab.GenerateCommand.ExecuteAsync(null);

        Assert.True(File.Exists(tab.GeneratedPath), shell.Session.LastResult?.Diagnostics);

        // Only AddBlogUrl's work: the Url column, and not the original table creation.
        Assert.Contains("Url", tab.Sql);
        Assert.DoesNotContain("CREATE TABLE \"Posts\"", tab.Sql);
    }
}
