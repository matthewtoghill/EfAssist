namespace EfAssist.Core.Diagrams;

/// <summary>
/// Colours for the file formats, which have no theme to ask.
/// </summary>
/// <remarks>
/// <para>
/// On screen a <see cref="DiagramRole"/> is resolved against the current theme's brushes; an SVG in a
/// repository, a PNG in a pull request or a PDF in a design document has no such thing, so the export
/// path needs literal colours somewhere. Here is the somewhere — the scene itself still carries roles
/// only.
/// </para>
/// <para>
/// Deliberately light whatever the app's theme is. A dark-background diagram pasted into a document
/// or a printed page is almost never what was wanted, and the values below are the light theme's from
/// <c>App.axaml</c> so an export looks like the app rather than merely near it.
/// </para>
/// </remarks>
public sealed record DiagramPalette
{
    private static readonly Dictionary<DiagramRole, string> LightColours = new()
    {
        [DiagramRole.NodeBackground] = "#FFFFFF",
        [DiagramRole.NodeBorder] = "#C4C4C4",
        [DiagramRole.HeaderBackground] = "#EDEDED",
        [DiagramRole.HeaderText] = "#1A1A1A",
        [DiagramRole.SubtitleText] = "#767676",
        [DiagramRole.Text] = "#2B2B2B",
        [DiagramRole.MutedText] = "#8A8A8A",
        [DiagramRole.KeyText] = "#7A4B00",
        [DiagramRole.Edge] = "#8C8C8C",
        [DiagramRole.EdgeLabel] = "#767676",
        [DiagramRole.Highlight] = "#1F6FEB",
        [DiagramRole.Selection] = "#0A4FBF",
        [DiagramRole.Dimmed] = "#D0D0D0",
    };

    public static DiagramPalette Light { get; } = new();

    /// <summary>The background the diagram is drawn on.</summary>
    public string Surface { get; init; } = "#FFFFFF";

    /// <summary>The font stack for titles and labels. A stack, because a viewer may lack any one of them.</summary>
    public string FontFamily { get; init; } = "Segoe UI, Inter, Helvetica, Arial, sans-serif";

    /// <summary>The font stack for property rows, which are drawn monospaced on screen.</summary>
    public string MonospaceFontFamily { get; init; } = "Consolas, Menlo, monospace";

    public IReadOnlyDictionary<DiagramRole, string> Colours { get; init; } = LightColours;

    /// <summary>The hex colour for a role, falling back to a visible grey rather than throwing.</summary>
    public string Colour(DiagramRole role) =>
        Colours.TryGetValue(role, out var colour) ? colour : "#808080";
}
