using Avalonia.Media;
using EfAssist.App.ViewModels;
using EfAssist.Core;

namespace EfAssist.Core.Tests;

/// <summary>
/// The settings screen's own logic: the palette gallery, the legibility check, the workspace defaults
/// and what a reset does. The search itself lives in the view — see <c>Views/SettingsSearch.cs</c> —
/// because the rows are declared in XAML.
/// </summary>
public class SettingsViewModelTests
{
    private static SettingsViewModel Create(AppSettings settings, Action? save = null) =>
        new(settings, save ?? (() => { }));

    [Fact]
    public void The_gallery_offers_every_palette_and_follows_the_half_being_edited()
    {
        var settings = Create(new AppSettings());

        Assert.Equal(ThemePresets.All.Count, settings.Palettes.Count);

        settings.EditingDark = false;
        var light = settings.Palettes.Single(p => p.Preset == ThemePreset.Nord).Sample;

        settings.EditingDark = true;
        var dark = settings.Palettes.Single(p => p.Preset == ThemePreset.Nord).Sample;

        // A palette's two halves are different sets of three colours, so a tile has to be rebuilt
        // rather than reused when the half changes.
        Assert.NotEqual(light.Surface, dark.Surface);
    }

    [Fact]
    public void Selecting_a_tile_is_the_same_choice_as_the_preset()
    {
        var app = new AppSettings();
        var settings = Create(app);

        settings.SelectedPalette = settings.Palettes.Single(p => p.Preset == ThemePreset.GitHub);

        Assert.Equal(ThemePreset.GitHub, settings.Preset);
        Assert.Equal(ThemePreset.GitHub, app.Display.Preset);
        Assert.Equal(ThemePreset.GitHub, settings.SelectedPalette?.Preset);
    }

    [Fact]
    public void High_contrast_grades_aaa_and_an_unreadable_pair_fails()
    {
        var settings = Create(new AppSettings());
        settings.EditingDark = false;
        settings.Preset = ThemePreset.HighContrast;

        Assert.Equal("AAA", settings.ContrastGrade);
        Assert.True(settings.ContrastPasses);

        // Mid grey text on its own background: the pickers accept it, and nobody can read it.
        settings.Background = Color.Parse("#808080");
        settings.Foreground = Color.Parse("#8A8A8A");

        Assert.False(settings.ContrastPasses);
        Assert.Equal("Fails AA", settings.ContrastGrade);
        Assert.Contains("hard to read", settings.ContrastNote);
    }

    [Fact]
    public void A_legible_pair_with_a_washed_out_accent_still_says_so()
    {
        var settings = Create(new AppSettings());
        settings.EditingDark = false;

        settings.Background = Color.Parse("#FFFFFF");
        settings.Foreground = Color.Parse("#101010");
        settings.Accent = Color.Parse("#F2F2F2");

        Assert.True(settings.ContrastPasses);
        Assert.Contains("accent", settings.ContrastNote);
    }

    [Fact]
    public void Workspace_defaults_write_through_and_save()
    {
        var app = new AppSettings();
        var saves = 0;
        var settings = Create(app, () => saves++);

        settings.DefaultDiscovery = DiscoveryMode.Manual;
        settings.DefaultNoBuild = true;
        settings.DefaultScriptFolder = @"C:\scripts";

        Assert.Equal(DiscoveryMode.Manual, app.WorkspaceDefaults.Discovery);
        Assert.True(app.WorkspaceDefaults.NoBuild);
        Assert.Equal(@"C:\scripts", app.WorkspaceDefaults.ScriptOutputFolder);
        Assert.Equal(3, saves);

        // An empty box means "ask each time", not a folder named "".
        settings.DefaultScriptFolder = "   ";
        Assert.Null(app.WorkspaceDefaults.ScriptOutputFolder);
        Assert.Equal("Ask each time", settings.ScriptFolderText);
    }

    [Fact]
    public void Loading_the_screen_shows_the_saved_workspace_defaults_without_resaving_them()
    {
        var app = new AppSettings();
        app.WorkspaceDefaults.Discovery = DiscoveryMode.AutoNoBuildFirst;
        app.WorkspaceDefaults.Idempotent = true;

        var saves = 0;
        var settings = Create(app, () => saves++);

        Assert.Equal(DiscoveryMode.AutoNoBuildFirst, settings.DefaultDiscovery);
        Assert.True(settings.DefaultIdempotent);
        Assert.Equal(0, saves);
    }

