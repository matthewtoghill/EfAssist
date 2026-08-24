using EfAssist.Core.Diagrams;

namespace EfAssist.Core.Tests;

public class DiagramLayoutTests
{
    /// <summary>
    /// A deterministic stand-in for the real font. The app passes a measurement backed by Avalonia's
    /// <c>FormattedText</c>; these tests need a function whose answers never change between machines.
    /// </summary>
    private static readonly LayoutOptions Options =
        LayoutOptions.Default with { MeasureText = (text, size) => text.Length * size * 0.6 };

    private static DiagramLayout Layout(
        DiagramViewOptions? view = null,
        IReadOnlyDictionary<string, DiagramPoint>? positions = null)
    {
        var model = ModelSnapshotParser.Parse(Fixture.Text("snapshot-rich"));
        var content = DiagramNodeContent.Build(model, view ?? new DiagramViewOptions());
        return DiagramLayoutEngine.Compute(content, Options, positions);
    }

    private static DiagramNodeContent.Content Content(params string[] statements)
    {
        var source = $$"""
            [DbContext(typeof(T))]
            partial class S : ModelSnapshot
            {
                protected override void BuildModel(ModelBuilder modelBuilder)
                {
                    {{string.Join("\n        ", statements)}}
                }
            }
            """;

        return DiagramNodeContent.Build(
            ModelSnapshotParser.Parse(source), new DiagramViewOptions());
    }

    /// <summary>Two entities where B depends on A.</summary>
    private static string Pair(string a, string b) => $$"""
        modelBuilder.Entity("Ns.{{a}}", x =>
            {
                x.Property<int>("Id").HasColumnType("int");
                x.HasKey("Id");
                x.ToTable("{{a}}");
            });
        modelBuilder.Entity("Ns.{{b}}", x =>
            {
                x.Property<int>("Id").HasColumnType("int");
                x.Property<int>("{{a}}Id").HasColumnType("int");
                x.HasKey("Id");
                x.ToTable("{{b}}");
                x.HasOne("Ns.{{a}}", "{{a}}").WithMany("{{b}}s").HasForeignKey("{{a}}Id").IsRequired();
            });
        """;

    // ---- Determinism ----

    [Fact]
    public void ProducesTheSameLayoutTwiceForTheSameModel()
    {
        // The whole reason the layout sorts before it ranks. Without it the result follows dictionary
        // order and the diagram shuffles itself between two runs on an unchanged model.
        var first = Layout();
        var second = Layout();

        Assert.Equal(
            first.Nodes.Select(n => (n.Node.EntityName, n.Bounds)),
            second.Nodes.Select(n => (n.Node.EntityName, n.Bounds)));

        Assert.Equal(
            first.Edges.SelectMany(e => e.Points),
            second.Edges.SelectMany(e => e.Points));
    }

    // ---- Overlap ----

    [Fact]
    public void NoTwoNodesOverlap()
    {
        var nodes = Layout().Nodes;

        for (var i = 0; i < nodes.Count; i++)
        {
            for (var j = i + 1; j < nodes.Count; j++)
            {
                Assert.False(
                    Overlaps(nodes[i].Bounds, nodes[j].Bounds),
                    $"{nodes[i].Node.EntityName} overlaps {nodes[j].Node.EntityName}");
            }
        }

        static bool Overlaps(DiagramRect a, DiagramRect b) =>
            a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
    }

    [Fact]
    public void EveryNodeIsInsideTheReportedSize()
    {
        var layout = Layout();

        Assert.All(layout.Nodes, n =>
        {
            Assert.True(n.Bounds.Right <= layout.Size.Width);
            Assert.True(n.Bounds.Bottom <= layout.Size.Height);
            Assert.True(n.Bounds.Left >= 0);
            Assert.True(n.Bounds.Top >= 0);
        });
    }

    [Fact]
    public void SizeCoversEdgeRoutesThatLeaveTheNodeBounds()
    {
        // A self-reference loops out past the right edge of its node, and an export sized to the
        // nodes alone would clip it.
        var layout = Layout();
        var points = layout.Edges.SelectMany(e => e.Points).ToList();

        Assert.All(points, p =>
        {
            Assert.True(p.X <= layout.Size.Width);
            Assert.True(p.Y <= layout.Size.Height);
        });
    }

    // ---- Ranking ----

    [Fact]
    public void PutsThePrincipalToTheLeftOfItsDependent()
    {
        var layout = DiagramLayoutEngine.Compute(Content(Pair("Blog", "Post")), Options);

        var blog = layout.Node("Ns.Blog")!;
        var post = layout.Node("Ns.Post")!;

        Assert.Equal(0, blog.Rank);
        Assert.Equal(1, post.Rank);
        Assert.True(blog.Bounds.Right < post.Bounds.Left);
    }

