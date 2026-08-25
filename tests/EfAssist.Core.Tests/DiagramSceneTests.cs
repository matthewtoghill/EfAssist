using EfAssist.Core.Diagrams;

namespace EfAssist.Core.Tests;

public class DiagramSceneTests
{
    private static readonly LayoutOptions Options =
        LayoutOptions.Default with { MeasureText = (text, size) => text.Length * size * 0.6 };

    private static DiagramLayout Layout(DiagramViewOptions? view = null) =>
        DiagramLayoutEngine.Compute(
            DiagramNodeContent.Build(
                ModelSnapshotParser.Parse(Fixture.Text("snapshot-rich")),
                view ?? new DiagramViewOptions()),
            Options);

    private static DiagramScene Scene(SceneState? state = null, DiagramViewOptions? view = null) =>
        SceneBuilder.Build(Layout(view), Options, state, view);

    // ---- Structure ----

    [Fact]
    public void ReportsTheLayoutSizeAndEveryNodesBounds()
    {
        var layout = Layout();
        var scene = SceneBuilder.Build(layout, Options);

        Assert.Equal(layout.Size, scene.Size);
        Assert.Equal(layout.Nodes.Count, scene.Nodes.Count);
        Assert.All(layout.Nodes, n =>
            Assert.Equal(n.Bounds, scene.Nodes[n.Node.EntityName]));
    }

    [Fact]
    public void DrawsEdgesBeforeNodes()
    {
        // A node's fill has to cover the routes passing behind it, not the other way round.
        var shapes = Scene().Shapes.ToList();
        var lastEdge = shapes.FindLastIndex(s => s is PolylineShape { EntityName: null });
        var firstNode = shapes.FindIndex(s => s.EntityName is not null);

        Assert.True(firstNode > lastEdge);
    }

    [Fact]
    public void TagsEveryNodeShapeWithItsEntity()
    {
        // What the SVG writer groups on and what dimming a whole node keys off.
        var scene = Scene();

        Assert.All(
            scene.Nodes.Keys,
            entity => Assert.Contains(scene.Shapes, s => s.EntityName == entity));
    }

    [Fact]
    public void PutsEveryNodeTitleOnTheDiagram()
    {
        var layout = Layout();
        var texts = SceneBuilder.Build(layout, Options).Shapes
            .OfType<TextShape>()
            .Select(t => t.Text)
            .ToHashSet();

        Assert.All(layout.Nodes, n => Assert.Contains(n.Node.Title, texts));
    }

    [Fact]
    public void KeepsEveryShapeInsideTheSceneBounds()
    {
        // What the PNG and PDF exports are sized to. A shape outside it is silently cropped.
        var scene = Scene();

        foreach (var shape in scene.Shapes)
        {
            switch (shape)
            {
                case RectShape rect:
                    Assert.InRange(rect.Bounds.Right, 0, scene.Size.Width);
                    Assert.InRange(rect.Bounds.Bottom, 0, scene.Size.Height);
                    break;

                case PolylineShape line:
                    Assert.All(line.Points, p =>
                    {
                        Assert.InRange(p.X, 0, scene.Size.Width);
                        Assert.InRange(p.Y, 0, scene.Size.Height);
                    });
                    break;
            }
        }
    }

    // ---- Roles, not colours ----

