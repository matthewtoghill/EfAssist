using EfAssist.Core.Diagrams;

namespace EfAssist.Core.Tests;

/// <summary>
/// Comparing two snapshots. The interesting half is the merge: a removed entity or column has to
/// survive into the model being drawn, or there is nothing on screen to mark as removed.
/// </summary>
public class DiagramDiffTests
{
    private static DiagramProperty Key(string name = "Id") =>
        new(name, "int", ColumnType: "INTEGER", IsKey: true);

    private static DiagramEntity Entity(
        string name, params DiagramProperty[] properties) =>
        new(name, Table: name + "s", Properties: properties);

    private static DiagramModel Model(
        IReadOnlyList<DiagramEntity> entities,
        IReadOnlyList<DiagramRelationship>? relationships = null) =>
        new("BlogContext", @"C:\repo\Snapshot.cs", "hash", "10.0.0", entities, relationships ?? []);

    // ---- Entities ----

    [Fact]
    public void MarksEverythingAddedWhenThereIsNoEarlierModel()
    {
        // The first migration. Everything in it genuinely is new, so marking it all up is the truth
        // rather than noise.
        var model = Model([Entity("Blog", Key(), new DiagramProperty("Name", "string"))]);

        var comparison = DiagramDiff.Compare(null, model);

        Assert.Same(model, comparison.Model);
        Assert.Equal(DiagramChange.Added, comparison.Diff.ForEntity("Blog"));
        Assert.Equal(DiagramChange.Added, comparison.Diff.ForRow("Blog", "Name"));
    }

    [Fact]
    public void MarksANewEntityAndAllOfItsColumnsAsAdded()
    {
        var previous = Model([Entity("Blog", Key())]);
        var current = Model([Entity("Blog", Key()), Entity("Post", Key(), new DiagramProperty("Title", "string"))]);

        var diff = DiagramDiff.Compare(previous, current).Diff;

        Assert.Equal(DiagramChange.None, diff.ForEntity("Blog"));
        Assert.Equal(DiagramChange.Added, diff.ForEntity("Post"));
        Assert.Equal(DiagramChange.Added, diff.ForRow("Post", "Title"));
    }

    [Fact]
    public void KeepsARemovedEntityInTheMergedModelSoItCanBeDrawn()
    {
        var previous = Model([Entity("Blog", Key()), Entity("Draft", Key())]);
        var current = Model([Entity("Blog", Key())]);

        var comparison = DiagramDiff.Compare(previous, current);

        Assert.NotNull(comparison.Model.Entity("Draft"));
        Assert.Equal(DiagramChange.Removed, comparison.Diff.ForEntity("Draft"));
        Assert.Equal(DiagramChange.Removed, comparison.Diff.ForRow("Draft", "Id"));
    }

    // ---- Columns ----

    [Fact]
    public void AppendsARemovedColumnToItsEntityAndMarksTheEntityModified()
    {
        var previous = Model([Entity("Blog", Key(), new DiagramProperty("Url", "string"))]);
        var current = Model([Entity("Blog", Key())]);

        var comparison = DiagramDiff.Compare(previous, current);
        var blog = comparison.Model.Entity("Blog")!;

        Assert.Equal(["Id", "Url"], blog.Properties.Select(p => p.Name));
        Assert.Equal(DiagramChange.Removed, comparison.Diff.ForRow("Blog", "Url"));
        Assert.Equal(DiagramChange.Modified, comparison.Diff.ForEntity("Blog"));
    }

    [Fact]
    public void MarksARetypedColumnAsModified()
    {
        // Nullability, length, default, key role — anything the parser reads counts, because the
        // comparison is record equality rather than a list of facets worth checking.
        var previous = Model([Entity("Blog", Key(), new DiagramProperty("Name", "string", MaxLength: 50))]);
        var current = Model([Entity("Blog", Key(), new DiagramProperty("Name", "string", MaxLength: 200))]);

        var diff = DiagramDiff.Compare(previous, current).Diff;

        Assert.Equal(DiagramChange.Modified, diff.ForRow("Blog", "Name"));
        Assert.Equal(DiagramChange.Modified, diff.ForEntity("Blog"));
    }

    [Fact]
    public void MarksNavigationsAddedAndRemoved()
    {
        var previous = Model([new DiagramEntity("Blog", Navigations: ["Posts"])]);
        var current = Model([new DiagramEntity("Blog", Navigations: ["Authors"])]);

        var comparison = DiagramDiff.Compare(previous, current);

        Assert.Equal(["Authors", "Posts"], comparison.Model.Entity("Blog")!.Navigations);
        Assert.Equal(DiagramChange.Added, comparison.Diff.ForRow("Blog", "Authors"));
        Assert.Equal(DiagramChange.Removed, comparison.Diff.ForRow("Blog", "Posts"));
    }

