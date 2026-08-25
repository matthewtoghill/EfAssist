using EfAssist.Core.Diagrams;

namespace EfAssist.Core.Tests;

public class DiagramStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "EfAssistTests", Guid.NewGuid().ToString("N")[..8]);

    private readonly string _workspace = @"C:\repos\Sample\Sample.slnx";

    public DiagramStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private static SavedDiagram Sample(DiagramModel? model = null)
    {
        var diagram = new SavedDiagram
        {
            Model = model ?? ModelSnapshotParser.Parse(Fixture.Text("snapshot-rich")),
            Locked = false,
            Kind = DiagramKind.Class,
            Options = new DiagramViewOptions { ShowIndexes = true, ShowTypes = false },
        };

        diagram.SetPositions(DiagramKind.EntityRelationship, new Dictionary<string, DiagramPoint>
        {
            ["SampleRichModel.Blog"] = new DiagramPoint(10, 20),
        });

        return diagram;
    }

    // ---- Paths ----

    [Fact]
    public void FilesADiagramUnderTheSameWorkspaceKeyAsItsSettings()
    {
        var path = DiagramStore.Path(_root, _workspace, "RichContext")!;

        Assert.Contains(SettingsStore.WorkspaceKey(_workspace), path);
        Assert.EndsWith(Path.Combine("RichContext.json"), path);
        Assert.Contains("diagrams", path);
    }

    [Fact]
    public void KeepsTwoContextsInOneWorkspaceApart()
    {
        Assert.NotEqual(
            DiagramStore.Path(_root, _workspace, "BlogContext"),
            DiagramStore.Path(_root, _workspace, "AuditContext"));
    }

    [Fact]
    public void MakesAFullyQualifiedContextNameSafeAsAFileName()
    {
        // dbcontext list --json returns the full name, and a path separator in it would write outside
        // the diagrams folder.
        var path = DiagramStore.Path(_root, _workspace, "Sample.Data/Blog:Context")!;

        Assert.DoesNotContain('/', Path.GetFileName(path));
        Assert.DoesNotContain(':', Path.GetFileName(path));
    }

    [Fact]
    public void HasNoPathBeforeSettingsHaveALocation()
    {
        // A first run: nothing has a folder yet, which is not an error.
        Assert.Null(DiagramStore.Path(null, _workspace, "RichContext"));
        Assert.Null(DiagramStore.Load(null, _workspace, "RichContext"));
        Assert.False(DiagramStore.Save(null, _workspace, "RichContext", new SavedDiagram()));
    }

    // ---- Round trip ----

    [Fact]
    public void RoundTripsADiagram()
    {
        var saved = Sample();
        Assert.True(DiagramStore.Save(_root, _workspace, "RichContext", saved));

        var loaded = DiagramStore.Load(_root, _workspace, "RichContext")!;

        Assert.Equal(saved.Kind, loaded.Kind);
        Assert.Equal(saved.Locked, loaded.Locked);
        Assert.Equal(saved.Model!.SourceHash, loaded.Model!.SourceHash);
        Assert.Equal(saved.Model.Entities.Count, loaded.Model.Entities.Count);
        Assert.True(loaded.Options!.ShowIndexes);
        Assert.False(loaded.Options.ShowTypes);
    }

    [Fact]
    public void RoundTripsTheWholeModelIncludingItsAwkwardParts()
    {
        DiagramStore.Save(_root, _workspace, "RichContext", Sample());
        var loaded = DiagramStore.Load(_root, _workspace, "RichContext")!.Model!;

        var post = loaded.Entity("SampleRichModel.Post")!;
        Assert.Equal(["Id"], post.Keys);
        Assert.True(post.Properties.Single(p => p.Name == "BlogId").IsForeignKey);
        Assert.Equal("IX_Post_Title", post.Indexes.Single(i => i.DatabaseName is not null).DatabaseName);

        Assert.True(loaded.Entity("PostTag")!.IsImplicitJoin);
        Assert.Contains(loaded.Entities, e => e.IsOwned);
        Assert.Equal(
            "Cascade",
            loaded.Relationships.First(r => r.DependentEntity == "SampleRichModel.PostStatistics")
                .DeleteBehavior);
    }

    [Fact]
    public void KeepsPositionsPerViewSoOneArrangementCannotOverwriteTheOther()
    {
        // The two views put different rows in a node, so the same entity is a different height in
        // each. One shared position set means an arrangement made in one view overlaps in the other.
        var saved = Sample();
        saved.SetPositions(DiagramKind.Class, new Dictionary<string, DiagramPoint>
        {
            ["SampleRichModel.Blog"] = new DiagramPoint(999, 888),
        });

        DiagramStore.Save(_root, _workspace, "RichContext", saved);
        var loaded = DiagramStore.Load(_root, _workspace, "RichContext")!;

        Assert.Equal(
            new DiagramPoint(10, 20),
            loaded.PositionsFor(DiagramKind.EntityRelationship)["SampleRichModel.Blog"]);
        Assert.Equal(
            new DiagramPoint(999, 888),
            loaded.PositionsFor(DiagramKind.Class)["SampleRichModel.Blog"]);
    }

    [Fact]
    public void ReportsNoPositionsForAViewThatHasNeverBeenArranged() =>
        Assert.Empty(new SavedDiagram().PositionsFor(DiagramKind.Class));

    [Fact]
    public void DefaultsToLocked()
    {
        // Panning and zooming cannot lose work; dragging can.
        Assert.True(new SavedDiagram().Locked);

        DiagramStore.Save(_root, _workspace, "Fresh", new SavedDiagram { Model = DiagramModel.Empty });
        Assert.True(DiagramStore.Load(_root, _workspace, "Fresh")!.Locked);
    }

    // ---- Failure ----

    [Fact]
    public void ReturnsNullWhenThereIsNoSavedDiagram() =>
        Assert.Null(DiagramStore.Load(_root, _workspace, "NeverSaved"));

    [Fact]
    public void TreatsACorruptFileAsNothingSaved()
    {
        var path = DiagramStore.Path(_root, _workspace, "Broken")!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not json");

        // "Regenerate" is exactly what the empty state already offers, so a corrupt cache is not an
        // error worth surfacing.
        Assert.Null(DiagramStore.Load(_root, _workspace, "Broken"));
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void DeletesADiagramWithoutComplainingAboutOneThatIsNotThere()
    {
        DiagramStore.Save(_root, _workspace, "RichContext", Sample());
        DiagramStore.Delete(_root, _workspace, "RichContext");
        DiagramStore.Delete(_root, _workspace, "RichContext");

        Assert.Null(DiagramStore.Load(_root, _workspace, "RichContext"));
    }

    // ---- Staleness ----

    [Fact]
    public void IsStaleWhenTheSnapshotHasChangedSince()
    {
        var snapshot = Path.Combine(_root, "Snapshot.cs");
        File.WriteAllText(snapshot, Fixture.Text("snapshot-simple"));

        var model = ModelSnapshotParser.Parse(File.ReadAllText(snapshot), snapshot);
        Assert.False(DiagramStore.IsStale(model));

        File.AppendAllText(snapshot, "\n// a new migration was added");
        Assert.True(DiagramStore.IsStale(model));
    }

    [Fact]
    public void IsNotStaleWhenTheSnapshotWasTouchedButNotChanged()
    {
        // Re-hashing rather than comparing timestamps. A checkout or a formatter run rewrites the
        // file without changing the model, and badging that as out of date cries wolf.
        var snapshot = Path.Combine(_root, "Snapshot.cs");
        var text = Fixture.Text("snapshot-simple");
        File.WriteAllText(snapshot, text);

        var model = ModelSnapshotParser.Parse(text, snapshot);
        File.WriteAllText(snapshot, text);

        Assert.False(DiagramStore.IsStale(model));
    }

    [Fact]
    public void IsStaleWhenTheSnapshotHasGone()
    {
        var snapshot = Path.Combine(_root, "Gone.cs");
        File.WriteAllText(snapshot, Fixture.Text("snapshot-simple"));
        var model = ModelSnapshotParser.Parse(File.ReadAllText(snapshot), snapshot);

        File.Delete(snapshot);

        Assert.True(DiagramStore.IsStale(model));
    }

    [Fact]
    public void IsNotStaleWithNothingToCompareAgainst()
    {
        // No recorded source, so there is no answer. Badging it would be a guess.
        Assert.False(DiagramStore.IsStale(null));
        Assert.False(DiagramStore.IsStale(DiagramModel.Empty));
    }
}
