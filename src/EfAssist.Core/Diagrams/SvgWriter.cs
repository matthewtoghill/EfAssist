using System.Globalization;
using System.Text;

namespace EfAssist.Core.Diagrams;

/// <summary>
/// Fits a string into a width by trimming characters and appending an ellipsis.
/// </summary>
/// <remarks>
/// The on-screen renderer gets this from Avalonia's <c>TextTrimming.CharacterEllipsis</c>. The export
/// backends have to do it themselves, and both of them do it the same way from here rather than each
/// growing its own copy.
/// </remarks>
public static class DiagramText
{
    /// <param name="measure">Width of a string, in the font the caller is about to draw with.</param>
    public static string Fit(string text, double maxWidth, Func<string, double> measure)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(measure);

        if (double.IsPositiveInfinity(maxWidth) || text.Length == 0 || measure(text) <= maxWidth)
        {
            return text;
        }

        // ponytail: linear walk back from the end. Node rows are short and there are a few hundred of
        // them; a binary search would be the same answer with more code.
        for (var length = text.Length - 1; length > 0; length--)
        {
            var candidate = text[..length] + "…";
            if (measure(candidate) <= maxWidth)
            {
                return candidate;
            }
        }

        return "…";
    }

    /// <summary>
    /// Where a glyph's baseline sits below the top of its line box.
    /// </summary>
    /// <remarks>
    /// The scene positions text by its top, because that is what Avalonia's <c>DrawText</c> takes.
    /// SVG and Skia both position by the baseline. This is the conversion, and it is an approximation
    /// on purpose — a font-exact ascent would mean loading the font in Core, which is the thing
    /// <see cref="LayoutOptions.MeasureText"/> exists to avoid. Skia's export overrides it with the
    /// real metric, since it has the font in hand anyway.
    /// </remarks>
    public const double BaselineFactor = 0.8;
}

/// <summary>
/// Writes a <see cref="DiagramScene"/> as SVG.
/// </summary>
/// <remarks>
/// Hand-written rather than produced by a rendering library: the whole scene is a few hundred shapes,
/// the output is meant to be readable and editable, and one <c>&lt;g id="…"&gt;</c> per entity makes
/// it so. Colours come from a <see cref="DiagramPalette"/> through a single <c>&lt;style&gt;</c>
/// block, so recolouring an exported diagram is one edit rather than one per shape.
/// </remarks>
public static class SvgWriter
{
    public static string Write(
        DiagramScene scene,
        DiagramPalette? palette = null,
        Func<string, double, double>? measure = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        palette ??= DiagramPalette.Light;
        measure ??= LayoutOptions.Default.MeasureText;

        var width = Math.Max(1, scene.Size.Width);
        var height = Math.Max(1, scene.Size.Height);

        var svg = new StringBuilder();
        svg.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(width)}\" height=\"{N(height)}\" viewBox=\"0 0 {N(width)} {N(height)}\">\n");
        svg.Append(Style(palette));
        svg.Append(CultureInfo.InvariantCulture, $"  <rect class=\"surface\" x=\"0\" y=\"0\" width=\"{N(width)}\" height=\"{N(height)}\" />\n");

        // Grouped into consecutive runs by entity, not a global group-by: reordering the shapes
        // would reorder the painting, and the edges drawn before the nodes have to stay behind them.
        string? open = null;

        foreach (var shape in scene.Shapes)
        {
            if (shape.EntityName != open)
            {
                if (open is not null)
                {
                    svg.Append("  </g>\n");
                }

                open = shape.EntityName;

                if (open is not null)
                {
                    svg.Append(CultureInfo.InvariantCulture, $"  <g id=\"{Escape(Id(open))}\">\n");
                }
            }

            Append(svg, shape, palette, measure, indent: open is null ? "  " : "    ");
        }

        if (open is not null)
        {
            svg.Append("  </g>\n");
        }

        svg.Append("</svg>\n");
        return svg.ToString();
    }

    private static void Append(
        StringBuilder svg,
        DiagramShape shape,
        DiagramPalette palette,
        Func<string, double, double> measure,
        string indent)
    {
        switch (shape)
        {
            case RectShape rect:
                svg.Append(indent);
                svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{N(rect.Bounds.X)}\" y=\"{N(rect.Bounds.Y)}\" width=\"{N(rect.Bounds.Width)}\" height=\"{N(rect.Bounds.Height)}\"");

                if (rect.CornerRadius > 0)
                {
                    svg.Append(CultureInfo.InvariantCulture, $" rx=\"{N(rect.CornerRadius)}\"");
                }

                svg.Append(CultureInfo.InvariantCulture, $" fill=\"{palette.Colour(rect.Fill)}\"");

                if (rect.Border is { } border)
                {
                    svg.Append(CultureInfo.InvariantCulture, $" stroke=\"{palette.Colour(border)}\" stroke-width=\"{N(rect.BorderThickness)}\"");
                }
                else
                {
                    svg.Append(" stroke=\"none\"");
                }

                svg.Append(" />\n");
                break;

            case PolylineShape line when line.Points.Count > 1:
                svg.Append(indent);
                svg.Append("<polyline points=\"");
                for (var i = 0; i < line.Points.Count; i++)
                {
                    if (i > 0)
                    {
                        svg.Append(' ');
                    }

                    svg.Append(CultureInfo.InvariantCulture, $"{N(line.Points[i].X)},{N(line.Points[i].Y)}");
                }

                svg.Append(CultureInfo.InvariantCulture, $"\" fill=\"none\" stroke=\"{palette.Colour(line.Role)}\" stroke-width=\"{N(line.Thickness)}\"");

                if (line.Dashed)
                {
                    svg.Append(" stroke-dasharray=\"4 3\"");
                }

                svg.Append(" />\n");
                break;

            case TextShape text:
                var content = DiagramText.Fit(
                    text.Text, text.MaxWidth, s => measure(s, text.FontSize));

                svg.Append(indent);
                svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{N(text.At.X)}\" y=\"{N(text.At.Y + (text.FontSize * DiagramText.BaselineFactor))}\"");
                svg.Append(CultureInfo.InvariantCulture, $" font-family=\"{(text.Monospace ? palette.MonospaceFontFamily : palette.FontFamily)}\" font-size=\"{N(text.FontSize)}\" fill=\"{palette.Colour(text.Role)}\"");

                if (text.Bold)
                {
                    svg.Append(" font-weight=\"600\"");
                }

                if (text.Alignment == TextAlignment.Right)
                {
                    svg.Append(" text-anchor=\"end\"");
                }

                svg.Append(CultureInfo.InvariantCulture, $">{Escape(content)}</text>\n");
                break;
        }
    }

    private static string Style(DiagramPalette palette) =>
        $"  <style>\n    .surface {{ fill: {palette.Surface}; }}\n  </style>\n";

    /// <summary>
    /// An entity name as an XML id. Ids cannot start with a digit and cannot contain a dot, which a
    /// fully-qualified CLR type name is full of.
    /// </summary>
    private static string Id(string entityName) =>
        "Entity_" + string.Concat(entityName.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    private static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    /// <summary>A number an SVG parser will read the same way in every locale, without trailing noise.</summary>
    private static string N(double value) =>
        Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
}