    [Fact]
    public void DoesNotConfuseRowsWhoseEntityAndColumnNamesRunTogether()
    {
        // "AB" + "C" and "A" + "BC" are the same string concatenated. They must not be the same key.
        var previous = Model([Entity("AB", Key("C")), Entity("A", Key("BC"))]);
        var current = Model([Entity("AB", Key("C")), Entity("A")]);

        var diff = DiagramDiff.Compare(previous, current).Diff;

        Assert.Equal(DiagramChange.None, diff.ForRow("AB", "C"));
        Assert.Equal(DiagramChange.Removed, diff.ForRow("A", "BC"));
    }

    // ---- Relationships ----

    [Fact]
    public void MarksARelationshipAddedWhicheverEndIsAskedAboutFirst()
    {
        var previous = Model([Entity("Blog", Key()), Entity("Post", Key())]);
        var current = Model(
            [Entity("Blog", Key()), Entity("Post", Key())],
            [new DiagramRelationship("Blog", "Post", ["BlogId"])]);

        var diff = DiagramDiff.Compare(previous, current).Diff;

        // A collapsed many-to-many edge does not know which end was the dependent, so the lookup
        // cannot depend on the order.
        Assert.Equal(DiagramChange.Added, diff.ForEdge("Post", "Blog"));
        Assert.Equal(DiagramChange.Added, diff.ForEdge("Blog", "Post"));
    }

    [Fact]
    public void KeepsARemovedRelationshipInTheMergedModel()
    {
        var previous = Model(
            [Entity("Blog", Key()), Entity("Post", Key())],
            [new DiagramRelationship("Blog", "Post", ["BlogId"])]);

        var current = Model([Entity("Blog", Key()), Entity("Post", Key())]);

        var comparison = DiagramDiff.Compare(previous, current);

        Assert.Single(comparison.Model.Relationships);
        Assert.Equal(DiagramChange.Removed, comparison.Diff.ForEdge("Post", "Blog"));
    }

    [Fact]
    public void CallsAForeignKeyThatMovedColumnsAddedRatherThanRemoved()
    {
        // One edge on screen, two answers available. "Added" says the more useful thing.
        var previous = Model(
            [Entity("Blog", Key()), Entity("Post", Key())],
            [new DiagramRelationship("Blog", "Post", ["BlogId"])]);

        var current = Model(
            [Entity("Blog", Key()), Entity("Post", Key())],
            [new DiagramRelationship("Blog", "Post", ["OwningBlogId"])]);

        var diff = DiagramDiff.Compare(previous, current).Diff;

        Assert.Equal(DiagramChange.Added, diff.ForEdge("Post", "Blog"));
    }

    // ---- Summary ----

    [Fact]
    public void ReportsNothingForTwoIdenticalModels()
    {
        var model = Model([Entity("Blog", Key())]);

        var diff = DiagramDiff.Compare(Model([Entity("Blog", Key())]), model).Diff;

        Assert.True(diff.IsEmpty);
        Assert.Equal("", diff.Summary);
    }

    [Fact]
    public void SummarisesTheCountsWithSignsAndPlurals()
    {
        var previous = Model([Entity("Blog", Key()), Entity("Draft", Key())]);
        var current = Model(
            [Entity("Blog", Key(), new DiagramProperty("Name", "string")), Entity("Post", Key())]);

        var summary = DiagramDiff.Compare(previous, current).Diff.Summary;

        // Draft's Id counts as a removed column alongside the removed table: dropping a table drops
        // its columns, and saying so is not double-counting.
        Assert.Equal("+1 table, −1 table, +2 columns, −1 column", summary);
    }

    // ---- What the diagram does with it ----

    [Fact]
    public void MarksTheNodesRowsAndEdgesTheDiagramBuildsFrom()
    {
        var previous = Model([Entity("Blog", Key())]);
        var current = Model(
            [Entity("Blog", Key(), new DiagramProperty("Name", "string")), Entity("Post", Key())],
            [new DiagramRelationship("Blog", "Post", ["BlogId"])]);

        var comparison = DiagramDiff.Compare(previous, current);
        var content = DiagramNodeContent.Build(
            comparison.Model, new DiagramViewOptions(), comparison.Diff);

        var blog = content.Nodes.Single(n => n.EntityName == "Blog");
        Assert.Equal(DiagramChange.Modified, blog.Change);
        Assert.Equal(DiagramChange.Added, blog.Rows.Single(r => r.Name == "Name").Change);
        Assert.Equal(DiagramChange.None, blog.Rows.Single(r => r.Name == "Id").Change);

        Assert.Equal(
            DiagramChange.Added,
            content.Nodes.Single(n => n.EntityName == "Post").Change);

        Assert.Equal(DiagramChange.Added, content.Edges.Single().Change);
    }

    [Fact]
    public void DrawsNothingDifferentlyWithoutADiff()
    {
        var content = DiagramNodeContent.Build(
            Model([Entity("Blog", Key())]), new DiagramViewOptions());

        Assert.All(content.Nodes, n => Assert.Equal(DiagramChange.None, n.Change));
        Assert.All(content.Nodes.SelectMany(n => n.Rows), r =>
            Assert.Equal(DiagramChange.None, r.Change));
    }
}
