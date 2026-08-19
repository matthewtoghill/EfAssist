using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EfMigrateHub.Core;

namespace EfMigrateHub.App.ViewModels;

/// <summary>
/// The appearance half of the settings screen. Every change saves immediately - there is no OK button.
/// The variant and the font sizes also take effect immediately; the colours cannot, so they are shown
/// in a preview tile and applied on the next start. See the remarks on <see cref="Theming"/> for why.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
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

    /// <summary>Parameterless constructor exists only for the XAML previewer.</summary>
    public SettingsViewModel() : this(new DisplaySettings(), () => { })
    {
    }

    public SettingsViewModel(DisplaySettings display, Action save)
    {
        _display = display;
        _save = save;

        _theme = display.Theme;
        _preset = display.Preset;
        _uiFontSize = display.UiFontSize;
        _editorFontSize = display.EditorFontSize;
        _startedWith = ColourSignature(display);

        // Start on whichever variant the user is actually looking at, so the first colour they change
        // is the one they can see change.
        _editingDark = display.Theme switch
        {
            AppTheme.Light => false,
            AppTheme.Dark => true,
            _ => Application.Current?.ActualThemeVariant == ThemeVariant.Dark,
        };

        LoadColours();
    }

    /// <summary>Choices for the variant dropdown, in the order they are offered.</summary>
    public static IReadOnlyList<AppTheme> Themes { get; } =
        [AppTheme.System, AppTheme.Light, AppTheme.Dark];

    public static IReadOnlyList<ThemePreset> Presets { get; } = ThemePresets.All;

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

        _loading = true;
        try
        {
            Background = Parse(palette.Background, defaults.Background);
            Accent = Parse(palette.Accent, defaults.Accent);
            Foreground = Parse(palette.Foreground, defaults.Foreground);
        }
        finally
        {
            _loading = false;
        }

        RefreshDerived();
    }

    /// <summary>
    /// Recomputes everything that follows from the three colours: the preview, whether a restart is
    /// owed, and whether there is anything to reset.
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

    partial void OnThemeChanged(AppTheme value)
    {
        _display.Theme = value;
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
        _save();

        // Fills the pickers from the new preset, which also refreshes the preview and the notice.
        LoadColours();
    }

    partial void OnEditingDarkChanged(bool value)
    {
        OnPropertyChanged(nameof(EditingVariantName));
        LoadColours();
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
        ApplyAndSave();
    }

    partial void OnEditorFontSizeChanged(double value)
    {
        _display.EditorFontSize = ThemePresets.ClampFontSize(value, ThemePresets.DefaultEditorFontSize);
        ApplyAndSave();
    }
}
