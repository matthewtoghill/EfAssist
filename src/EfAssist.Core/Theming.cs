using System.Text.Json.Serialization;

namespace EfAssist.Core;

/// <summary>
/// A named starting point for the three colours a user can set. Orthogonal to
/// <see cref="AppTheme"/>: every preset has a light and a dark half, so "Solarized" plus
/// "follow the OS" is Solarized Light by day and Solarized Dark by night.
/// </summary>
public enum ThemePreset
{
    /// <summary>Default, and first so that <c>default(ThemePreset)</c> matches it: stock Fluent.</summary>
    Default,
    HighContrast,
    Solarized,
    Nord,
    Dracula,
    OneDarkPro,
    NightOwl,
    GitHub,
    MonokaiPro,
}

/// <summary>
/// The three colours a theme is built from, as <c>#RRGGBB</c> strings. Everything else the UI needs
/// — control fills, borders, hover states, accent shades — is derived from these, so a preset is
/// three values rather than a table of thirty.
/// </summary>
/// <param name="Background">The window surface. Also the base for panel and control fills.</param>
/// <param name="Accent">Selection, focus and the primary button.</param>
/// <param name="Foreground">Body text. Also the base for borders and muted text.</param>
public sealed record ThemePalette(string Background, string Accent, string Foreground);

/// <summary>
/// One variant's user overrides. Null means "whatever the preset says", which is what lets a preset
/// change afterwards still move the colours the user never touched.
/// </summary>
public sealed class ThemeColours
{
    public string? Background { get; set; }

    public string? Accent { get; set; }

    public string? Foreground { get; set; }

    /// <summary>
    /// Convenience for callers, not state. Without this it serialises into the settings file as a
    /// property that reads back as nothing.
    /// </summary>
    [JsonIgnore]
    public bool IsDefault => Background is null && Accent is null && Foreground is null;

    public void Clear()
    {
        Background = null;
        Accent = null;
        Foreground = null;
    }
}

/// <summary>
/// The shipped palettes, and the rule for combining one with a user's overrides.
/// </summary>
public static class ThemePresets
{
    /// <summary>Smallest and largest font size the settings screen will accept, in points.</summary>
    public const double MinFontSize = 9;
    public const double MaxFontSize = 28;

    /// <summary>Fluent's own <c>ControlContentThemeFontSize</c>, so the default changes nothing.</summary>
    public const double DefaultUiFontSize = 14;

    /// <summary>Matches the size the code and console panes were hard-coded to before this existed.</summary>
    public const double DefaultEditorFontSize = 12;

    /// <summary>In the order the settings screen offers them.</summary>
    public static IReadOnlyList<ThemePreset> All { get; } =
        [
            ThemePreset.Default,
            ThemePreset.HighContrast,
            ThemePreset.Solarized,
            ThemePreset.Nord,
            ThemePreset.Dracula,
            ThemePreset.OneDarkPro,
            ThemePreset.NightOwl,
            ThemePreset.GitHub,
            ThemePreset.MonokaiPro,
        ];

    /// <summary>
    /// What the settings screen shows. Member names alone do not survive a general spacing rule:
    /// "GitHub" is one word and "OneDarkPro" is a product name, neither of which sentence case gets
    /// right.
    /// </summary>
    public static string DisplayName(ThemePreset preset) => preset switch
    {
        ThemePreset.HighContrast => "High contrast",
        ThemePreset.OneDarkPro => "One Dark Pro",
        ThemePreset.NightOwl => "Night Owl",
        ThemePreset.GitHub => "GitHub",
        ThemePreset.MonokaiPro => "Monokai Pro",
        _ => preset.ToString(),
    };

    /// <summary>The preset's own values for one variant, before any user override.</summary>
    public static ThemePalette Defaults(ThemePreset preset, bool dark) => (preset, dark) switch
    {
        // Stock Fluent: #202020 is the surface Fluent's own dark variant uses.
        (ThemePreset.Default, false) => new ThemePalette("#FFFFFF", "#0078D4", "#1A1A1A"),
        (ThemePreset.Default, true) => new ThemePalette("#202020", "#4CA6FF", "#F2F2F2"),

        // Pure black on pure white, and an accent that stays distinguishable at either extreme.
        (ThemePreset.HighContrast, false) => new ThemePalette("#FFFFFF", "#0000CC", "#000000"),
        (ThemePreset.HighContrast, true) => new ThemePalette("#000000", "#FFD700", "#FFFFFF"),

        (ThemePreset.Solarized, false) => new ThemePalette("#FDF6E3", "#268BD2", "#586E75"),
        (ThemePreset.Solarized, true) => new ThemePalette("#002B36", "#268BD2", "#93A1A1"),

        (ThemePreset.Nord, false) => new ThemePalette("#ECEFF4", "#5E81AC", "#2E3440"),
        (ThemePreset.Nord, true) => new ThemePalette("#2E3440", "#88C0D0", "#D8DEE9"),

        // Dracula's light half is its own Alucard variant, not an inversion.
        (ThemePreset.Dracula, false) => new ThemePalette("#F8F8F2", "#7B39C4", "#1F1F1F"),
        (ThemePreset.Dracula, true) => new ThemePalette("#282A36", "#BD93F9", "#F8F8F2"),

        // One Dark Pro, paired with Atom's One Light that the theme descends from.
        (ThemePreset.OneDarkPro, false) => new ThemePalette("#FAFAFA", "#3E7EE6", "#383A42"),
        (ThemePreset.OneDarkPro, true) => new ThemePalette("#282C34", "#61AFEF", "#ABB2BF"),

        // Night Owl, with its companion Light Owl.
        (ThemePreset.NightOwl, false) => new ThemePalette("#FBFBFB", "#2AA298", "#403F53"),
        (ThemePreset.NightOwl, true) => new ThemePalette("#011627", "#7FDBCA", "#D6DEEB"),

        // GitHub's own light and dark.
        (ThemePreset.GitHub, false) => new ThemePalette("#FFFFFF", "#0969DA", "#1F2328"),
        (ThemePreset.GitHub, true) => new ThemePalette("#0D1117", "#4493F8", "#E6EDF3"),

        // Monokai Pro, and its Light filter. The accent is the theme's pink rather than its yellow
        (ThemePreset.MonokaiPro, false) => new ThemePalette("#FAF4F2", "#C03E7A", "#29242A"),
        (ThemePreset.MonokaiPro, true) => new ThemePalette("#2D2A2E", "#FF6188", "#FCFCFA"),

        _ => new ThemePalette("#FFFFFF", "#0078D4", "#1A1A1A"),
    };

    /// <summary>
    /// The preset's values with any override laid over the top. Overrides are not validated here —
    /// a value that is not a colour is dropped by the caller that has to parse it.
    /// </summary>
    public static ThemePalette Resolve(ThemePreset preset, bool dark, ThemeColours? overrides)
    {
        var defaults = Defaults(preset, dark);
        if (overrides is null)
        {
            return defaults;
        }

        return new ThemePalette(
            overrides.Background ?? defaults.Background,
            overrides.Accent ?? defaults.Accent,
            overrides.Foreground ?? defaults.Foreground);
    }

    /// <summary>
    /// Keeps a font size inside a range the layout survives. A hand-edited settings file is the
    /// realistic source of a 400pt value, so this runs on load rather than only on the spinner.
    /// </summary>
    public static double ClampFontSize(double size, double fallback) =>
        double.IsFinite(size) ? Math.Clamp(size, MinFontSize, MaxFontSize) : fallback;
}
