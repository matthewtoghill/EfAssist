using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using EfAssist.Core.Diagrams;

namespace EfAssist.App;

/// <summary>
/// Resolves a <see cref="DiagramRole"/> to a brush from the current theme, and measures text in the
/// fonts the surface actually draws with.
/// </summary>
/// <remarks>
/// <para>
/// The scene carries roles rather than colours precisely so this lookup happens at draw time. Baking
/// a colour into the scene would repeat the trap recorded in <c>docs/dev/ROADMAP.md</c> for the SQL
/// syntax definitions: a literal colour is not a theme resource, so nothing repaints it when the
/// variant changes and the diagram keeps its light-theme text on a dark background.
/// </para>
/// <para>
/// Deliberately re-resolved rather than cached across a theme switch. The brushes come from
/// <c>App.axaml</c>'s <c>ThemeDictionaries</c>, and a cache would be the thing that goes stale.
/// </para>
/// </remarks>
public sealed class DiagramTheme
{
    private static readonly Dictionary<DiagramRole, string> Keys = new()
    {
        [DiagramRole.NodeBackground] = "DiagramNodeBackgroundBrush",
        [DiagramRole.NodeBorder] = "DiagramNodeBorderBrush",
        [DiagramRole.HeaderBackground] = "DiagramHeaderBackgroundBrush",
        [DiagramRole.HeaderText] = "DiagramHeaderTextBrush",
        [DiagramRole.SubtitleText] = "DiagramSubtitleTextBrush",
        [DiagramRole.Text] = "DiagramTextBrush",
        [DiagramRole.MutedText] = "DiagramMutedTextBrush",
        [DiagramRole.KeyText] = "DiagramKeyTextBrush",
        [DiagramRole.Edge] = "DiagramEdgeBrush",
        [DiagramRole.EdgeLabel] = "DiagramEdgeLabelBrush",
        [DiagramRole.Highlight] = "DiagramHighlightBrush",
        [DiagramRole.Selection] = "DiagramSelectionBrush",
        [DiagramRole.Dimmed] = "DiagramDimmedBrush",
        [DiagramRole.Added] = "DiagramAddedBrush",
        [DiagramRole.Removed] = "DiagramRemovedBrush",
        [DiagramRole.Modified] = "DiagramModifiedBrush",
    };

    /// <summary>Last-resort colour when a resource is missing, so a typo shows up rather than crashes.</summary>
    private static readonly IBrush Fallback = Brushes.Gray;

    private readonly Dictionary<DiagramRole, IBrush> _brushes = [];

    private DiagramTheme(IBrush surface) => Surface = surface;

    /// <summary>The background the whole diagram sits on.</summary>
    public IBrush Surface { get; }

    /// <summary>
    /// Resolves every role against a visual's current theme variant.
    /// </summary>
    /// <remarks>
    /// Built per render pass rather than held, because <c>ActualThemeVariant</c> is what decides the
    /// answers and it changes underneath us. Thirteen dictionary lookups is not a cost worth caching
    /// against.
    /// </remarks>
    public static DiagramTheme For(Control element)
    {
        ArgumentNullException.ThrowIfNull(element);

        var variant = element.ActualThemeVariant;
        var theme = new DiagramTheme(Lookup(element, "DiagramSurfaceBrush", variant));

        foreach (var (role, key) in Keys)
        {
            theme._brushes[role] = Lookup(element, key, variant);
        }

        return theme;
    }

    public IBrush Brush(DiagramRole role) =>
        _brushes.TryGetValue(role, out var brush) ? brush : Fallback;

    /// <summary>
    /// Measures a string the way the surface will draw it. Passed into
    /// <see cref="LayoutOptions.MeasureText"/>, so node widths match the real font rather than the
    /// character-count approximation Core falls back to.
    /// </summary>
    public static Func<string, double, double> Measure(
        FontFamily uiFont, FontFamily monoFont) =>
        (text, size) =>
        {
            // The wider of the two fonts, because a node holds both: its title in the UI font and its
            // rows in the monospace one. Measuring only one leaves the other clipped.
            var ui = Width(text, size, uiFont);
            var mono = Width(text, size, monoFont);
            return Math.Max(ui, mono);
        };

    private static double Width(string text, double size, FontFamily font) =>
        new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(font),
            size,
            Brushes.Black).Width;

    private static IBrush Lookup(Control element, string key, ThemeVariant variant)
    {
        element.TryFindResource(key, variant, out var value);
        return value as IBrush ?? Fallback;
    }
}