    [Fact]
    public void TerminatesOnACycle()
    {
        // Two entities each holding an optional foreign key to the other. A longest-path ranking that
        // does not break the cycle recurses until the stack gives out.
        var content = Content("""
            modelBuilder.Entity("Ns.A", x =>
                {
                    x.Property<int>("Id").HasColumnType("int");
                    x.Property<int?>("BId").HasColumnType("int");
                    x.HasKey("Id");
                    x.ToTable("A");
                    x.HasOne("Ns.B", "B").WithMany("As").HasForeignKey("BId");
                });
            modelBuilder.Entity("Ns.B", x =>
                {
                    x.Property<int>("Id").HasColumnType("int");
                    x.Property<int?>("AId").HasColumnType("int");
                    x.HasKey("Id");
                    x.ToTable("B");
                    x.HasOne("Ns.A", "A").WithMany("Bs").HasForeignKey("AId");
                });
            """);

        var layout = DiagramLayoutEngine.Compute(content, Options);

        Assert.Equal(2, layout.Nodes.Count);
        Assert.Equal(2, layout.Edges.Count);
    }

    [Fact]
    public void TerminatesOnALongInheritanceChain()
    {
        var statements = Enumerable.Range(0, 12).Select(i => $$"""
            modelBuilder.Entity("Ns.T{{i}}", x =>
                {
                    x.Property<int>("Id").HasColumnType("int");
                    x.HasKey("Id");
                    {{(i == 0 ? "x.ToTable(\"T\");" : $"x.HasBaseType(\"Ns.T{i - 1}\");")}}
                });
            """);

        var layout = DiagramLayoutEngine.Compute(Content([.. statements]), Options);

        Assert.Equal(12, layout.Nodes.Count);
        Assert.Equal(11, layout.Nodes.Max(n => n.Rank));
    }

    // ---- Edges ----

    [Fact]
    public void RoutesEveryEdgeBetweenTheTwoNodeBorders()
    {
        var layout = DiagramLayoutEngine.Compute(Content(Pair("Blog", "Post")), Options);
        var edge = layout.Edges.Single();

        var post = layout.Node("Ns.Post")!.Bounds;
        var blog = layout.Node("Ns.Blog")!.Bounds;

        // Out of the dependent's left edge, into the principal's right edge.
        Assert.Equal(post.Left, edge.Points[0].X, precision: 6);
        Assert.Equal(blog.Right, edge.Points[^1].X, precision: 6);
    }

    [Fact]
    public void EdgeEndpointsSitOnTheNodesTheyConnect()
    {
        var layout = Layout();
        var bounds = layout.Nodes.ToDictionary(n => n.Node.EntityName, n => n.Bounds);

        foreach (var edge in layout.Edges.Where(e => e.Edge.From != e.Edge.To))
        {
            var from = bounds[edge.Edge.From];
            var to = bounds[edge.Edge.To];

            Assert.InRange(edge.Points[0].Y, from.Top, from.Bottom);
            Assert.InRange(edge.Points[^1].Y, to.Top, to.Bottom);
        }
    }

    [Fact]
    public void RoutesASelfReferenceOutsideItsOwnNode()
    {
        var layout = Layout();
        var loop = layout.Edges.Single(e => e.Edge.From == e.Edge.To);
        var node = layout.Node(loop.Edge.From)!.Bounds;

        Assert.Contains(loop.Points, p => p.X > node.Right);
        Assert.All(loop.Points, p => Assert.InRange(p.Y, node.Top, node.Bottom));
    }

    [Fact]
    public void SeparatesParallelEdgesIntoTheSameNode()
    {
        // Two relationships arriving at one node must not land on the same point, or they draw as one
        // line and the diagram loses a relationship.
        var layout = Layout();
        var intoPerson = layout.Edges
            .Where(e => e.Edge.To == "SampleRichModel.Person")
            .Select(e => e.Points[^1].Y)
            .ToList();

        Assert.True(intoPerson.Count > 1);
        Assert.Equal(intoPerson.Count, intoPerson.Distinct().Count());
    }

    [Fact]
    public void DropsAnEdgeWhoseNodesAreNotPresentRatherThanThrowing()
    {
        var content = new DiagramNodeContent.Content(
            [new DiagramNode("Ns.A", "A")],
            [new DiagramEdge("Ns.A", "Ns.Missing")]);

        var layout = DiagramLayoutEngine.Compute(content, Options);

        Assert.Single(layout.Nodes);
        Assert.Empty(layout.Edges);
    }

    // ---- Sizing ----

    [Fact]
    public void ClampsNodeWidthBetweenTheMinimumAndMaximum()
    {
        var options = Options with { MinNodeWidth = 120, MaxNodeWidth = 200 };

        var content = Content("""
            modelBuilder.Entity("Ns.Wide", x =>
                {
                    x.Property<string>("AnExtremelyLongColumnNameThatGoesOnAndOnForeverAndEverAndEver")
                        .HasColumnType("nvarchar(max)");
                    x.ToTable("W");
                });
            modelBuilder.Entity("Ns.Tiny", x => { x.ToTable("T"); });
            """);

        var layout = DiagramLayoutEngine.Compute(content, options);

        Assert.All(layout.Nodes, n => Assert.InRange(n.Bounds.Width, 120, 200));
    }

