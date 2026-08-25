namespace EfAssist.Core.Diagrams;

/// <summary>
/// Sizes and spacings, plus the one thing Core cannot work out for itself: how wide a piece of text
/// is in the font actually being used.
/// </summary>
/// <param name="MeasureText">
/// Text and font size to width. The app passes one backed by Avalonia's <c>FormattedText</c>; tests
/// pass a deterministic stub. Deliberately a parameter rather than a constant — node width is the
/// one part of layout that depends on the real font, and approximating it in Core would make every
/// node either clipped or padded on somebody's machine.
/// </param>
public sealed record LayoutOptions(Func<string, double, double> MeasureText)
{
    /// <summary>
    /// A fallback measurement for tests and for a first layout before the renderer is available:
    /// average glyph width as a fraction of the font size. Wrong for any specific string, close
    /// enough for a starting layout, and the reason <see cref="MeasureText"/> is injectable.
    /// </summary>
    /// <remarks>
    /// ponytail: character count times font size times 0.55. Deliberately never upgraded — the app
    /// injects a real <c>FormattedText</c> measurement, and this exists so Core and the tests need no
    /// font at all.
    /// </remarks>
    public static LayoutOptions Default { get; } =
        new((text, fontSize) => text.Length * fontSize * 0.55);

    public double TitleFontSize { get; init; } = 13;

    public double RowFontSize { get; init; } = 12;

    public double RowHeight { get; init; } = 18;

    /// <summary>Title plus subtitle.</summary>
    public double HeaderHeight { get; init; } = 38;

    public double NodePadding { get; init; } = 8;

    /// <summary>Gap between the name column and the type column inside a row.</summary>
    public double ColumnSpacing { get; init; } = 14;

    public double MinNodeWidth { get; init; } = 150;

    /// <summary>
    /// Beyond this a node is ellipsised rather than widened. One <c>nvarchar(max)</c> column should
    /// not make a node three times the width of its neighbours.
    /// </summary>
    public double MaxNodeWidth { get; init; } = 340;

    /// <summary>Horizontal space between ranks, which is also where edges are routed.</summary>
    public double RankGap { get; init; } = 90;

    public double NodeGap { get; init; } = 28;

    public double Margin { get; init; } = 24;
}

/// <param name="RowOffsets">
/// The y of each row relative to the node's top, so the renderer and the hit test agree on which row
/// the pointer is over without re-deriving it.
/// </param>
public sealed record LaidOutNode(
    DiagramNode Node,
    DiagramRect Bounds,
    int Rank,
    IReadOnlyList<double> RowOffsets);

public sealed record LaidOutEdge(DiagramEdge Edge, IReadOnlyList<DiagramPoint> Points)
{
    /// <summary>Mid-point of the routed path, where a label goes.</summary>
    public DiagramPoint Midpoint => Points.Count == 0
        ? default
        : Points[Points.Count / 2];
}

public sealed record DiagramLayout(
    IReadOnlyList<LaidOutNode> Nodes,
    IReadOnlyList<LaidOutEdge> Edges,
    DiagramSize Size)
{
    public static DiagramLayout Empty { get; } = new([], [], new DiagramSize(0, 0));

    public LaidOutNode? At(DiagramPoint point) =>
        Nodes.LastOrDefault(n => n.Bounds.Contains(point));

    public LaidOutNode? Node(string entityName) =>
        Nodes.FirstOrDefault(n => n.Node.EntityName == entityName);

    /// <summary>Every node's position, in the form the persisted diagram stores.</summary>
    public Dictionary<string, DiagramPoint> Positions() =>
        Nodes.ToDictionary(n => n.Node.EntityName, n => n.Bounds.TopLeft, StringComparer.Ordinal);
}

