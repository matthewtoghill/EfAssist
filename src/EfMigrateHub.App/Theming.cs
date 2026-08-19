using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using EfMigrateHub.Core;

namespace EfMigrateHub.App;

/// <summary>
/// Turns the three colours and two font sizes in <see cref="DisplaySettings"/> into the resources
/// Fluent actually paints with.
/// </summary>
/// <remarks>
/// <para>
/// Fluent builds every one of its brushes from a small palette of base colours, and exposes that
/// palette as <see cref="ColorPaletteResources"/>. Feeding it there rather than overriding brushes
/// one by one is what makes a custom background reach the inside of a ComboBox dropdown, not just
/// the window behind it.
/// </para>
/// <para>
/// The palette can only be set once, before the theme loads, which is why <see cref="Initialise"/> is
/// separate from <see cref="Apply"/>. Fluent reads its palette while loading, so nothing short of a
/// fresh <see cref="FluentTheme"/> repaints a running app - and swapping the theme re-applies every
/// control template, which leaves each ComboBox popup presenter attached to a template that has been
/// discarded. The next time that dropdown opens, Avalonia throws "already has a visual parent". Open
/// upstream with no fix, and the only suggested workaround drops the ScrollViewer from the ComboBox
/// theme, which would cost scrolling in the project and context lists: AvaloniaUI/Avalonia#17917 and
/// #15115. So colours apply at startup and a change asks for a restart, while the variant and the font
/// sizes need no reload and stay live.
/// </para>
/// </remarks>
public static class Theming
{
    /// <summary>
    /// Alpha steps Fluent's Base/Alt families use, from High down to Low. WinUI defines them as
    /// literal ARGB values; expressed as alphas over the two anchor colours they reproduce exactly.
    /// </summary>
    private const double High = 1.0;
    private const double MediumHigh = 0.8;
    private const double Medium = 0.6;
    private const double MediumLow = 0.4;
    private const double Low = 0.2;

    /// <summary>
    /// Hands Fluent its palette, then applies everything <see cref="Apply"/> does. Must run before the
    /// first window is constructed: that is the only point at which the palette is read, and it also
    /// means a dark-theme user never sees a white flash.
    /// </summary>
    public static void Initialise(DisplaySettings display)
    {
        if (Application.Current is { } app)
        {
            for (var i = 0; i < app.Styles.Count; i++)
            {
                if (app.Styles[i] is FluentTheme fluent)
                {
                    // Both variants, not only the active one: a System user's OS can flip while the app
                    // is running, and the palette it flips to has to already be right.
                    fluent.Palettes[ThemeVariant.Light] = BuildPalette(display, dark: false);
                    fluent.Palettes[ThemeVariant.Dark] = BuildPalette(display, dark: true);
                    break;
                }
            }
        }

        Apply(display);
    }

