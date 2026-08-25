using System.Xml.Linq;
using EfAssist.Core.Diagrams;

namespace EfAssist.Core.Tests;

public class SvgWriterTests
{
    private static readonly LayoutOptions Options =
        LayoutOptions.Default with { MeasureText = (text, size) => text.Length * size * 0.6 };

    private static DiagramScene Scene(string fixture = "snapshot-rich")
    {
        var view = new DiagramViewOptions();
        var content = DiagramNodeContent.Build(
            ModelSnapshotParser.Parse(Fixture.Text(fixture)), view);

        return SceneBuilder.Build(
            DiagramLayoutEngine.Compute(content, Options), Options, view: view);
    }

    private static XDocument Parse(DiagramScene scene) =>
        XDocument.Parse(SvgWriter.Write(scene, measure: Options.MeasureText));

    [Fact]
    public void WritesWellFormedSvgSizedToTheDiagram()
    {
        var scene = Scene();
        var svg = Parse(scene).Root!;

        Assert.Equal("svg", svg.Name.LocalName);
        Assert.Equal("http://www.w3.org/2000/svg", svg.Name.NamespaceName);
        Assert.Equal(
            $"0 0 {Round(scene.Size.Width)} {Round(scene.Size.Height)}",
            (string?)svg.Attribute("viewBox"));
    }

    [Fact]
    public void WritesOneGroupPerEntity()
    {
        var scene = Scene();
        var groups = Parse(scene).Descendants()
            .Where(e => e.Name.LocalName == "g")
            .Select(e => (string?)e.Attribute("id"))
            .ToList();

        Assert.Equal(scene.Nodes.Count, groups.Count);
        Assert.All(groups, id => Assert.StartsWith("Entity_", id));
        Assert.Equal(groups.Count, groups.Distinct().Count());
    }

    [Fact]
    public void GroupIdsAreLegalXmlIdentifiers()
    {
        // Entity names are fully qualified CLR type names, and a dot is not allowed in an XML id.
        var ids = Parse(Scene()).Descendants()
            .Where(e => e.Name.LocalName == "g")
            .Select(e => (string)e.Attribute("id")!);

        Assert.All(ids, id => Assert.DoesNotContain('.', id));
    }

    [Fact]
    public void EscapesMarkupInEntityNames()
    {
        // A generic navigation type is the realistic case: "ICollection<Post>" as a row's type would
        // otherwise open a tag halfway through the file.
        var scene = new DiagramScene(
            new DiagramSize(200, 100),
            [
                new TextShape("ICollection<Post> & \"more\"", new DiagramPoint(4, 4))
                {
                    EntityName = "Ns.Blog<T>",
                },
            ],
            new Dictionary<string, DiagramRect>());

        var text = SvgWriter.Write(scene);

        // Parses at all, which is the assertion that matters, and the content survives the escaping.
        var element = XDocument.Parse(text).Descendants()
            .Single(e => e.Name.LocalName == "text");

        Assert.Equal("ICollection<Post> & \"more\"", element.Value);
        Assert.Contains("&lt;", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EllipsisesTextThatDoesNotFitItsWidth()
    {
        var scene = new DiagramScene(
            new DiagramSize(200, 100),
            [
                new TextShape(
                    "AVeryLongColumnNameIndeed",
                    new DiagramPoint(0, 0),
                    MaxWidth: 30),
            ],
            new Dictionary<string, DiagramRect>());

        var value = XDocument.Parse(SvgWriter.Write(scene, measure: Options.MeasureText))
            .Descendants().Single(e => e.Name.LocalName == "text").Value;

        Assert.EndsWith("…", value);
        Assert.True(Options.MeasureText(value, 12) <= 30);
    }

    [Fact]
    public void WritesNumbersInvariantly()
    {
        // A comma decimal separator would silently produce an SVG with two coordinates where the
        // file wanted one.
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

        try
        {
            var scene = new DiagramScene(
                new DiagramSize(10.5, 20.25),
                [new RectShape(new DiagramRect(1.5, 2.5, 3.5, 4.5), DiagramRole.NodeBackground)],
                new Dictionary<string, DiagramRect>());

            Assert.Contains("x=\"1.5\"", SvgWriter.Write(scene), StringComparison.Ordinal);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void AnEmptySceneIsStillValidSvg()
    {
        var svg = XDocument.Parse(SvgWriter.Write(DiagramScene.Empty)).Root!;

        Assert.Equal("svg", svg.Name.LocalName);
        Assert.Equal("1", (string?)svg.Attribute("width"));
    }

    private static string Round(double value) =>
        Math.Round(value, 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