    [Fact]
    public void GivesEveryRowAnOffsetInsideItsNode()
    {
        var layout = Layout();

        Assert.All(layout.Nodes, node =>
        {
            Assert.Equal(node.Node.Rows.Count, node.RowOffsets.Count);
            Assert.All(node.RowOffsets, offset =>
                Assert.InRange(offset, Options.HeaderHeight, node.Bounds.Height));
        });
    }

    [Fact]
    public void ANodeWithNoRowsStillHasABody()
    {
        var layout = DiagramLayoutEngine.Compute(
            Content("""modelBuilder.Entity("Ns.Bare", x => { x.ToTable("B"); });"""),
            Options);

        Assert.True(layout.Nodes.Single().Bounds.Height > Options.HeaderHeight);
    }

    // ---- Restored positions ----

    [Fact]
    public void HonoursPositionsRestoredFromDisk()
    {
        var pinned = new Dictionary<string, DiagramPoint>(StringComparer.Ordinal)
        {
            ["SampleRichModel.Blog"] = new DiagramPoint(900, 700),
        };

        var layout = Layout(positions: pinned);

        Assert.Equal(new DiagramPoint(900, 700), layout.Node("SampleRichModel.Blog")!.Bounds.TopLeft);
    }

    [Fact]
    public void PlacesOnlyTheUnpinnedNodesWhenSomePositionsAreRestored()
    {
        // Adding one entity to a hand-arranged diagram must move that entity and nothing else.
        var baseline = Layout();
        var pinned = baseline.Positions();
        pinned.Remove("SampleRichModel.Tag");

        var restored = Layout(positions: pinned);

        foreach (var (name, position) in pinned)
        {
            Assert.Equal(position, restored.Node(name)!.Bounds.TopLeft);
        }
    }

    [Fact]
    public void RoutesEdgesToRestoredPositionsRatherThanComputedOnes()
    {
        var pinned = new Dictionary<string, DiagramPoint>(StringComparer.Ordinal)
        {
            ["Ns.Blog"] = new DiagramPoint(600, 40),
            ["Ns.Post"] = new DiagramPoint(40, 40),
        };

        // Blog is now to the right of Post, the opposite of where ranking put it. Keeping the
        // original exits would draw the line back through both nodes.
        var layout = DiagramLayoutEngine.Compute(Content(Pair("Blog", "Post")), Options, pinned);
        var edge = layout.Edges.Single();

        Assert.Equal(layout.Node("Ns.Post")!.Bounds.Right, edge.Points[0].X, precision: 6);
        Assert.Equal(layout.Node("Ns.Blog")!.Bounds.Left, edge.Points[^1].X, precision: 6);
    }

    [Fact]
    public void ReportsPositionsInTheFormThePersistedDiagramStores()
    {
        var layout = Layout();
        var positions = layout.Positions();

        Assert.Equal(layout.Nodes.Count, positions.Count);
        Assert.All(layout.Nodes, n =>
            Assert.Equal(n.Bounds.TopLeft, positions[n.Node.EntityName]));
    }

    // ---- Hit testing ----

    [Fact]
    public void FindsTheNodeUnderAPoint()
    {
        var layout = Layout();
        var target = layout.Nodes[3];
        var centre = new DiagramPoint(target.Bounds.CentreX, target.Bounds.CentreY);

        Assert.Equal(target.Node.EntityName, layout.At(centre)!.Node.EntityName);
        Assert.Null(layout.At(new DiagramPoint(-50, -50)));
    }

    // ---- Edges of the input ----

    [Fact]
    public void ReturnsAnEmptyLayoutForAnEmptyModel()
    {
        var layout = DiagramLayoutEngine.Compute(
            new DiagramNodeContent.Content([], []), Options);

        Assert.Empty(layout.Nodes);
        Assert.Equal(new DiagramSize(0, 0), layout.Size);
    }

    [Fact]
    public void ObservesCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var content = DiagramNodeContent.Build(
            ModelSnapshotParser.Parse(Fixture.Text("snapshot-rich")), new DiagramViewOptions());

        Assert.ThrowsAny<OperationCanceledException>(() =>
            DiagramLayoutEngine.Compute(content, Options, cancellationToken: cancelled.Token));
    }

    [Fact]
    public void FallsBackToACharacterWidthMeasurementWhenNoneIsSupplied()
    {
        // LayoutOptions.Default has to produce a usable layout on its own, because the first layout
        // happens before a renderer exists to measure with.
        var model = ModelSnapshotParser.Parse(Fixture.Text("snapshot-rich"));
        var content = DiagramNodeContent.Build(model, new DiagramViewOptions());

        var layout = DiagramLayoutEngine.Compute(content);

        Assert.NotEmpty(layout.Nodes);
        Assert.All(layout.Nodes, n => Assert.True(n.Bounds.Width > 0));
    }
}
