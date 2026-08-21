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