    [Fact]
    public void MarksKeyRowsWithTheKeyRole()
    {
        var scene = Scene();
        var keyRows = scene.Shapes
            .OfType<TextShape>()
            .Where(t => t.Text.StartsWith("PK", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(keyRows);
        Assert.All(keyRows, t => Assert.Equal(DiagramRole.KeyText, t.Role));
    }

    [Fact]
    public void UsesTheMutedRoleForTypes()
    {
        var types = Scene().Shapes
            .OfType<TextShape>()
            .Where(t => t.Alignment == TextAlignment.Right)
            .ToList();

        Assert.NotEmpty(types);
        Assert.All(types, t => Assert.Equal(DiagramRole.MutedText, t.Role));
    }

    // ---- Selection, search, dimming ----

    [Fact]
    public void BordersTheSelectedNodeWithTheSelectionRole()
    {
        var selected = "SampleRichModel.Post";
        var scene = Scene(new SceneState(Selected: selected));

        var border = scene.Shapes
            .OfType<RectShape>()
            .Single(r => r.EntityName == selected && r.Border is not null);

        Assert.Equal(DiagramRole.Selection, border.Border);
        Assert.Equal(2, border.BorderThickness);
    }

    [Fact]
    public void HighlightsMatchesAndDimsEverythingElseWhileSearching()
    {
        var matches = new HashSet<string>(StringComparer.Ordinal) { "SampleRichModel.Post" };
        var scene = Scene(new SceneState(Matches: matches, Searching: true));

        var match = scene.Shapes
            .OfType<RectShape>()
            .Single(r => r.EntityName == "SampleRichModel.Post" && r.Border is not null);
        Assert.Equal(DiagramRole.Highlight, match.Border);

        var other = scene.Shapes
            .OfType<RectShape>()
            .Single(r => r.EntityName == "SampleRichModel.Tag" && r.Border is not null);
        Assert.Equal(DiagramRole.Dimmed, other.Border);
    }

    [Fact]
    public void DimsNothingWhenNoSearchIsRunning()
    {
        // An empty match set with Searching off is "no search", which is a different thing from a
        // search that matched nothing — that one dims everything.
        var scene = Scene(new SceneState(Matches: new HashSet<string>(), Searching: false));

        Assert.DoesNotContain(scene.Shapes, s => s is RectShape { Border: DiagramRole.Dimmed });
    }

    [Fact]
    public void DimsEverythingWhenASearchMatchesNothing()
    {
        var scene = Scene(new SceneState(Matches: new HashSet<string>(), Searching: true));

        Assert.All(
            scene.Shapes.OfType<RectShape>().Where(r => r.Border is not null),
            r => Assert.Equal(DiagramRole.Dimmed, r.Border));
    }

    [Fact]
    public void SelectionWinsOverAHighlight()
    {
        var matches = new HashSet<string>(StringComparer.Ordinal) { "SampleRichModel.Post" };
        var scene = Scene(new SceneState("SampleRichModel.Post", matches, Searching: true));

        var border = scene.Shapes
            .OfType<RectShape>()
            .Single(r => r.EntityName == "SampleRichModel.Post" && r.Border is not null);

        Assert.Equal(DiagramRole.Selection, border.Border);
    }

    // ---- Edge decoration ----

    [Fact]
    public void DashesInheritanceAndOwnershipButNotForeignKeys()
    {
        var layout = Layout(new DiagramViewOptions { InlineOwnedTypes = false });
        var scene = SceneBuilder.Build(layout, Options);

        // One representative edge of each kind the model actually produces.
        var byKind = layout.Edges
            .GroupBy(e => e.Edge.Kind)
            .ToDictionary(g => g.Key, g => g.First().Points[0]);

        Assert.Equal(4, byKind.Count);

        foreach (var (kind, start) in byKind)
        {
            var line = scene.Shapes
                .OfType<PolylineShape>()
                .First(p => p.Points.Count == 4 && p.Points[0] == start);

            Assert.Equal(kind is EdgeKind.Inheritance or EdgeKind.Ownership, line.Dashed);
        }
    }

    [Fact]
    public void DrawsAMarkerAtThePrincipalEndOfEveryEdge()
    {
        var layout = Layout();
        var scene = SceneBuilder.Build(layout, Options);

        foreach (var edge in layout.Edges)
        {
            var tip = edge.Points[^1];

            // Every marker style ends or starts at the tip, so its presence is what is asserted here
            // rather than its shape.
            Assert.Contains(
                scene.Shapes.OfType<PolylineShape>(),
                p => p.Points.Contains(tip) && p.Points.Count <= 5);
        }
    }

    [Fact]
    public void LabelsEdgesWithTheirDeleteBehaviourWhenAsked()
    {
        var view = new DiagramViewOptions { ShowDeleteBehavior = true };
        var texts = Scene(view: view).Shapes
            .OfType<TextShape>()
            .Where(t => t.Role == DiagramRole.EdgeLabel)
            .Select(t => t.Text)
            .ToList();

        Assert.Contains("Cascade", texts);
        Assert.Contains("SetNull", texts);
        Assert.Contains("Restrict", texts);
    }

    [Fact]
    public void PointsAMarkerBackFromTheTipEvenOnAReversedRoute()
    {
        // A dragged node can put the principal to the right of its dependent. A marker that always
        // fans one way ends up drawn inside the node it is pointing at.
        var content = DiagramNodeContent.Build(
            ModelSnapshotParser.Parse(Fixture.Text("snapshot-rich")), new DiagramViewOptions());

        var reversed = DiagramLayoutEngine.Compute(
            content,
            Options,
            new Dictionary<string, DiagramPoint>(StringComparer.Ordinal)
            {
                ["SampleRichModel.Blog"] = new DiagramPoint(1200, 40),
                ["SampleRichModel.Post"] = new DiagramPoint(40, 40),
            });

        var edge = reversed.Edges.Single(e =>
            e.Edge.From == "SampleRichModel.Post" && e.Edge.To == "SampleRichModel.Blog");

        var scene = SceneBuilder.Build(reversed, Options);
        var tip = edge.Points[^1];

        var marker = scene.Shapes
            .OfType<PolylineShape>()
            .First(p => p.Points.Count <= 5 && p.Points.Contains(tip));

        // Arriving from the left, so the marker's tail sits to the left of the tip.
        Assert.All(marker.Points, p => Assert.True(p.X <= tip.X));
    }

    // ---- Empty ----

    [Fact]
    public void ReturnsAnEmptySceneForAnEmptyLayout()
    {
        var scene = SceneBuilder.Build(DiagramLayout.Empty, Options);

        Assert.True(scene.IsEmpty);
        Assert.Empty(scene.Nodes);
    }
}
