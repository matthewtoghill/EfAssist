using EfAssist.Core.Diagrams;

namespace EfAssist.Core.Tests;

public class MermaidWriterTests
{
    private static DiagramModel Rich => ModelSnapshotParser.Parse(Fixture.Text("snapshot-rich"));

    private static string Write(DiagramKind kind, DiagramViewOptions? options = null) =>
        MermaidWriter.Write(Rich, (options ?? new DiagramViewOptions()) with { Kind = kind });

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    [Fact]
    public void EntityViewOpensAnErDiagram() =>
        Assert.StartsWith("erDiagram", Write(DiagramKind.EntityRelationship), StringComparison.Ordinal);

    [Fact]
    public void ClassViewOpensAClassDiagram() =>
        Assert.StartsWith("classDiagram", Write(DiagramKind.Class), StringComparison.Ordinal);

    [Fact]
    public void WritesOneBlockPerVisibleEntity()
    {
        var options = new DiagramViewOptions();
        var expected = DiagramNodeContent.Build(Rich, options).Nodes.Count;

        var blocks = Lines(Write(DiagramKind.EntityRelationship, options))
            .Count(l => l.EndsWith('{'));

        Assert.Equal(expected, blocks);
    }

    [Fact]
    public void FollowsTheViewOptionsRatherThanTheWholeModel()
    {
        // The Mermaid export describes the diagram on screen. A collapsed join table is not in it,
        // and turning the option off puts it back.
        var collapsed = Write(
            DiagramKind.EntityRelationship, new DiagramViewOptions { CollapseJoinEntities = true });
        var expanded = Write(
            DiagramKind.EntityRelationship, new DiagramViewOptions { CollapseJoinEntities = false });

        Assert.True(
            Lines(expanded).Count(l => l.EndsWith('{'))
            > Lines(collapsed).Count(l => l.EndsWith('{')));
    }

    [Fact]
    public void NamesContainNothingMermaidWouldChokeOn()
    {
        // Fully qualified CLR names, generic navigations and column types like nvarchar(450) all
        // arrive here; none of them is a legal Mermaid identifier.
        foreach (var kind in new[] { DiagramKind.EntityRelationship, DiagramKind.Class })
        {
            foreach (var line in Lines(Write(kind)))
            {
                // Quoted segments are labels and cardinalities, which may hold anything.
                var unquoted = string.Join(
                    ' ', line.Split('"').Where((_, i) => i % 2 == 0));

                // Mermaid's own operators and stereotypes are not identifiers.
                foreach (var token in new[] { "<<abstract>>", "<|--", "<-->", "*--", "-->" })
                {
                    unquoted = unquoted.Replace(token, "", StringComparison.Ordinal);
                }

                Assert.DoesNotContain('.', unquoted);
                Assert.DoesNotContain('(', unquoted);
                Assert.DoesNotContain('<', unquoted);
            }
        }
    }

    [Fact]
    public void DrawsInheritanceInTheClassViewOnly()
    {
        // TPH is a class-model idea. An ER diagram has no notation for it, and drawing it as a
        // foreign key would claim a join that does not exist.
        Assert.Contains("<|--", Write(DiagramKind.Class), StringComparison.Ordinal);
        Assert.DoesNotContain("<|--", Write(DiagramKind.EntityRelationship), StringComparison.Ordinal);
    }

    [Fact]
    public void MarksKeysInTheEntityView()
    {
        var lines = Lines(Write(DiagramKind.EntityRelationship)).ToList();

        Assert.Contains(lines, l => l.EndsWith(" PK", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.EndsWith(" FK", StringComparison.Ordinal));
    }

    [Fact]
    public void LabelsDeleteBehaviourOnlyWhenTheViewDoes()
    {
        var without = Write(
            DiagramKind.EntityRelationship, new DiagramViewOptions { ShowDeleteBehavior = false });
        var with = Write(
            DiagramKind.EntityRelationship, new DiagramViewOptions { ShowDeleteBehavior = true });

        Assert.DoesNotContain("Cascade", without, StringComparison.Ordinal);
        Assert.Contains("Cascade", with, StringComparison.Ordinal);
    }

    [Fact]
    public void AModelWithNoRelationshipsStillProducesADiagram()
    {
        var text = MermaidWriter.Write(
            ModelSnapshotParser.Parse(Fixture.Text("snapshot-simple")), new DiagramViewOptions());

        Assert.StartsWith("erDiagram", text, StringComparison.Ordinal);
        Assert.Contains('{', text);
    }
}