/// <summary>
/// Places nodes and routes edges. A pure function: same input, same output, no clock and no
/// randomness, so a diagram does not shuffle itself between two runs on the same model.
/// </summary>
/// <remarks>
/// ponytail: a layered layout — rank by dependency depth, order within a rank by two barycentre
/// passes, route orthogonally through the gap between ranks. Not as good as a real Sugiyama
/// implementation, and deliberately so: dragging nodes is a shipped feature, so auto-layout only has
/// to produce a sane starting point. Upgrade to MSAGL if the output disappoints on a real model —
/// that only touches <see cref="Compute"/>. See <c>docs/DIAGRAMS-PLAN.md</c> §7.
/// </remarks>
public static class DiagramLayoutEngine
{
    /// <param name="fixedPositions">
    /// Positions to honour instead of computing, keyed by entity name — a hand-arranged layout
    /// restored from disk. Entities absent from it are placed normally, so adding one entity to a
    /// dragged diagram moves that entity and nothing else.
    /// </param>
    public static DiagramLayout Compute(
        DiagramNodeContent.Content content,
        LayoutOptions? options = null,
        IReadOnlyDictionary<string, DiagramPoint>? fixedPositions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        options ??= LayoutOptions.Default;

        if (content.Nodes.Count == 0)
        {
            return DiagramLayout.Empty;
        }

        // Sorted first, so ranking, ordering and therefore the whole layout is independent of the
        // order the parser happened to produce.
        var nodes = content.Nodes
            .OrderBy(n => n.EntityName, StringComparer.Ordinal)
            .ToList();

        var edges = content.Edges
            .OrderBy(e => e.From, StringComparer.Ordinal)
            .ThenBy(e => e.To, StringComparer.Ordinal)
            .ToList();

        cancellationToken.ThrowIfCancellationRequested();

        var measured = nodes.ToDictionary(
            n => n.EntityName,
            n => Measure(n, options),
            StringComparer.Ordinal);

        var ranks = Rank(nodes, edges);
        var ordered = OrderWithinRanks(nodes, edges, ranks);

        cancellationToken.ThrowIfCancellationRequested();

        var placed = Place(ordered, ranks, measured, options, fixedPositions);
        var routed = Route(placed, edges, options);

        return new DiagramLayout(placed, routed, Extent(placed, routed, options));
    }

    // ---- Sizing ----

    private static (DiagramSize Size, List<double> RowOffsets) Measure(
        DiagramNode node, LayoutOptions options)
    {
        var width = options.MeasureText(node.Title, options.TitleFontSize);

        if (node.Subtitle is not null)
        {
            width = Math.Max(width, options.MeasureText(node.Subtitle, options.RowFontSize));
        }

        var offsets = new List<double>(node.Rows.Count);
        var y = options.HeaderHeight;

        foreach (var row in node.Rows)
        {
            var rowWidth = options.MeasureText(RowLabel(row), options.RowFontSize);
            if (row.Type is not null)
            {
                rowWidth += options.ColumnSpacing + options.MeasureText(row.Type, options.RowFontSize);
            }

            width = Math.Max(width, rowWidth);
            offsets.Add(y);
            y += options.RowHeight;
        }

        // Two pixels of slack past what the text measured. The scene divides this same width back
        // into a name column and a type column, and without the slack an exact fit rounds the wrong
        // way and every row comes out ellipsised one character early.
        width = Math.Clamp(
            width + (2 * options.NodePadding) + 2, options.MinNodeWidth, options.MaxNodeWidth);

        // An empty node still needs a body, or its border collapses onto the header.
        var height = y + options.NodePadding;

        return (new DiagramSize(width, height), offsets);
    }

    /// <summary>The measured text of a row: badge, name, and the nullability marker.</summary>
    private static string RowLabel(DiagramRow row)
    {
        var badge = row.Badge.Length > 0 ? row.Badge + " " : "";
        var nullable = row.IsNullable ? " ?" : "";
        return badge + row.Name + nullable;
    }

    // ---- Ranking ----

    /// <summary>
    /// Rank by dependency depth, principals to the left of their dependents. Cycles — which a real
    /// model has, through an optional relationship in both directions — are broken by ignoring the
    /// edge that closes them, which is what makes this terminate.
    /// </summary>
    /// <remarks>
    /// ponytail: which edge of a cycle gets ignored depends on traversal order, so a mutually-optional
    /// pair is drawn one way round rather than the other for no reason the reader can see. Revisit if
    /// anyone cares which.
    /// </remarks>
    private static Dictionary<string, int> Rank(
        List<DiagramNode> nodes, List<DiagramEdge> edges)
    {
        // Dependent to the principals it must sit right of. Self-references are excluded: a node
        // cannot be to the right of itself.
        var principals = nodes.ToDictionary(
            n => n.EntityName, _ => new List<string>(), StringComparer.Ordinal);

        foreach (var edge in edges.Where(e => e.From != e.To && e.Kind != EdgeKind.ManyToMany))
        {
            if (principals.TryGetValue(edge.From, out var list) && principals.ContainsKey(edge.To))
            {
                list.Add(edge.To);
            }
        }

        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            Resolve(node.EntityName);
        }

