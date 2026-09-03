using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EfAssist.Core;

namespace EfAssist.App.ViewModels;

/// <summary>One palette in the gallery: its name, and what it looks like on the half being edited.</summary>
/// <param name="Sample">
/// The palette's own colours, expanded the way the real theme expands them, so a tile can show the
/// accent against its own background rather than as a swatch on the settings window's.
/// </param>
public sealed record PaletteChoice(ThemePreset Preset, string Name, Theming.ThemeSample Sample);

/// <summary>
/// The settings screen. Every change saves immediately - there is no OK button. The variant and the
/// font sizes also take effect immediately; the colours cannot, so they are shown in a preview tile
/// and applied on the next start. See the remarks on <see cref="Theming"/> for why.
/// </summary>
/// <remarks>
/// Named for the appearance half it started as, and still owns it, but it is now the whole screen:
/// the category list, the search over it, the workspace defaults a new solution starts from, and the
/// export, import and reset actions. The rows for code, console, diagrams and the window bind
/// straight through to <see cref="MainWindowViewModel"/> and its tabs rather than being copied here,
/// so the settings screen and the controls on the workspace screen cannot disagree.
/// </remarks>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly DisplaySettings _display;
    private readonly Action _save;

    /// <summary>
    /// Suppresses persistence and re-application while the pickers are being reloaded from settings.
    /// Without it, filling in the three colours for a newly selected preset would write each one
    /// straight back as a user override, pinning the theme to the preset it just left.
    /// </summary>
    private bool _loading;

    /// <summary>
    /// The colours the app was actually started with. Compared against the current choice to decide
    /// whether a restart is owed, so reverting a change by hand clears the notice rather than leaving
    /// it up for the rest of the session.
    /// </summary>
    private readonly string _startedWith;

    /// <summary>
    /// The theme as the app started: the variant, the palette and both variants' overrides, so the
    /// whole experiment can be put back. Kept as values rather than as the live objects, which are
    /// what gets edited.
    /// </summary>
    private readonly AppTheme _startedTheme;

    private readonly ThemePreset _startedPreset;

    private readonly ThemeColours _startedLight;

    private readonly ThemeColours _startedDark;

    /// <summary>Parameterless constructor exists only for the XAML previewer.</summary>
    public SettingsViewModel() : this(new AppSettings(), () => { })
    {
    }

    public SettingsViewModel(AppSettings settings, Action save, string? settingsPath = null)
    {
        _settings = settings;
        _display = settings.Display;
        _save = save;
        SettingsFilePath = settingsPath ?? SettingsStore.DefaultPath;

        _theme = _display.Theme;
        _preset = _display.Preset;
        _uiFontSize = _display.UiFontSize;
        _editorFontSize = _display.EditorFontSize;
        _checkForUpdatesOnLaunch = _display.CheckForUpdatesOnLaunch;
        _startedWith = ColourSignature(_display);
        _startedTheme = _display.Theme;
        _startedPreset = _display.Preset;
        _startedLight = Copy(_display.LightColours);
        _startedDark = Copy(_display.DarkColours);

        // Start on whichever variant the user is actually looking at, so the first colour they change
        // is the one they can see change.
        _editingDark = _display.Theme switch
        {
            AppTheme.Light => false,
            AppTheme.Dark => true,
            _ => Application.Current?.ActualThemeVariant == ThemeVariant.Dark,
        };

        // Loading, so that filling the screen from the settings does not write every value it reads
        // straight back out again.
        _loading = true;
        try
        {
            LoadWorkspaceDefaults();
            LoadColours();
        }
        finally
        {
            _loading = false;
        }

        BuildPalettes();
    }

    // ---- The screen: categories and the search over them ----

    /// <summary>
    /// The category list, in the order it is offered. Ordered by how often a category is opened
    /// rather than alphabetically: theme first because it is what the screen is opened for, the file
    /// and reset actions last because they are the ones worth having to look for.
    /// </summary>
    public IReadOnlyList<SettingsSection> Sections { get; } =
    [
        new(SettingsCategory.Theme, "Theme", "A palette sets three colours per variant. Borders, panels, hover states and the accent's own shades are derived from them; errors and warnings keep their own colours, so a failure always reads as one.", "palette colour color variant dark light system accent background contrast default high nord dracula solarized github monokai owl one restart"),
        new(SettingsCategory.TextAndLayout, "Text and layout", "Sizes apply immediately, everywhere they are used.", "font size interface code window maximised"),
        new(SettingsCategory.CodeAndConsole, "Code and console", "The migration source, the generated SQL and the output console.", "wrap line numbers sort order"),
        new(SettingsCategory.WorkspaceDefaults, "Workspace defaults", "What a workspace's own settings start from the first time it is opened. A workspace you have already used keeps its own choices.", "discovery offline idempotent build script folder no-connect no-build"),
        new(SettingsCategory.Diagrams, "Diagrams", "The surface, its legend and its detail pane. A workspace you have switched keeps its own view.", "diagram legend corner detail entity relationship class"),
        new(SettingsCategory.Tools, "Tools", "dotnet-ef and the SDK this solution builds against. Output goes to the console on the workspace screen.", "dotnet-ef tool update sdk version"),
        new(SettingsCategory.Shortcuts, "Shortcuts", "F1 or Ctrl+/ opens this straight from the app.", "shortcuts keyboard keys gesture ctrl alt f1 f3 f5 escape enter"),
        new(SettingsCategory.About, "Updates and about", "The version, where the settings live, and how to move or forget them.", "update check launch version settings file export import reset everything github release"),
    ];

    /// <summary>Which pane is showing.</summary>
    [ObservableProperty]
    private SettingsCategory _category;

    /// <summary>
    /// The category list's selection. A separate property from <see cref="Category"/> so the list can
    /// bind two-way to an item while the panes switch on the enum, and so a search that hides the
    /// selected category can move the selection without the panes flickering through null.
    /// </summary>
    public SettingsSection? SelectedSection
    {
        get => Sections.FirstOrDefault(s => s.Category == Category);
        set
        {
            if (value is not null)
            {
                Category = value.Category;
            }
        }
    }

    /// <summary>
    /// What is typed in the search box. The filtering itself runs in the view: the rows are declared
    /// in XAML, so the view is the only thing that can hide and count them. See <c>SettingsSearch</c>.
    /// </summary>
    [ObservableProperty]
    private string _query = "";

    public bool IsSearching => !string.IsNullOrWhiteSpace(Query);

    [RelayCommand]
    private void ClearQuery() => Query = "";

    /// <summary>Opens the screen on the shortcut reference, for F1 and Ctrl+/.</summary>
    public void ShowShortcuts()
    {
        Query = "";
        Category = SettingsCategory.Shortcuts;
    }

    // ---- Theme ----

    /// <summary>Choices for the variant control, in the order they are offered.</summary>
    public static IReadOnlyList<AppTheme> Themes { get; } =
        [AppTheme.System, AppTheme.Light, AppTheme.Dark];

    public static IReadOnlyList<ThemePreset> Presets { get; } = ThemePresets.All;

    /// <summary>Choices for the diagram legend's corner.</summary>
    public static IReadOnlyList<SurfaceCorner> LegendCorners { get; } = Enum.GetValues<SurfaceCorner>();

    /// <summary>
    /// The variant as three booleans, for the segmented control. Radio buttons need one property
    /// each, and a converter cannot be two-way against an enum without a parameter.
    /// </summary>
    public bool VariantIsSystem
    {
        get => Theme == AppTheme.System;
        set => SetVariant(value, AppTheme.System);
    }

    public bool VariantIsLight
    {
        get => Theme == AppTheme.Light;
        set => SetVariant(value, AppTheme.Light);
    }

    public bool VariantIsDark
    {
        get => Theme == AppTheme.Dark;
        set => SetVariant(value, AppTheme.Dark);
    }

    /// <summary>
    /// Only the segment being turned on says anything: the one being turned off is the same event
    /// arriving from the other side, and acting on it would fight the new selection.
    /// </summary>
    private void SetVariant(bool selected, AppTheme variant)
    {
        if (selected)
        {
            Theme = variant;
        }
    }

    /// <summary>
    /// The palette gallery: every preset, sampled on the half being edited. Rebuilt when that half
    /// changes, which is the only thing that alters what a tile looks like.
    /// </summary>
    public ObservableCollection<PaletteChoice> Palettes { get; } = [];

    /// <summary>
    /// The selected tile. Reads through to <see cref="Preset"/> rather than holding its own value, so
    /// the gallery and the rest of the screen cannot disagree about which palette is chosen.
    /// </summary>
    public PaletteChoice? SelectedPalette
    {
        get => Palettes.FirstOrDefault(p => p.Preset == Preset);
        set
        {
            if (value is not null)
            {
                Preset = value.Preset;
            }
        }
    }

    public static double MinFontSize => ThemePresets.MinFontSize;

    public static double MaxFontSize => ThemePresets.MaxFontSize;

    /// <summary>Light, dark, or follow the OS.</summary>
    [ObservableProperty]
    private AppTheme _theme;

    /// <summary>Which palette the colours below start from.</summary>
    [ObservableProperty]
    private ThemePreset _preset;

    /// <summary>
    /// Which variant's colours the pickers are editing. Two sets rather than one, because a
    /// background that works on light is unreadable on dark — and a System user needs both.
    /// </summary>
    [ObservableProperty]
    private bool _editingDark;

    [ObservableProperty]
    private Color _background;

    [ObservableProperty]
    private Color _accent;

    [ObservableProperty]
    private Color _foreground;

    [ObservableProperty]
    private double _uiFontSize;

    [ObservableProperty]
    private double _editorFontSize;

    /// <summary>True once anything on the current variant differs from its preset value.</summary>
    public bool HasOverrides => !_display.ColoursFor(EditingDark).IsDefault;

    public string EditingVariantName => EditingDark ? "dark" : "light";

    /// <summary>
    /// True only for System, where the pickers could be editing either half and the screen has to say
    /// which. An explicit Light or Dark already answers the question.
    /// </summary>
    public bool CanChooseHalf => Theme == AppTheme.System;

    /// <summary>
    /// The colours as they will look once applied, for the preview tile. Replaced wholesale rather than
    /// updated member by member, so one notification repaints the whole sample. Plain colours rather
    /// than brushes: a brush is an <c>AvaloniaObject</c>, and building one here would claim Avalonia's
    /// dispatcher for whichever thread constructed the view model. The tile converts them.
    /// </summary>
    [ObservableProperty]
    private Theming.ThemeSample _preview = Sample(ThemePreset.Default, dark: false, overrides: null);

    /// <summary>
    /// True once the chosen colours differ from the ones the app started with. Drives the notice
    /// offering a restart - the colours are already saved either way.
    /// </summary>
    public bool NeedsRestart => ColourSignature(_display) != _startedWith;

    // ---- The legibility check on the three colours ----

    /// <summary>
    /// Contrast between the chosen text and background, on the WCAG ratio. Worth showing because the
    /// pickers will happily accept a pair nobody can read, and the preview tile alone does not say
    /// whether a marginal pair is merely dim or actually failing.
    /// </summary>
    public double TextContrast => Contrast(Preview.Text, Preview.Surface);

    /// <summary>Accent against the background, which is what a button or a selected row rests on.</summary>
    public double AccentContrast => Contrast(Preview.Accent, Preview.Surface);

    /// <summary>AA needs 4.5:1 for body text, AAA 7:1.</summary>
    public bool ContrastPasses => TextContrast >= 4.5;

    public string ContrastGrade => TextContrast >= 7 ? "AAA" : ContrastPasses ? "AA" : "Fails AA";

    public string ContrastRatioText => $"text on background, {TextContrast:0.0}:1";

    /// <summary>What to do about it, rather than only what is wrong.</summary>
    public string ContrastNote => !ContrastPasses
        ? "Body text under 4.5:1 is hard to read for a long session. Lighten the text or darken the background."
        : AccentContrast >= 3
            ? "The accent clears 3:1 against the background too."
            : "The accent is under 3:1 against the background, so buttons and the selected row will be hard to pick out.";

    // ---- Workspace defaults ----

    public static IReadOnlyList<DiscoveryMode> DiscoveryModes { get; } = Enum.GetValues<DiscoveryMode>();

    /// <inheritdoc cref="WorkspaceDefaults.Discovery"/>
    [ObservableProperty]
    private DiscoveryMode _defaultDiscovery;

    /// <inheritdoc cref="WorkspaceDefaults.MigrationRefresh"/>
    [ObservableProperty]
    private DiscoveryMode _defaultMigrationRefresh;

    /// <inheritdoc cref="WorkspaceDefaults.Offline"/>
    [ObservableProperty]
    private bool _defaultOffline;

    /// <inheritdoc cref="WorkspaceDefaults.Idempotent"/>
    [ObservableProperty]
    private bool _defaultIdempotent;

    /// <inheritdoc cref="WorkspaceDefaults.NoBuild"/>
    [ObservableProperty]
    private bool _defaultNoBuild;

    /// <inheritdoc cref="WorkspaceDefaults.ScriptOutputFolder"/>
    [ObservableProperty]
    private string? _defaultScriptFolder;

    /// <summary>What the folder row shows when nothing is set: the Save As dialog does the asking.</summary>
    public string ScriptFolderText =>
        string.IsNullOrWhiteSpace(DefaultScriptFolder) ? "Ask each time" : DefaultScriptFolder;

    public bool HasScriptFolder => !string.IsNullOrWhiteSpace(DefaultScriptFolder);

    /// <summary>Supplied by the view: picks the folder generated scripts are written to.</summary>
    public Func<Task<string?>>? PickFolderAsync { get; set; }

    [RelayCommand]
    private async Task ChooseScriptFolderAsync()
    {
        if (PickFolderAsync is null)
        {
            return;
        }

        if (await PickFolderAsync() is { Length: > 0 } folder)
        {
            DefaultScriptFolder = folder;
        }
    }

    [RelayCommand]
    private void ClearScriptFolder() => DefaultScriptFolder = null;

    // ---- Updates and about ----

    /// <summary>Where the core settings file lives, for the row that reveals it.</summary>
    public string SettingsFilePath { get; }

    /// <summary>
    /// Where the settings window was last left. Its own geometry rather than the main window's - see
    /// <see cref="DisplaySettings.SettingsWindow"/>.
    /// </summary>
    public WindowSettings WindowLayout => _display.SettingsWindow;

    /// <summary>
    /// Remembers the settings window's size as it closes. Size only, not position: it opens centred
    /// on the window it belongs to, so that a second monitor cannot end up holding the settings for
    /// an app running on the first. A minimised window is ignored, as it says nothing about how the
    /// user wants it.
    /// </summary>
    public void SaveWindowLayout(bool maximised, (double Width, double Height)? size)
    {
        var window = _display.SettingsWindow;
        window.Maximised = maximised;

        if (size is { } s && s.Width > 0 && s.Height > 0)
        {
            window.Width = s.Width;
            window.Height = s.Height;
        }

        _save();
    }

    /// <inheritdoc cref="DisplaySettings.CheckForUpdatesOnLaunch"/>
    [ObservableProperty]
    private bool _checkForUpdatesOnLaunch;

    /// <summary>
    /// The result of the last export, import or reset. One line, cleared by the next action: these
    /// are the only things on the screen that do not show their result by simply changing.
    /// </summary>
    [ObservableProperty]
    private string _actionMessage = "";

    /// <summary>Supplied by the view, which owns the storage provider and the modal windows.</summary>
    public Func<string, Task<string?>>? PickExportFileAsync { get; set; }

    public Func<Task<string?>>? PickImportFileAsync { get; set; }

    public Func<string, Task>? RevealFileAsync { get; set; }

    public Func<ConfirmRequest, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>
    /// Re-read everything from the settings after an import or a reset. Supplied by the shell, which
    /// owns the view models holding the other half of these preferences: replacing the values under
    /// them without telling them would leave the screen right and the app wrong.
    /// </summary>
    public Action? SettingsReplaced { get; set; }

    [RelayCommand]
    private async Task RevealSettingsFileAsync()
    {
        if (RevealFileAsync is not null)
        {
            await RevealFileAsync(SettingsFilePath);
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (PickExportFileAsync is null)
        {
            return;
        }

        if (await PickExportFileAsync("efassist-settings.json") is not { Length: > 0 } path)
        {
            return;
        }

        ActionMessage = SettingsStore.Export(_settings, path)
            ? $"Exported to {path}."
            : $"Could not write {path}.";
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (PickImportFileAsync is null)
        {
            return;
        }

        if (await PickImportFileAsync() is not { Length: > 0 } path)
        {
            return;
        }

        if (!SettingsStore.Import(_settings, path))
        {
            ActionMessage = $"{Path.GetFileName(path)} is not an EfAssist settings export.";
            return;
        }

        Reload();
        ActionMessage = $"Imported from {path}. Colours take effect on the next start.";
    }

    /// <summary>
    /// Everything app-wide back to how it shipped, including the recent list. Existing workspaces keep
    /// their own settings files - see <see cref="SettingsStore.Reset"/> - so this is not a way to lose
    /// a hand-arranged diagram.
    /// </summary>
    [RelayCommand]
    private async Task ResetEverythingAsync()
    {
        // No dialog available means no way to ask, and this is not an action to take unasked.
        if (ConfirmAsync is null)
        {
            return;
        }

        var confirmed = await ConfirmAsync(new ConfirmRequest(
            "Reset settings",
            "Puts the theme, colours, font sizes, workspace defaults and the recent list back to how EfAssist shipped.",
            "Reset everything",
            Detail: "Each workspace keeps its own remembered projects, migrations and diagrams. Colours take effect on the next start."));

        if (!confirmed)
        {
            return;
        }

        SettingsStore.Reset(_settings);
        Reload();
        ActionMessage = "Settings reset. Colours take effect on the next start.";
    }

    /// <summary>
    /// Re-reads the screen from the settings, applies what can be applied live, and tells the shell to
    /// do the same. Used after an import or a reset, which replace values this screen and half a dozen
    /// view models are already holding.
    /// </summary>
    private void Reload()
    {
        _loading = true;
        try
        {
            Theme = _display.Theme;
            Preset = _display.Preset;
            UiFontSize = _display.UiFontSize;
            EditorFontSize = _display.EditorFontSize;
            CheckForUpdatesOnLaunch = _display.CheckForUpdatesOnLaunch;
            LoadWorkspaceDefaults();
        }
        finally
        {
            _loading = false;
        }

        LoadColours();
        BuildPalettes();

        Theming.Apply(_display);
        _save();
        SettingsReplaced?.Invoke();
    }

    // ---- Appearance plumbing ----

    /// <summary>
    /// Identifies a colour configuration: the preset plus both variants' resolved colours. Both
    /// variants, because a System user's OS can flip to the one that was edited.
    /// </summary>
    private static string ColourSignature(DisplaySettings display)
    {
        var light = ThemePresets.Resolve(display.Preset, false, display.LightColours);
        var dark = ThemePresets.Resolve(display.Preset, true, display.DarkColours);

        return string.Join(
            '|',
            display.Preset,
            light.Background,
            light.Accent,
            light.Foreground,
            dark.Background,
            dark.Accent,
            dark.Foreground);
    }

    /// <summary>
    /// Drops the colour overrides for the variant on screen and puts the preset's own values back.
    /// Only that variant: someone who has tuned dark carefully should not lose it while tidying up
    /// light.
    /// </summary>
    [RelayCommand]
    private void ResetColours()
    {
        _display.ColoursFor(EditingDark).Clear();
        _save();
        LoadColours();
        BuildPalettes();
    }

    /// <summary>
    /// Abandons the theme experiment: the variant, the palette and both variants' overrides go back to
    /// what the app is running with, which clears the restart notice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The theme and nothing else. Font sizes, the code and console toggles, the workspace defaults and
    /// everything else on this screen are left exactly as they are — they are not what the notice is
    /// about, and several of them are already applied.
    /// </para>
    /// <para>
    /// Back to the running theme rather than to whatever was showing when this window opened, because
    /// the running colours are the ones the user can see and the ones "no restart needed" has to mean.
    /// A custom palette survives it: the overrides are restored as they were, not dropped.
    /// </para>
    /// </remarks>
    [RelayCommand]
    private void RevertColours()
    {
        _loading = true;
        try
        {
            Preset = _startedPreset;
            Restore(_display.LightColours, _startedLight);
            Restore(_display.DarkColours, _startedDark);
        }
        finally
        {
            _loading = false;
        }

        // Outside the loading guard: the variant is the one theme choice that applies to the running
        // app, so putting it back has to actually repaint.
        Theme = _startedTheme;

        _save();
        LoadColours();
        BuildPalettes();
    }

    private static ThemeColours Copy(ThemeColours colours) => new()
    {
        Background = colours.Background,
        Accent = colours.Accent,
        Foreground = colours.Foreground,
    };

    /// <summary>
    /// Copies values into the live overrides rather than replacing them: the block belongs to
    /// <see cref="DisplaySettings"/>, which hands the same instance to everything that paints.
    /// </summary>
    private static void Restore(ThemeColours target, ThemeColours from)
    {
        target.Background = from.Background;
        target.Accent = from.Accent;
        target.Foreground = from.Foreground;
    }

    /// <summary>Puts both font sizes back to the sizes the app shipped with.</summary>
    [RelayCommand]
    private void ResetFontSizes()
    {
        UiFontSize = ThemePresets.DefaultUiFontSize;
        EditorFontSize = ThemePresets.DefaultEditorFontSize;
    }

    /// <summary>
    /// Fills the pickers from the preset plus whatever the user has overridden for this variant.
    /// </summary>
    private void LoadColours()
    {
        var palette = ThemePresets.Resolve(Preset, EditingDark, _display.ColoursFor(EditingDark));
        var defaults = ThemePresets.Defaults(Preset, EditingDark);

        var loading = _loading;
        _loading = true;
        try
        {
            Background = Parse(palette.Background, defaults.Background);
            Accent = Parse(palette.Accent, defaults.Accent);
            Foreground = Parse(palette.Foreground, defaults.Foreground);
        }
        finally
        {
            _loading = loading;
        }

        RefreshDerived();
    }

    private void LoadWorkspaceDefaults()
    {
        var defaults = _settings.WorkspaceDefaults;

        DefaultDiscovery = defaults.Discovery;
        DefaultMigrationRefresh = defaults.MigrationRefresh;
        DefaultOffline = defaults.Offline;
        DefaultIdempotent = defaults.Idempotent;
        DefaultNoBuild = defaults.NoBuild;
        DefaultScriptFolder = defaults.ScriptOutputFolder;
    }

    /// <summary>
    /// Rebuilds the gallery for the half being edited. The tiles differ per half - a palette's dark
    /// side is a different set of three colours - so this runs whenever that changes rather than once.
    /// </summary>
    private void BuildPalettes()
    {
        Palettes.Clear();
        foreach (var preset in ThemePresets.All)
        {
            Palettes.Add(new PaletteChoice(
                preset,
                ThemePresets.DisplayName(preset),
                Sample(preset, EditingDark, overrides: null)));
        }

        OnPropertyChanged(nameof(SelectedPalette));
    }

    /// <summary>
    /// Recomputes everything that follows from the three colours: the preview, the contrast check,
    /// whether a restart is owed, and whether there is anything to reset.
    /// </summary>
    private void RefreshDerived()
    {
        Preview = Sample(Preset, EditingDark, _display.ColoursFor(EditingDark));

        OnPropertyChanged(nameof(HasOverrides));
        OnPropertyChanged(nameof(NeedsRestart));
    }

    /// <summary>The preview colours for one preset and variant, with the user's overrides applied.</summary>
    private static Theming.ThemeSample Sample(ThemePreset preset, bool dark, ThemeColours? overrides) =>
        Theming.Sample(
            ThemePresets.Resolve(preset, dark, overrides),
            ThemePresets.Defaults(preset, dark));

    private static Color Parse(string value, string fallback) =>
        Color.TryParse(value, out var colour) ? colour : Color.Parse(fallback);

    /// <summary>
    /// The WCAG contrast ratio between two opaque colours, 1:1 to 21:1. Its own implementation rather
    /// than <see cref="Theming.Luminance"/>, which is the cheap perceptual weighting used to pick
    /// black or white text; a pass or fail claim has to use the real curve.
    /// </summary>
    private static double Contrast(Color first, Color second)
    {
        var a = RelativeLuminance(first);
        var b = RelativeLuminance(second);

        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double RelativeLuminance(Color colour) =>
        (0.2126 * Linear(colour.R)) + (0.7152 * Linear(colour.G)) + (0.0722 * Linear(colour.B));

    private static double Linear(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    /// <summary>
    /// Records one colour against the variant being edited. A value equal to the preset's own is
    /// stored as "no override", so switching preset afterwards still moves it — matching a preset
    /// colour by hand should not pin it.
    /// </summary>
    private void StoreColour(Color value, string presetDefault, Action<ThemeColours, string?> write)
    {
        if (_loading)
        {
            return;
        }

        var hex = ToHex(value);
        write(_display.ColoursFor(EditingDark), SameColour(hex, presetDefault) ? null : hex);

        _save();
        RefreshDerived();
    }

    /// <summary>Compares a normalised hex against a preset value, which may be written any way.</summary>
    private static bool SameColour(string hex, string presetDefault) =>
        Color.TryParse(presetDefault, out var colour)
        && ToHex(colour).Equals(hex, StringComparison.OrdinalIgnoreCase);

    private static string ToHex(Color colour) => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    /// <summary>
    /// Hands the whole configuration, colours included, to Avalonia. Called once before the first
    /// window exists - the only moment Fluent will accept a palette.
    /// </summary>
    public void Initialise() => Theming.Initialise(_display);

    /// <summary>
    /// Applies what can be applied to a running app - the variant and the font sizes - and saves.
    /// Colour changes go through <see cref="_save"/> alone and wait for a restart.
    /// </summary>
    private void ApplyAndSave()
    {
        Theming.Apply(_display);
        _save();
    }

    partial void OnPreviewChanged(Theming.ThemeSample value)
    {
        OnPropertyChanged(nameof(TextContrast));
        OnPropertyChanged(nameof(AccentContrast));
        OnPropertyChanged(nameof(ContrastPasses));
        OnPropertyChanged(nameof(ContrastGrade));
        OnPropertyChanged(nameof(ContrastRatioText));
        OnPropertyChanged(nameof(ContrastNote));
    }

    partial void OnCategoryChanged(SettingsCategory value) => OnPropertyChanged(nameof(SelectedSection));

    partial void OnQueryChanged(string value) => OnPropertyChanged(nameof(IsSearching));

    partial void OnThemeChanged(AppTheme value)
    {
        _display.Theme = value;
        OnPropertyChanged(nameof(CanChooseHalf));
        OnPropertyChanged(nameof(VariantIsSystem));
        OnPropertyChanged(nameof(VariantIsLight));
        OnPropertyChanged(nameof(VariantIsDark));

        if (_loading)
        {
            return;
        }

        ApplyAndSave();

        // Following the OS keeps whichever variant was on screen; an explicit choice moves the
        // pickers to the variant the user just switched to.
        if (value is AppTheme.Light or AppTheme.Dark)
        {
            EditingDark = value == AppTheme.Dark;
        }
    }

    partial void OnPresetChanged(ThemePreset value)
    {
        _display.Preset = value;
        OnPropertyChanged(nameof(SelectedPalette));

        if (_loading)
        {
            return;
        }

        _save();

        // Fills the pickers from the new preset, which also refreshes the preview and the notice.
        LoadColours();
    }

    partial void OnEditingDarkChanged(bool value)
    {
        OnPropertyChanged(nameof(EditingVariantName));
        LoadColours();
        BuildPalettes();
    }

    partial void OnBackgroundChanged(Color value) => StoreColour(
        value, ThemePresets.Defaults(Preset, EditingDark).Background, (c, v) => c.Background = v);

    partial void OnAccentChanged(Color value) => StoreColour(
        value, ThemePresets.Defaults(Preset, EditingDark).Accent, (c, v) => c.Accent = v);

    partial void OnForegroundChanged(Color value) => StoreColour(
        value, ThemePresets.Defaults(Preset, EditingDark).Foreground, (c, v) => c.Foreground = v);

    partial void OnUiFontSizeChanged(double value)
    {
        _display.UiFontSize = ThemePresets.ClampFontSize(value, ThemePresets.DefaultUiFontSize);

        if (!_loading)
        {
            ApplyAndSave();
        }
    }

    partial void OnEditorFontSizeChanged(double value)
    {
        _display.EditorFontSize = ThemePresets.ClampFontSize(value, ThemePresets.DefaultEditorFontSize);

        if (!_loading)
        {
            ApplyAndSave();
        }
    }

    partial void OnCheckForUpdatesOnLaunchChanged(bool value)
    {
        _display.CheckForUpdatesOnLaunch = value;

        if (!_loading)
        {
            _save();
        }
    }

    partial void OnDefaultDiscoveryChanged(DiscoveryMode value) => StoreDefault(d => d.Discovery = value);

    partial void OnDefaultMigrationRefreshChanged(DiscoveryMode value) =>
        StoreDefault(d => d.MigrationRefresh = value);

    partial void OnDefaultOfflineChanged(bool value) => StoreDefault(d => d.Offline = value);

    partial void OnDefaultIdempotentChanged(bool value) => StoreDefault(d => d.Idempotent = value);

    partial void OnDefaultNoBuildChanged(bool value) => StoreDefault(d => d.NoBuild = value);

    partial void OnDefaultScriptFolderChanged(string? value)
    {
        OnPropertyChanged(nameof(ScriptFolderText));
        OnPropertyChanged(nameof(HasScriptFolder));

        StoreDefault(d => d.ScriptOutputFolder = string.IsNullOrWhiteSpace(value) ? null : value);
    }

    private void StoreDefault(Action<WorkspaceDefaults> write)
    {
        if (_loading)
        {
            return;
        }

        write(_settings.WorkspaceDefaults);
        _save();
    }
}