    [Fact]
    public async Task Reset_puts_everything_back_and_tells_the_shell()
    {
        var app = new AppSettings();
        app.Display.Preset = ThemePreset.Dracula;
        app.Display.UiFontSize = 20;
        app.WorkspaceDefaults.Offline = true;
        app.RecentWorkspaces.Add(@"C:\repos\Thing\Thing.slnx");

        var replaced = 0;
        var settings = Create(app);
        settings.SettingsReplaced = () => replaced++;
        settings.ConfirmAsync = _ => Task.FromResult(true);

        await settings.ResetEverythingCommand.ExecuteAsync(null);

        Assert.Equal(ThemePreset.Default, settings.Preset);
        Assert.Equal(ThemePresets.DefaultUiFontSize, settings.UiFontSize);
        Assert.False(settings.DefaultOffline);
        Assert.Empty(app.RecentWorkspaces);
        Assert.Equal(1, replaced);
        Assert.Contains("reset", settings.ActionMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reset_does_nothing_when_the_confirmation_is_declined()
    {
        var app = new AppSettings { Display = { Preset = ThemePreset.Nord } };
        var settings = Create(app);
        settings.ConfirmAsync = _ => Task.FromResult(false);

        await settings.ResetEverythingCommand.ExecuteAsync(null);

        Assert.Equal(ThemePreset.Nord, app.Display.Preset);
        Assert.Equal("", settings.ActionMessage);
    }

    [Fact]
    public void F1_opens_the_shortcut_reference_and_drops_any_search()
    {
        var settings = Create(new AppSettings());
        settings.Query = "wrap";

        settings.ShowShortcuts();

        Assert.Equal(SettingsCategory.Shortcuts, settings.Category);
        Assert.Equal("", settings.Query);
        Assert.False(settings.IsSearching);
        Assert.Equal(SettingsCategory.Shortcuts, settings.SelectedSection?.Category);
    }

    [Fact]
    public void Every_category_has_a_section_and_every_section_a_pane_heading()
    {
        var settings = Create(new AppSettings());

        Assert.Equal(
            Enum.GetValues<SettingsCategory>().Length,
            settings.Sections.Select(s => s.Category).Distinct().Count());

        Assert.All(settings.Sections, section => Assert.False(string.IsNullOrWhiteSpace(section.Title)));
    }

    [Fact]
    public void Undoing_a_colour_experiment_restores_the_palette_and_its_custom_colours()
    {
        var app = new AppSettings();
        app.Display.Preset = ThemePreset.Nord;
        app.Display.DarkColours.Accent = "#112233";

        var settings = Create(app);
        settings.EditingDark = true;

        Assert.False(settings.NeedsRestart);

        settings.Preset = ThemePreset.MonokaiPro;
        settings.Accent = Color.Parse("#FF0000");
        Assert.True(settings.NeedsRestart);

        settings.RevertColoursCommand.Execute(null);

        Assert.Equal(ThemePreset.Nord, settings.Preset);
        Assert.Equal(ThemePreset.Nord, app.Display.Preset);
        // A palette that had been tuned by hand comes back tuned, not stripped.
        Assert.Equal("#112233", app.Display.DarkColours.Accent);
        Assert.Equal(Color.Parse("#112233"), settings.Accent);
        Assert.False(settings.NeedsRestart);
    }

    [Fact]
    public void Undoing_leaves_a_palette_that_had_no_overrides_with_none()
    {
        var app = new AppSettings { Display = { Preset = ThemePreset.GitHub } };
        var settings = Create(app);
        settings.EditingDark = false;

        settings.Background = Color.Parse("#123456");
        Assert.True(settings.HasOverrides);

        settings.RevertColoursCommand.Execute(null);

        Assert.False(settings.HasOverrides);
        Assert.True(app.Display.LightColours.IsDefault);
        Assert.False(settings.NeedsRestart);
    }
}