        return ranks;

        int Resolve(string name)
        {
            if (ranks.TryGetValue(name, out var known))
            {
                return known;
            }

            // Already on the stack: this edge closes a cycle. Treat it as rank 0 for now — the
            // recursion that owns the node will write the real value on the way out.
            if (!visiting.Add(name))
            {
                return 0;
            }

            var rank = 0;
            foreach (var principal in principals[name])
            {
                rank = Math.Max(rank, Resolve(principal) + 1);
            }

            visiting.Remove(name);
            ranks[name] = rank;
            return rank;
        }
    }

    // ---- Ordering within a rank ----

    /// <summary>
    /// Two barycentre passes: put each node near the average position of what it connects to in the
    /// rank before it. Cheap, and enough to stop the obvious crossings.
    /// </summary>
    private static List<List<DiagramNode>> OrderWithinRanks(
        List<DiagramNode> nodes, List<DiagramEdge> edges, Dictionary<string, int> ranks)
    {
        var maxRank = ranks.Count == 0 ? 0 : ranks.Values.Max();
        var byRank = new List<List<DiagramNode>>();

        for (var rank = 0; rank <= maxRank; rank++)
        {
            byRank.Add([.. nodes.Where(n => ranks[n.EntityName] == rank)]);
        }

        var neighbours = nodes.ToDictionary(
            n => n.EntityName, _ => new List<string>(), StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            if (neighbours.TryGetValue(edge.From, out var from)
                && neighbours.TryGetValue(edge.To, out var to))
            {
                from.Add(edge.To);
                to.Add(edge.From);
            }
        }

        for (var pass = 0; pass < 2; pass++)
        {
            for (var rank = 1; rank < byRank.Count; rank++)
            {
                var previous = byRank[rank - 1]
                    .Select((n, index) => (n.EntityName, Index: (double)index))
                    .ToDictionary(x => x.EntityName, x => x.Index, StringComparer.Ordinal);

                byRank[rank] = [.. byRank[rank]
                    .OrderBy(n => Barycentre(n.EntityName, previous, neighbours))
                    // Ties broken by name, so the result stays deterministic.
                    .ThenBy(n => n.EntityName, StringComparer.Ordinal)];
            }
        }

        return byRank;
    }

    private static double Barycentre(
        string name,
        Dictionary<string, double> previousRank,
        Dictionary<string, List<string>> neighbours)
    {
        var positions = neighbours[name]
            .Where(previousRank.ContainsKey)
            .Select(n => previousRank[n])
            .ToList();

        // No connection to the previous rank: leave it where alphabetical order put it rather than
        // pulling it to the top.
        return positions.Count == 0 ? double.MaxValue : positions.Average();
    }

    // ---- Placement ----

    private static List<LaidOutNode> Place(
        List<List<DiagramNode>> byRank,
        Dictionary<string, int> ranks,
        Dictionary<string, (DiagramSize Size, List<double> RowOffsets)> measured,
        LayoutOptions options,
        IReadOnlyDictionary<string, DiagramPoint>? fixedPositions)
    {
        var columnWidths = byRank
            .Select(rank => rank.Count == 0 ? 0 : rank.Max(n => measured[n.EntityName].Size.Width))
            .ToList();

        var columnHeights = byRank
            .Select(rank => rank.Sum(n => measured[n.EntityName].Size.Height)
                + (Math.Max(0, rank.Count - 1) * options.NodeGap))
            .ToList();

        var tallest = columnHeights.Count == 0 ? 0 : columnHeights.Max();

        var placed = new List<LaidOutNode>();
        var x = options.Margin;

        for (var rank = 0; rank < byRank.Count; rank++)
        {
            // Each rank centred against the tallest, so a two-node column does not sit at the top of
            // a twelve-node one.
            var y = options.Margin + ((tallest - columnHeights[rank]) / 2);

            foreach (var node in byRank[rank])
            {
                var (size, offsets) = measured[node.EntityName];
                var bounds = new DiagramRect(x, y, size.Width, size.Height);

                if (fixedPositions is not null
                    && fixedPositions.TryGetValue(node.EntityName, out var pinned))
                {
                    bounds = bounds.WithPosition(pinned);
                }
                else
                {
                    y += size.Height + options.NodeGap;
                }

                placed.Add(new LaidOutNode(node, bounds, ranks[node.EntityName], offsets));
            }

            x += columnWidths[rank] + options.RankGap;
        }

        return placed;
    }

    // ---- Edge routing ----

    /// <summary>
    /// Orthogonal routes: out of the dependent's left edge, across the gap, into the principal's
    /// right edge. A self-reference loops out of the right-hand side instead.
    /// </summary>
    /// <remarks>
    /// Attachment points are allocated by counting every edge touching a node first, then spreading
    /// them evenly down that node's side. A running offset per edge is the obvious approach and it is
    /// wrong: two edges into one node can land on the same offset, and two routes drawn on top of
    /// each other read as one relationship.
    /// </remarks>
    private static List<LaidOutEdge> Route(
        List<LaidOutNode> nodes, List<DiagramEdge> edges, LayoutOptions options)
    {
        var byName = nodes.ToDictionary(n => n.Node.EntityName, StringComparer.Ordinal);

        var connectable = edges
            .Where(e => byName.ContainsKey(e.From) && byName.ContainsKey(e.To))
            .ToList();

        var total = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var edge in connectable.Where(e => e.From != e.To))
        {
            total[edge.From] = total.GetValueOrDefault(edge.From) + 1;
            total[edge.To] = total.GetValueOrDefault(edge.To) + 1;
        }

        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        var routed = new List<LaidOutEdge>();

        foreach (var edge in connectable)
        {
            var from = byName[edge.From];
            var to = byName[edge.To];

            if (edge.From == edge.To)
            {
                routed.Add(new LaidOutEdge(edge, SelfLoop(from.Bounds, options)));
                continue;
            }

            var fromY = Attach(from.Bounds, edge.From);
            var toY = Attach(to.Bounds, edge.To);

            routed.Add(new LaidOutEdge(edge, Orthogonal(from.Bounds, to.Bounds, fromY, toY)));
        }

        return routed;

        double Attach(DiagramRect bounds, string entity)
        {
            var slot = used.GetValueOrDefault(entity);
            used[entity] = slot + 1;

            // Spread over the body rather than the whole node, so a route never arrives across the
            // node's own title.
            var top = bounds.Top + options.HeaderHeight;
            var height = Math.Max(1, bounds.Bottom - top);

            return top + (height * (slot + 1) / (total[entity] + 1.0));
        }
    }

    private static List<DiagramPoint> Orthogonal(
        DiagramRect from, DiagramRect to, double fromY, double toY)
    {
        // The principal is normally to the left. When it is not — a back-edge from a broken cycle, or
        // a node the user has dragged — leaving the same-side exits alone would draw a line straight
        // through both nodes, so the route reverses with it.
        var goingLeft = to.CentreX <= from.CentreX;

        var startX = goingLeft ? from.Left : from.Right;
        var endX = goingLeft ? to.Right : to.Left;
        var midX = (startX + endX) / 2;

        return
        [
            new DiagramPoint(startX, fromY),
            new DiagramPoint(midX, fromY),
            new DiagramPoint(midX, toY),
            new DiagramPoint(endX, toY),
        ];
    }

    private static List<DiagramPoint> SelfLoop(DiagramRect bounds, LayoutOptions options)
    {
        var out_ = bounds.Right + (options.RankGap / 3);
        var top = bounds.Top + (bounds.Height / 3);
        var bottom = bounds.Bottom - (bounds.Height / 3);

        return
        [
            new DiagramPoint(bounds.Right, top),
            new DiagramPoint(out_, top),
            new DiagramPoint(out_, bottom),
            new DiagramPoint(bounds.Right, bottom),
        ];
    }

    /// <summary>
    /// The whole diagram's extent, including anything routing or a dragged node pushed outside the
    /// nodes' own bounding box — the export and the scroll extent both need the real number.
    /// </summary>
    private static DiagramSize Extent(
        List<LaidOutNode> nodes, List<LaidOutEdge> edges, LayoutOptions options)
    {
        var right = nodes.Count == 0 ? 0 : nodes.Max(n => n.Bounds.Right);
        var bottom = nodes.Count == 0 ? 0 : nodes.Max(n => n.Bounds.Bottom);

        foreach (var point in edges.SelectMany(e => e.Points))
        {
            right = Math.Max(right, point.X);
            bottom = Math.Max(bottom, point.Y);
        }

        return new DiagramSize(right + options.Margin, bottom + options.Margin);
    }
}