    /// <summary>
    /// Applies the part of the display configuration that can change while the app runs: the variant
    /// and the font sizes. Colours are not here - see the remarks on this class. Does nothing when
    /// there is no application, so view models constructed directly in tests and in the XAML previewer
    /// can set theme properties freely.
    /// </summary>
    public static void Apply(DisplaySettings display)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        app.RequestedThemeVariant = display.Theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            // Default, not a guess at what the OS wants: Avalonia already tracks that, including
            // changes made while the app is running.
            _ => ThemeVariant.Default,
        };

        ApplyFontSizes(app, display);
    }

    /// <summary>
    /// The sizes the XAML asks for by name. Derived from one base so the relative hierarchy — hint
    /// smaller than body, heading larger — survives any base the user picks, rather than collapsing
    /// at small sizes the way fixed offsets from a hard-coded 14 would.
    /// </summary>
    private static void ApplyFontSizes(Application app, DisplaySettings display)
    {
        var ui = ThemePresets.ClampFontSize(display.UiFontSize, ThemePresets.DefaultUiFontSize);
        var mono = ThemePresets.ClampFontSize(display.EditorFontSize, ThemePresets.DefaultEditorFontSize);

        // Fluent's own control themes read this one, so it is what scales buttons, tabs and text
        // boxes. The rest are ours.
        app.Resources["ControlContentThemeFontSize"] = ui;

        app.Resources["AppFontSize"] = ui;
        app.Resources["AppFontSizeSmall"] = Math.Max(ThemePresets.MinFontSize, ui - 2);
        app.Resources["AppFontSizeTiny"] = Math.Max(ThemePresets.MinFontSize, ui - 3);
        app.Resources["AppFontSizeLarge"] = ui + 1;
        app.Resources["AppFontSizeHeading"] = ui + 6;
        app.Resources["AppFontSizeMono"] = mono;
    }

    /// <summary>
    /// Expands three colours into Fluent's palette. An override that is not a colour falls
    /// back to the preset's value for that slot, so a hand-edited settings file cannot leave the app
    /// unpaintable.
    /// </summary>
    private static ColorPaletteResources BuildPalette(DisplaySettings display, bool dark)
    {
        var palette = new ColorPaletteResources();

        var defaults = ThemePresets.Defaults(display.Preset, dark);
        var chosen = ThemePresets.Resolve(display.Preset, dark, display.ColoursFor(dark));

        var background = Parse(chosen.Background, defaults.Background);
        var accent = Parse(chosen.Accent, defaults.Accent);
        var foreground = Parse(chosen.Foreground, defaults.Foreground);

        palette.Accent = accent;

        // The window surface.
        palette.RegionColor = background;

        // "Alt" is the background family: opaque at High, progressively more transparent so
        // whatever sits behind shows through.
        palette.AltHigh = Fade(background, High);
        palette.AltMediumHigh = Fade(background, MediumHigh);
        palette.AltMedium = Fade(background, Medium);
        palette.AltMediumLow = Fade(background, MediumLow);
        palette.AltLow = Fade(background, Low);

        // "Base" is the foreground family, on the same steps. BaseMedium is what muted text and
        // most borders end up as.
        palette.BaseHigh = Fade(foreground, High);
        palette.BaseMediumHigh = Fade(foreground, MediumHigh);
        palette.BaseMedium = Fade(foreground, Medium);
        palette.BaseMediumLow = Fade(foreground, MediumLow);
        palette.BaseLow = Fade(foreground, Low);

        // List hover and press washes. Deliberately faint: they sit under text that must stay
        // readable through them.
        palette.ListLow = Fade(foreground, 0.10);
        palette.ListMedium = Fade(foreground, 0.20);

        // "Chrome" is control fills, which are opaque and sit a little way from the background
        // towards the foreground. The fractions are read off WinUI's own light and dark values,
        // averaged where the two disagree, so stock colours land close to stock Fluent.
        palette.ChromeLow = Mix(background, foreground, 0.07);
        palette.ChromeMedium = Mix(background, foreground, 0.11);
        palette.ChromeMediumLow = Mix(background, foreground, 0.14);
        palette.ChromeHigh = Mix(background, foreground, 0.35);
        palette.ChromeAltLow = Mix(background, foreground, 0.88);
        palette.ChromeDisabledHigh = Mix(background, foreground, 0.20);
        palette.ChromeDisabledLow = Mix(background, foreground, 0.35);
        palette.ChromeGray = Mix(background, foreground, 0.48);

        // Fluent uses ChromeWhite for text and glyphs drawn on top of the accent, so it has to
        // contrast with the accent rather than literally be white — a pale accent with white
        // text on it is unreadable. Falls out as white for any ordinary dark accent.
        palette.ChromeWhite = Contrasting(accent);

        // Left as the fixed anchors Fluent expects: these back drop shadows and scrim overlays,
        // which are black regardless of theme.
        palette.ChromeBlackHigh = Colors.Black;
        palette.ChromeBlackMedium = Fade(Colors.Black, MediumHigh);
        palette.ChromeBlackMediumLow = Fade(Colors.Black, MediumLow);
        palette.ChromeBlackLow = Fade(Colors.Black, Low);

        // Validation red. Not derived from the accent on purpose: an error has to read as an
        // error whatever the user has chosen.
        palette.ErrorText = dark ? Color.Parse("#FF99A4") : Color.Parse("#C42B1C");

        return palette;
    }

    /// <summary>
    /// The handful of colours a preview of a palette needs. Derived by the same rules
    /// <see cref="BuildPalette"/> uses, so a sample cannot drift from what a restart will produce.
    /// Opaque throughout: a preview tile is composited over the settings window rather than over the
    /// new surface, so the alpha-based members of Fluent's palette are flattened against it here.
    /// </summary>
    public sealed record ThemeSample(
        Color Surface,
        Color Panel,
        Color Border,
        Color Accent,
        Color OnAccent,
        Color Text,
        Color MutedText);

    /// <summary>
    /// Expands three colours into what a preview needs, falling back per slot the same way the palette
    /// does so an unparseable override cannot blank the sample.
    /// </summary>
    public static ThemeSample Sample(ThemePalette palette, ThemePalette fallback)
    {
        var background = Parse(palette.Background, fallback.Background);
        var accent = Parse(palette.Accent, fallback.Accent);
        var foreground = Parse(palette.Foreground, fallback.Foreground);

        return new ThemeSample(
            Surface: background,
            // ChromeLow, which is what panel and control fills become.
            Panel: Mix(background, foreground, 0.07),
            // BaseLow over the surface: Fluent's border, flattened.
            Border: Mix(background, foreground, Low),
            Accent: accent,
            OnAccent: Contrasting(accent),
            Text: foreground,
            // BaseMedium over the surface, which is where hint and label text lands.
            MutedText: Mix(background, foreground, Medium));
    }

    private static Color Parse(string value, string fallback) =>
        Color.TryParse(value, out var colour) ? colour : Color.Parse(fallback);

    /// <summary>The colour at the given alpha, ignoring any alpha it already carried.</summary>
    private static Color Fade(Color colour, double alpha) =>
        Color.FromArgb((byte)Math.Round(alpha * 255), colour.R, colour.G, colour.B);

    /// <summary>Opaque blend, <paramref name="amount"/> of the way from <paramref name="from"/>.</summary>
    private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
        (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
        (byte)Math.Round(from.B + ((to.B - from.B) * amount)));

    /// <summary>
    /// Black or white, whichever is more readable on the given colour. The 0.55 threshold rather
    /// than 0.5 is because white text on a mid tone reads worse than black does.
    /// </summary>
    public static Color Contrasting(Color colour) =>
        Luminance(colour) > 0.55 ? Colors.Black : Colors.White;

    /// <summary>Relative luminance, 0 to 1, on the usual perceptual weighting.</summary>
    public static double Luminance(Color colour) =>
        ((0.2126 * colour.R) + (0.7152 * colour.G) + (0.0722 * colour.B)) / 255.0;
}
