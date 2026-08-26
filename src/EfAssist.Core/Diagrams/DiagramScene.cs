namespace EfAssist.Core.Diagrams;

/// <summary>
/// What a shape <em>is</em>, not what colour it is.
/// </summary>
/// <remarks>
/// A shape carries a role and the renderer resolves it to a brush at draw time. Baking a colour in
/// here would repeat the mistake recorded in <c>docs/ROADMAP.md</c> for the SQL syntax definitions:
/// a literal colour is not a theme resource, so nothing repaints it when the variant changes, and
/// the diagram would keep its light-theme text on a dark background.
/// </remarks>
public enum DiagramRole
{
    NodeBackground,
    NodeBorder,
    HeaderBackground,
    HeaderText,
    SubtitleText,

    /// <summary>An ordinary property or column row.</summary>
    Text,

    /// <summary>Types, nullability markers, index rows — present but secondary.</summary>
    MutedText,

    /// <summary>A key or foreign key row, and its badge.</summary>
    KeyText,

    Edge,
    EdgeLabel,

    /// <summary>Border and fill of a node matching the current search.</summary>
    Highlight,

    /// <summary>Border of the selected node.</summary>
    Selection,

    /// <summary>A node that does not match the current search, so it recedes rather than vanishing.</summary>
    Dimmed,

    /// <summary>Added by the migration being compared. Green, by the convention every diff uses.</summary>
    Added,

    /// <summary>Removed by it, and only on screen because the earlier model still had it.</summary>
    Removed,

    /// <summary>Present in both, but changed — a retyped or renullabled column.</summary>
    Modified,
}

/// <summary>The role a change is drawn in. Nothing outside a diff ever resolves to one.</summary>
internal static class ChangeRole
{
    internal static DiagramRole? For(DiagramChange change) => change switch
    {
        DiagramChange.Added => DiagramRole.Added,
        DiagramChange.Removed => DiagramRole.Removed,
        DiagramChange.Modified => DiagramRole.Modified,
        _ => null,
    };
}

public enum TextAlignment
{
    Left,
    Right,
}

/// <summary>One drawing instruction. Replayed by the on-screen renderer and by every vector export.</summary>
public abstract record DiagramShape
{
    /// <summary>
    /// The entity this shape belongs to, when it belongs to one. Used to group an export's output —
    /// the SVG writer turns it into a <c>&lt;g id="…"&gt;</c> — and to dim a whole node at once.
    /// </summary>
    public string? EntityName { get; init; }
}

public sealed record RectShape(
    DiagramRect Bounds,
    DiagramRole Fill,
    DiagramRole? Border = null,
    double BorderThickness = 1,
    double CornerRadius = 0) : DiagramShape;

public sealed record PolylineShape(
    IReadOnlyList<DiagramPoint> Points,
    DiagramRole Role = DiagramRole.Edge,
    double Thickness = 1,
    bool Dashed = false) : DiagramShape;

public sealed record TextShape(
    string Text,
    DiagramPoint At,
    DiagramRole Role = DiagramRole.Text,
    double FontSize = 12,
    bool Bold = false,
    bool Monospace = false,
    TextAlignment Alignment = TextAlignment.Left,
    double MaxWidth = double.PositiveInfinity) : DiagramShape;

/// <param name="Selected">The entity whose node is selected, or null.</param>
/// <param name="Matches">
/// Entities matching the current search. Empty means no search is running — which is different from
/// a search that matched nothing, where nothing highlights and everything dims.
/// </param>
public sealed record SceneState(
    string? Selected = null,
    IReadOnlySet<string>? Matches = null,
    bool Searching = false)
{
    public static SceneState None { get; } = new();

    public bool IsMatch(string entityName) => Matches?.Contains(entityName) ?? false;

    public bool IsDimmed(string entityName) => Searching && !IsMatch(entityName);
}

/// <param name="Nodes">
/// Node bounds by entity name, so hit-testing and dragging work off the scene without needing the
/// layout as well.
/// </param>
public sealed record DiagramScene(
    DiagramSize Size,
    IReadOnlyList<DiagramShape> Shapes,
    IReadOnlyDictionary<string, DiagramRect> Nodes)
{
    public static DiagramScene Empty { get; } =
        new(new DiagramSize(0, 0), [], new Dictionary<string, DiagramRect>());

    public bool IsEmpty => Shapes.Count == 0;
}

/// <summary>
/// Turns a placed layout into a flat list of drawing instructions.
/// </summary>
/// <remarks>
/// The reason exporting to SVG, PNG and PDF is cheap: each backend replays this one list rather than
/// re-deriving the diagram. It also means what is exported is exactly what is on screen, because both
/// come from the same scene.
/// </remarks>
public static class SceneBuilder
{
    public static DiagramScene Build(
        DiagramLayout layout,
        LayoutOptions? options = null,
        SceneState? state = null,
        DiagramViewOptions? view = null)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (layout.Nodes.Count == 0)
        {
            return DiagramScene.Empty;
        }

