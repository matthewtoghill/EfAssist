using EfAssist.App;
using EfAssist.Core.Diagrams;

namespace EfAssist.Core.Tests;

/// <summary>
/// Smoke tests for the two binary exports.
/// </summary>
/// <remarks>
/// Deliberately no pixel comparison. Skia's text rendering varies with the fonts installed on the
/// machine, so a golden image would fail on a different build agent for a reason that is not a bug.
/// What is worth asserting is that a real file comes out with the right magic bytes in it.
/// </remarks>
public class DiagramExportTests
{
    private static readonly LayoutOptions Options =
        LayoutOptions.Default with { MeasureText = (text, size) => text.Length * size * 0.6 };

    private static DiagramScene Scene()
    {
        var view = new DiagramViewOptions();
        var content = DiagramNodeContent.Build(
            ModelSnapshotParser.Parse(Fixture.Text("snapshot-rich")), view);

        return SceneBuilder.Build(
            DiagramLayoutEngine.Compute(content, Options), Options, view: view);
    }

    [Fact]
    public void WritesAPng()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".png");

        try
        {
            DiagramExport.WritePng(Scene(), path);

            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length > 0);
            Assert.Equal<byte[]>([0x89, (byte)'P', (byte)'N', (byte)'G'], bytes[..4]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WritesAPdf()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pdf");

        try
        {
            DiagramExport.WritePdf(Scene(), path);

            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length > 0);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes[..4]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnEmptyDiagramStillProducesAValidFile()
    {
        // The surface is never empty in practice, but a one-pixel image beats a Skia exception from
        // a zero-sized surface.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".png");

        try
        {
            DiagramExport.WritePng(DiagramScene.Empty, path);
            Assert.True(new FileInfo(path).Length > 0);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
