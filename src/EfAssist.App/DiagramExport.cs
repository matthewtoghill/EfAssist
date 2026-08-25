using System;
using System.Collections.Generic;
using System.IO;
using EfAssist.Core.Diagrams;
using SkiaSharp;
using CoreTextAlignment = EfAssist.Core.Diagrams.TextAlignment;

namespace EfAssist.App;

/// <summary>
/// Writes a <see cref="DiagramScene"/> as PNG or PDF.
/// </summary>
/// <remarks>
/// <para>
/// Both go through Skia, which is already in the process — Avalonia renders with it — so this adds a
/// managed reference and no new native assets. One <see cref="Replay"/> serves both, for the same
/// reason the SVG writer walks the same scene: what is exported is what is on screen, because there
/// is only one description of the diagram.
/// </para>
/// <para>
/// Colours come from <see cref="DiagramPalette"/> rather than the live theme. A file has no theme to
/// follow, and a dark diagram in a printed document is rarely what was wanted.
/// </para>
/// </remarks>
public static class DiagramExport
{
    /// <summary>
    /// Rendered at twice the diagram's size, so the PNG is still crisp on a high-DPI display and in a
    /// document that scales it up.
    /// </summary>
    /// <remarks>
    /// ponytail: a constant rather than a 1×/2×/4× picker. 2× is right for a screenshot in a pull
    /// request, which is what this is for; add the picker if someone needs a poster.
    /// </remarks>
    public const int PngScale = 2;

    public static void WritePng(DiagramScene scene, string path, DiagramPalette? palette = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        palette ??= DiagramPalette.Light;

        var width = (int)Math.Ceiling(Math.Max(1, scene.Size.Width));
        var height = (int)Math.Ceiling(Math.Max(1, scene.Size.Height));

        using var surface = SKSurface.Create(
            new SKImageInfo(width * PngScale, height * PngScale, SKColorType.Rgba8888, SKAlphaType.Premul));

        surface.Canvas.Scale(PngScale);
        Replay(surface.Canvas, scene, palette);
        surface.Canvas.Flush();

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
    }

    /// <remarks>
    /// One page, sized to the diagram. Tiling a diagram across A4 pages makes it unreadable at exactly
    /// the point it needs to be read, and every PDF viewer can zoom.
    /// </remarks>
    public static void WritePdf(DiagramScene scene, string path, DiagramPalette? palette = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        palette ??= DiagramPalette.Light;

        var width = (float)Math.Max(1, scene.Size.Width);
        var height = (float)Math.Max(1, scene.Size.Height);

        using var file = File.Create(path);
        using var document = SKDocument.CreatePdf(file);

        var canvas = document.BeginPage(width, height);
        Replay(canvas, scene, palette);
        document.EndPage();
        document.Close();
    }

    // ---- The one replay ----

    private static void Replay(SKCanvas canvas, DiagramScene scene, DiagramPalette palette)
    {
        canvas.Clear(Parse(palette.Surface));

        using var ui = Font(palette.FontFamily, bold: false);
        using var uiBold = Font(palette.FontFamily, bold: true);
        using var mono = Font(palette.MonospaceFontFamily, bold: false);

        var colours = new Dictionary<DiagramRole, SKColor>();
        SKColor Colour(DiagramRole role)
        {
            if (!colours.TryGetValue(role, out var colour))
            {
                colour = Parse(palette.Colour(role));
                colours[role] = colour;
            }

            return colour;
        }

        using var paint = new SKPaint { IsAntialias = true };

        foreach (var shape in scene.Shapes)
        {
            switch (shape)
            {
                case RectShape rect:
                    paint.Style = SKPaintStyle.Fill;
                    paint.Color = Colour(rect.Fill);
                    paint.PathEffect = null;
                    canvas.DrawRoundRect(
                        Rect(rect.Bounds), (float)rect.CornerRadius, (float)rect.CornerRadius, paint);

                    if (rect.Border is { } border)
                    {
                        paint.Style = SKPaintStyle.Stroke;
                        paint.StrokeWidth = (float)rect.BorderThickness;
                        paint.Color = Colour(border);
                        canvas.DrawRoundRect(
                            Rect(rect.Bounds), (float)rect.CornerRadius, (float)rect.CornerRadius, paint);
                    }

                    break;

                case PolylineShape line when line.Points.Count > 1:
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = (float)line.Thickness;
                    paint.Color = Colour(line.Role);
                    paint.PathEffect = line.Dashed ? SKPathEffect.CreateDash([4, 3], 0) : null;

                    for (var i = 1; i < line.Points.Count; i++)
                    {
                        canvas.DrawLine(
                            (float)line.Points[i - 1].X, (float)line.Points[i - 1].Y,
                            (float)line.Points[i].X, (float)line.Points[i].Y,
                            paint);
                    }

                    paint.PathEffect?.Dispose();
                    paint.PathEffect = null;
                    break;

                case TextShape text:
                    var font = text.Monospace ? mono : text.Bold ? uiBold : ui;
                    font.Size = (float)text.FontSize;

                    var content = DiagramText.Fit(
                        text.Text, text.MaxWidth, s => font.MeasureText(s));

                    paint.Style = SKPaintStyle.Fill;
                    paint.Color = Colour(text.Role);
                    paint.PathEffect = null;

                    // The scene places text by its top; Skia draws from the baseline, and the font
                    // knows exactly where that is.
                    canvas.DrawText(
                        content,
                        (float)text.At.X,
                        (float)(text.At.Y - font.Metrics.Ascent),
                        text.Alignment == CoreTextAlignment.Right ? SKTextAlign.Right : SKTextAlign.Left,
                        font,
                        paint);
                    break;
            }
        }
    }

    /// <summary>
    /// A font from the palette's family stack, falling back to Skia's default rather than throwing —
    /// a machine without Consolas should still get a PNG.
    /// </summary>
    private static SKFont Font(string familyStack, bool bold)
    {
        var style = bold ? SKFontStyle.Bold : SKFontStyle.Normal;

        foreach (var family in familyStack.Split(','))
        {
            var typeface = SKTypeface.FromFamilyName(family.Trim(), style);
            if (typeface is not null)
            {
                return new SKFont(typeface) { Subpixel = true };
            }
        }

        return new SKFont(SKTypeface.Default) { Subpixel = true };
    }

    private static SKRect Rect(DiagramRect rect) =>
        new((float)rect.X, (float)rect.Y, (float)rect.Right, (float)rect.Bottom);

    private static SKColor Parse(string hex) =>
        SKColor.TryParse(hex, out var colour) ? colour : SKColors.Gray;
}
