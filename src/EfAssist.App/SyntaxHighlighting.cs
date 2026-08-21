using System;
using System.Collections.Generic;
using System.Reflection;
using System.Xml;
using Avalonia.Styling;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace EfAssist.App;

/// <summary>
/// Supplies a syntax definition for a language and theme variant.
/// </summary>
/// <remarks>
/// There is a definition per variant rather than one shared definition because a
/// <c>HighlightingColor</c> holds a literal colour, not a theme resource — nothing repaints it when
/// the variant changes. This is the same trap the colour-producing converters fell into before they
/// were replaced by style classes; here the fix is to load a different definition instead.
/// AvaloniaEdit does bundle <c>TSQL</c> and <c>C#</c> definitions, but their colours are
/// blue-on-white literals, which is unreadable on the dark variant.
/// </remarks>
public static class SyntaxHighlighting
{
    private static readonly Dictionary<string, IHighlightingDefinition> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// The SQL definition for the given variant. Anything that is not explicitly light is treated as
    /// dark, which is what <see cref="Avalonia.StyledElement.ActualThemeVariant"/> resolves
    /// <c>Default</c> to on a dark OS — it never reports <c>Default</c> itself.
    /// </summary>
    public static IHighlightingDefinition Sql(ThemeVariant? variant) => Load("Sql", variant);

    /// <summary>The C# definition, for reading a migration's own source file.</summary>
    public static IHighlightingDefinition CSharp(ThemeVariant? variant) => Load("CSharp", variant);

    private static IHighlightingDefinition Load(string language, ThemeVariant? variant)
    {
        var name = $"{language}-{(variant == ThemeVariant.Light ? "Light" : "Dark")}";
        if (Cache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var resource = $"EfAssist.App.Highlighting.{name}.xshd";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded resource '{resource}' is missing.");
        using var reader = XmlReader.Create(stream);

        // Not registered with HighlightingManager: nothing looks these up by name or by extension,
        // and registering both variants would mean two definitions claiming the same extension.
        var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        Cache[name] = definition;
        return definition;
    }
}
