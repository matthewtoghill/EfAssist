using System;
using System.Collections.Generic;
using System.Reflection;
using System.Xml;
using Avalonia.Styling;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace EfMigrateHub.App;

/// <summary>
/// Supplies the SQL syntax definition for a theme variant.
/// </summary>
/// <remarks>
/// There is a definition per variant rather than one shared definition because a
/// <c>HighlightingColor</c> holds a literal colour, not a theme resource — nothing repaints it when
/// the variant changes. This is the same trap the colour-producing converters fell into before they
/// were replaced by style classes; here the fix is to load a different definition instead.
/// AvaloniaEdit does bundle a <c>TSQL</c> definition, but its colours are blue-on-white literals,
/// which is unreadable on the dark variant.
/// </remarks>
public static class SqlHighlighting
{
    private static readonly Dictionary<string, IHighlightingDefinition> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// The definition for the given variant. Anything that is not explicitly light is treated as
    /// dark, which is what <see cref="Avalonia.StyledElement.ActualThemeVariant"/> resolves
    /// <c>Default</c> to on a dark OS — it never reports <c>Default</c> itself.
    /// </summary>
    public static IHighlightingDefinition For(ThemeVariant? variant) =>
        Load(variant == ThemeVariant.Light ? "Light" : "Dark");

    private static IHighlightingDefinition Load(string variant)
    {
        if (Cache.TryGetValue(variant, out var cached))
        {
            return cached;
        }

        var resource = $"EfMigrateHub.App.Highlighting.Sql-{variant}.xshd";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded resource '{resource}' is missing.");
        using var reader = XmlReader.Create(stream);

        // Not registered with HighlightingManager: nothing looks these up by name or by extension,
        // and registering both would mean two definitions claiming ".sql".
        var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        Cache[variant] = definition;
        return definition;
    }
}