        options ??= LayoutOptions.Default;
        state ??= SceneState.None;
        view ??= new DiagramViewOptions();

        var shapes = new List<DiagramShape>();

        // Edges first, so a node's fill covers the routes that pass behind it rather than the other
        // way round.
        foreach (var edge in layout.Edges)
        {
            shapes.AddRange(EdgeShapes(edge, state, options));
        }

        foreach (var node in layout.Nodes)
        {
            shapes.AddRange(NodeShapes(node, state, options, view));
        }

        return new DiagramScene(
            layout.Size,
            shapes,
            layout.Nodes.ToDictionary(
                n => n.Node.EntityName, n => n.Bounds, StringComparer.Ordinal));
    }

    // ---- Nodes ----

    private static IEnumerable<DiagramShape> NodeShapes(
        LaidOutNode node, SceneState state, LayoutOptions options, DiagramViewOptions view)
    {
        var entity = node.Node.EntityName;
        var dimmed = state.IsDimmed(entity);
        var bounds = node.Bounds;

        // Selection and search win over the diff colours: both are things the user is doing right
        // now, and losing track of what is selected is worse than losing a colour that the rows
        // inside the node repeat anyway.
        var border = entity == state.Selected
            ? DiagramRole.Selection
            : state.IsMatch(entity) ? DiagramRole.Highlight
            : dimmed ? DiagramRole.Dimmed
            : ChangeRole.For(node.Node.Change) ?? DiagramRole.NodeBorder;

        yield return new RectShape(
            bounds,
            DiagramRole.NodeBackground,
            border,
            BorderThickness: entity == state.Selected ? 2 : 1,
            CornerRadius: 4) { EntityName = entity };

        yield return new RectShape(
            new DiagramRect(bounds.X, bounds.Y, bounds.Width, options.HeaderHeight),
            DiagramRole.HeaderBackground) { EntityName = entity };

        var textLeft = bounds.X + options.NodePadding;
        var textRight = bounds.Right - options.NodePadding;
        var inner = bounds.Width - (2 * options.NodePadding);

        yield return new TextShape(
            DiagramDiff.Marker(node.Node.Change) + node.Node.Title,
            new DiagramPoint(textLeft, bounds.Y + 5),
            dimmed
                ? DiagramRole.Dimmed
                : ChangeRole.For(node.Node.Change) ?? DiagramRole.HeaderText,
            options.TitleFontSize,
            Bold: true,
            MaxWidth: inner) { EntityName = entity };

        if (node.Node.Subtitle is not null)
        {
            yield return new TextShape(
                node.Node.Subtitle,
                new DiagramPoint(textLeft, bounds.Y + 5 + options.TitleFontSize + 3),
                dimmed ? DiagramRole.Dimmed : DiagramRole.SubtitleText,
                options.RowFontSize - 1,
                MaxWidth: inner) { EntityName = entity };
        }

        // The line under the header, drawn as a polyline rather than a 1px rect so it picks up the
        // border colour and stays crisp under a scale transform.
        yield return new PolylineShape(
            [
                new DiagramPoint(bounds.Left, bounds.Y + options.HeaderHeight),
                new DiagramPoint(bounds.Right, bounds.Y + options.HeaderHeight),
            ],
            dimmed ? DiagramRole.Dimmed : DiagramRole.NodeBorder) { EntityName = entity };

        for (var i = 0; i < node.Node.Rows.Count; i++)
        {
            var row = node.Node.Rows[i];
            var y = bounds.Y + node.RowOffsets[i];

            // A changed row's colour beats its key colour. What changed is the question being
            // asked when a diff is on screen, and the PK badge still says the rest.
            var role = dimmed ? DiagramRole.Dimmed
                : ChangeRole.For(row.Change) is { } changed ? changed
                : row.IsKey || row.IsForeignKey ? DiagramRole.KeyText
                : row.Kind == RowKind.Property ? DiagramRole.Text
                : DiagramRole.MutedText;

            var marker = DiagramDiff.Marker(row.Change);
            var badge = row.Badge.Length > 0 ? row.Badge + " " : "";
            var nullable = view.ShowNullability && row.IsNullable ? " ?" : "";

            // The type gets what it needs and the name gets the rest, capped so a long type can
            // never squeeze the name out entirely. A fixed half-and-half split truncates
            // "ICollection<ContactMethod>" in a node that had the room for it.
            var typeWidth = row.Type is null
                ? 0
                : Math.Min(options.MeasureText(row.Type, options.RowFontSize), inner * 0.62);

            yield return new TextShape(
                marker + badge + row.Name + nullable,
                new DiagramPoint(textLeft, y),
                role,
                options.RowFontSize,
                Monospace: true,
                MaxWidth: Math.Max(20, inner - typeWidth - options.ColumnSpacing)) { EntityName = entity };

            if (row.Type is not null)
            {
                yield return new TextShape(
                    row.Type,
                    new DiagramPoint(textRight, y),
                    dimmed ? DiagramRole.Dimmed : DiagramRole.MutedText,
                    options.RowFontSize,
                    Monospace: true,
                    Alignment: TextAlignment.Right,
                    MaxWidth: typeWidth) { EntityName = entity };
            }
        }
    }

    // ---- Edges ----

    private static IEnumerable<DiagramShape> EdgeShapes(
        LaidOutEdge edge, SceneState state, LayoutOptions options)
    {
        var dimmed = state.IsDimmed(edge.Edge.From) && state.IsDimmed(edge.Edge.To);
        var role = dimmed
            ? DiagramRole.Dimmed
            : ChangeRole.For(edge.Edge.Change) ?? DiagramRole.Edge;

        yield return new PolylineShape(
            edge.Points,
            role,
            Thickness: edge.Edge.Kind == EdgeKind.ManyToMany ? 1.6 : 1,
            // Inheritance is not a foreign key, and an owned type is not an independent row, so
            // neither is drawn as a solid association line.
            Dashed: edge.Edge.Kind is EdgeKind.Inheritance or EdgeKind.Ownership);

        if (edge.Points.Count < 2)
        {
            yield break;
        }

        foreach (var shape in EndMarker(edge, role))
        {
            yield return shape;
        }

        if (edge.Edge.Label is { Length: > 0 } label)
        {
            yield return new TextShape(
                label,
                edge.Midpoint.Offset(4, -options.RowFontSize - 2),
                dimmed ? DiagramRole.Dimmed : DiagramRole.EdgeLabel,
                options.RowFontSize - 1);
        }

        // Cardinalities sit beside the end they belong to. Running horizontally an end is on a node's
        // side, so the label goes above the line; running vertically it is on the top or bottom edge,
        // where above the line would be inside the node, so it goes below the exit instead.
        if (edge.Edge.FromLabel is { Length: > 0 } from)
        {
            yield return new TextShape(
                from,
                options.IsVertical
                    ? edge.Points[0].Offset(4, options.RowFontSize + 1)
                    : edge.Points[0].Offset(-14, -options.RowFontSize - 1),
                role,
                options.RowFontSize - 1);
        }

        if (edge.Edge.ToLabel is { Length: > 0 } to)
        {
            yield return new TextShape(
                to,
                options.IsVertical
                    ? edge.Points[^1].Offset(4, -3)
                    : edge.Points[^1].Offset(4, -options.RowFontSize - 1),
                role,
                options.RowFontSize - 1);
        }
    }

    /// <summary>
    /// The marker at the principal end. An open triangle for inheritance, a filled diamond for
    /// ownership, a crow's foot otherwise — all drawn as polylines, so nothing downstream needs to
    /// know how to draw a marker.
    /// </summary>
    /// <remarks>
    /// Built from the route's own last segment rather than from the layout's orientation: the segment
    /// already says which way the line arrives, which covers both orientations and a route reversed by
    /// a dragged node with one rule instead of three.
    /// </remarks>
    private static IEnumerable<DiagramShape> EndMarker(LaidOutEdge edge, DiagramRole role)
    {
        var tip = edge.Points[^1];
        var previous = edge.Points[^2];

        var dx = tip.X - previous.X;
        var dy = tip.Y - previous.Y;

        // Routes are orthogonal, so the last segment lies on one axis or the other. A zero-length
        // final segment — two nodes lining up exactly — reads as horizontal, which is what the rest of
        // the route looks like.
        var horizontal = Math.Abs(dx) >= Math.Abs(dy);

        // Back along the segment, and across it. The marker geometry is written in those two, so it
        // does not care which axis the route arrived on.
        var (backX, backY) = horizontal
            ? (dx >= 0 ? -1.0 : 1.0, 0.0)
            : (0.0, dy >= 0 ? -1.0 : 1.0);

        var (acrossX, acrossY) = horizontal ? (0.0, 1.0) : (1.0, 0.0);

        const double length = 9;
        const double half = 5;

        DiagramPoint At(double back, double across) => new(
            tip.X + (backX * back) + (acrossX * across),
            tip.Y + (backY * back) + (acrossY * across));

        switch (edge.Edge.Kind)
        {
            case EdgeKind.Inheritance:
                yield return new PolylineShape(
                    [At(length, -half), tip, At(length, half), At(length, -half)],
                    role);
                break;

            case EdgeKind.Ownership:
                yield return new PolylineShape(
                    [
                        tip,
                        At(length / 2, -half / 1.6),
                        At(length, 0),
                        At(length / 2, half / 1.6),
                        tip,
                    ],
                    role);
                break;

            default:
                // Crow's foot: three lines fanning back from the tip.
                yield return new PolylineShape([At(length, -half), tip], role);
                yield return new PolylineShape([At(length, 0), tip], role);
                yield return new PolylineShape([At(length, half), tip], role);
                break;
        }
    }
}
