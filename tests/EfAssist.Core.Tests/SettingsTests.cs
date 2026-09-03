using EfAssist.Core;

namespace EfAssist.Core.Tests;

public class SettingsTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "EfAssistTests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void Missing_file_is_a_first_run_not_an_error()
    {
        var settings = SettingsStore.Load(SettingsPath);

        Assert.Empty(settings.Workspaces);
        Assert.Empty(settings.RecentWorkspaces);
    }

    [Fact]
    public void Round_trips_per_workspace_choices()
    {
        var settings = SettingsStore.Load(SettingsPath);
        var workspace = settings.For(@"C:\repos\Thing\Thing.slnx");
        workspace.StartupProject = @"C:\repos\Thing\src\Api\Api.csproj";
        workspace.MigrationsProject = @"C:\repos\Thing\src\Data\Data.csproj";
        workspace.Context = "BlogContext";
        workspace.ScriptOutputFolder = @"C:\repos\Thing\scripts";
        settings.MarkRecent(@"C:\repos\Thing\Thing.slnx");

        SettingsStore.Save(settings, SettingsPath);
        var reloaded = SettingsStore.Load(SettingsPath);

        var restored = reloaded.For(@"C:\repos\Thing\Thing.slnx");
        Assert.Equal(@"C:\repos\Thing\src\Api\Api.csproj", restored.StartupProject);
        Assert.Equal("BlogContext", restored.Context);
        Assert.Equal(@"C:\repos\Thing\scripts", restored.ScriptOutputFolder);
        Assert.Single(reloaded.RecentWorkspaces);
    }

    [Fact]
    public void Discovery_mode_defaults_to_cached_and_round_trips_as_a_name()
    {
        var settings = SettingsStore.Load(SettingsPath);

        // Cached, because the set of DbContext types in a solution rarely changes and discovery
        // costs a build. Also the enum's zero value, so an unset field agrees with this default.
        Assert.Equal(DiscoveryMode.Cached, settings.For(@"C:\repos\Thing\Thing.slnx").Discovery);
        Assert.Equal(DiscoveryMode.Cached, default(DiscoveryMode));

        settings.For(@"C:\repos\Thing\Thing.slnx").Discovery = DiscoveryMode.AutoNoBuildFirst;
        SettingsStore.Save(settings, SettingsPath);

        // Stored by name, so reordering the enum can't silently change a saved workspace's mode.
        Assert.Contains(
            "AutoNoBuildFirst",
            File.ReadAllText(SettingsStore.WorkspaceFile(_directory, @"C:\repos\Thing\Thing.slnx")));
        Assert.Equal(
            DiscoveryMode.AutoNoBuildFirst,
            SettingsStore.Load(SettingsPath).For(@"C:\repos\Thing\Thing.slnx").Discovery);
    }

    [Fact]
    public void Workspace_lookup_is_case_insensitive_and_path_normalised()
    {
        var settings = new AppSettings();
        settings.For(@"C:\repos\Thing\..\Thing\Thing.slnx").Context = "A";

        Assert.Equal("A", settings.For(@"c:\REPOS\thing\Thing.slnx").Context);
        Assert.Single(settings.Workspaces);
    }

    [Fact]
    public void Recent_list_is_most_recent_first_deduplicated_and_capped()
    {
        var settings = SettingsStore.Load(SettingsPath);
        settings.MarkRecent(@"C:\a\a.slnx", keep: 2);
        settings.MarkRecent(@"C:\b\b.slnx", keep: 2);
        settings.MarkRecent(@"C:\a\a.slnx", keep: 2);
        settings.MarkRecent(@"C:\c\c.slnx", keep: 2);

        Assert.Equal([@"C:\c\c.slnx", @"C:\a\a.slnx"], settings.RecentWorkspaces);
    }

    [Fact]
    public void Corrupt_file_is_set_aside_rather_than_crashing_or_being_silently_overwritten()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{ this is not json");

        var settings = SettingsStore.Load(SettingsPath);

        Assert.Empty(settings.Workspaces);
        Assert.True(File.Exists(SettingsPath + ".corrupt"), "the unreadable file should be preserved");
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        SettingsStore.Save(new AppSettings(), SettingsPath);

        Assert.True(File.Exists(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void Each_workspace_gets_its_own_file_and_the_core_file_holds_none_of_them()
    {
        var settings = SettingsStore.Load(SettingsPath);
        settings.For(@"C:\repos\One\One.slnx").Context = "OneContext";
        settings.For(@"C:\repos\Two\Two.slnx").Context = "TwoContext";
        settings.Display.WrapOutput = true;
        SettingsStore.Save(settings, SettingsPath);

        // The core file is app-wide preferences only. A workspace's remembered migration list is
        // bigger than everything else put together, which is the whole reason for the split.
        var core = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain("OneContext", core);
        Assert.DoesNotContain("TwoContext", core);
        Assert.Contains("WrapOutput", core);

        Assert.Equal(2, Directory.GetFiles(Path.Combine(_directory, "workspaces")).Length);

        var reloaded = SettingsStore.Load(SettingsPath);
        Assert.Equal("OneContext", reloaded.For(@"C:\repos\One\One.slnx").Context);
        Assert.Equal("TwoContext", reloaded.For(@"C:\repos\Two\Two.slnx").Context);
        Assert.True(reloaded.Display.WrapOutput);
    }

    [Fact]
    public void Viewer_and_window_preferences_default_sensibly_and_round_trip()
    {
        var settings = SettingsStore.Load(SettingsPath);

        // On, because that is how both viewers behaved before it was a choice. False is bool's
        // default, so this only holds while the property initialiser does.
        Assert.True(settings.Display.ShowLineNumbers);
        Assert.False(settings.Display.Window.Maximised);
        Assert.Null(settings.Display.Window.Width);

        settings.Display.ShowLineNumbers = false;
        settings.Display.Window.Maximised = true;
        settings.Display.Window.Width = 1400;
        settings.Display.Window.Height = 900;
        settings.Display.Window.X = -1200;
        settings.Display.Window.Y = 40;
        SettingsStore.Save(settings, SettingsPath);

        var reloaded = SettingsStore.Load(SettingsPath).Display;
        Assert.False(reloaded.ShowLineNumbers);
        Assert.True(reloaded.Window.Maximised);
        Assert.Equal(1400, reloaded.Window.Width);
        Assert.Equal(900, reloaded.Window.Height);

        // Negative on purpose: a second monitor left of the primary one has negative coordinates.
        Assert.Equal(-1200, reloaded.Window.X);
        Assert.Equal(40, reloaded.Window.Y);
    }

    [Fact]
    public void A_workspace_file_is_only_read_when_that_workspace_is_asked_for()
    {
        var settings = SettingsStore.Load(SettingsPath);
        settings.For(@"C:\repos\One\One.slnx").Context = "OneContext";
        settings.For(@"C:\repos\Two\Two.slnx").Context = "TwoContext";
        SettingsStore.Save(settings, SettingsPath);

        // Loading reads the core file and nothing else; startup cost stays flat however many
        // workspaces have ever been opened.
        var reloaded = SettingsStore.Load(SettingsPath);
        Assert.Empty(reloaded.Workspaces);

        reloaded.For(@"C:\repos\One\One.slnx");
        Assert.Single(reloaded.Workspaces);
    }

    [Fact]
    public void Same_named_solutions_in_different_repos_do_not_share_a_file()
    {
        var a = SettingsStore.WorkspaceFile(_directory, @"C:\repos\Alpha\Api.slnx");
        var b = SettingsStore.WorkspaceFile(_directory, @"C:\repos\Beta\Api.slnx");

        Assert.NotEqual(a, b);
        // Named for a human reading the folder, hashed so the paths can't collide.
        Assert.StartsWith("Api.slnx-", Path.GetFileName(a));
    }

    [Fact]
    public void A_new_workspace_starts_from_the_workspace_defaults()
    {
        var settings = SettingsStore.Load(SettingsPath);
        settings.WorkspaceDefaults.Discovery = DiscoveryMode.Manual;
        settings.WorkspaceDefaults.Idempotent = true;
        settings.WorkspaceDefaults.ScriptOutputFolder = @"C:\scripts";

        var workspace = settings.For(@"C:\repos\New\New.slnx");

        Assert.Equal(DiscoveryMode.Manual, workspace.Discovery);
        Assert.True(workspace.Idempotent);
        Assert.Equal(@"C:\scripts", workspace.ScriptOutputFolder);
    }

    [Fact]
    public void Changing_the_defaults_leaves_an_existing_workspace_alone()
    {
        var settings = SettingsStore.Load(SettingsPath);
        settings.For(@"C:\repos\Old\Old.slnx").Discovery = DiscoveryMode.Auto;
        SettingsStore.Save(settings, SettingsPath);

        var reloaded = SettingsStore.Load(SettingsPath);
        reloaded.WorkspaceDefaults.Discovery = DiscoveryMode.Manual;

        // A seed, not a policy: the workspace has a file of its own and keeps what it was left with.
        Assert.Equal(DiscoveryMode.Auto, reloaded.For(@"C:\repos\Old\Old.slnx").Discovery);
        Assert.Equal(DiscoveryMode.Manual, reloaded.For(@"C:\repos\Newer\Newer.slnx").Discovery);
    }

    [Fact]
    public void Export_and_import_carry_the_preferences_but_not_the_window_geometry()
    {
        var settings = SettingsStore.Load(SettingsPath);
        settings.Display.Preset = ThemePreset.Nord;
        settings.Display.UiFontSize = 17;
        settings.Display.Window.Maximised = true;
        settings.Display.Window.Width = 1234;
        settings.WorkspaceDefaults.NoBuild = true;

        var backup = Path.Combine(_directory, "backup.json");
        Assert.True(SettingsStore.Export(settings, backup));

        var target = new AppSettings();
        target.Display.Window.Width = 900;
        Assert.True(SettingsStore.Import(target, backup));

        Assert.Equal(ThemePreset.Nord, target.Display.Preset);
        Assert.Equal(17, target.Display.UiFontSize);
        Assert.True(target.Display.Window.Maximised);
        Assert.True(target.WorkspaceDefaults.NoBuild);
        // The exporting machine's screens are none of this machine's business.
        Assert.Equal(900, target.Display.Window.Width);
    }

    [Fact]
    public void Import_of_something_that_is_not_a_backup_changes_nothing()
    {
        var path = Path.Combine(_directory, "nonsense.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, """{ "somethingElse": 1 }""");

        var settings = new AppSettings { Display = { Preset = ThemePreset.Dracula } };

        Assert.False(SettingsStore.Import(settings, path));
        Assert.Equal(ThemePreset.Dracula, settings.Display.Preset);
    }

    [Fact]
    public void Reset_clears_preferences_defaults_and_the_recent_list_but_keeps_workspace_files()
    {
        var settings = SettingsStore.Load(SettingsPath);
        settings.Display.Preset = ThemePreset.GitHub;
        settings.Display.Window.Width = 1111;
        settings.WorkspaceDefaults.Offline = true;
        settings.For(@"C:\repos\Keep\Keep.slnx").Context = "KeepContext";
        settings.MarkRecent(@"C:\repos\Keep\Keep.slnx");
        SettingsStore.Save(settings, SettingsPath);

        SettingsStore.Reset(settings);
        SettingsStore.Save(settings, SettingsPath);

        var reloaded = SettingsStore.Load(SettingsPath);
        Assert.Equal(ThemePreset.Default, reloaded.Display.Preset);
        Assert.False(reloaded.WorkspaceDefaults.Offline);
        Assert.Empty(reloaded.RecentWorkspaces);
        Assert.Equal(1111, reloaded.Display.Window.Width);
        // Resetting what a new workspace starts from is not the same as forgetting the old ones.
        Assert.Equal("KeepContext", reloaded.For(@"C:\repos\Keep\Keep.slnx").Context);
    }

    [Fact]
    public void A_corrupt_workspace_file_falls_back_to_defaults_without_losing_the_core_file()
    {
        var settings = SettingsStore.Load(SettingsPath);
        settings.For(@"C:\repos\One\One.slnx").Context = "OneContext";
        SettingsStore.Save(settings, SettingsPath);

        var workspaceFile = SettingsStore.WorkspaceFile(_directory, @"C:\repos\One\One.slnx");
        File.WriteAllText(workspaceFile, "{ not json");

        var reloaded = SettingsStore.Load(SettingsPath);
        Assert.Null(reloaded.For(@"C:\repos\One\One.slnx").Context);
        Assert.True(File.Exists(workspaceFile + ".corrupt"), "the unreadable file should be preserved");
        Assert.True(File.Exists(SettingsPath), "one bad workspace must not take the core file with it");
    }
}
