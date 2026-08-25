using EfAssist.App.ViewModels;
using EfAssist.Core.Diagrams;

namespace EfAssist.Core.Tests;

/// <summary>
/// The Diagrams tab. Everything below the view model is a pure function with its own tests, so this
/// concentrates on the parts only the tab has: what triggers generation, what survives a restart, and
/// what happens when the snapshot moves underneath a saved diagram.
/// </summary>
public class DiagramsViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "EfAssistTests", Guid.NewGuid().ToString("N")[..8]);

    /// <summary>A real migrations project on disk, so the locator has something to find.</summary>
    private readonly string _project;

    private readonly string _snapshot;

    private const string Workspace = @"C:\repo\Sample.slnx";

    public DiagramsViewModelTests()
    {
        _project = Path.Combine(_root, "Data");
        Directory.CreateDirectory(Path.Combine(_project, "Migrations"));

        _snapshot = Path.Combine(_project, "Migrations", "RichContextModelSnapshot.cs");
        File.WriteAllText(_snapshot, Fixture.Text("snapshot-rich"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Fails every call. Nothing on this tab should reach <c>dotnet ef</c> except the
    /// pending-changes check, so a runner that only ever fails is enough to prove that.</summary>
    private sealed class NeverRuns : IEfRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<EfResult> RunAsync(
            IReadOnlyList<string> args,
            string workingDirectory,
            IProgress<OutputLine>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(args);
            return Task.FromResult(new EfResult(
                1, [new OutputLine(OutputChannel.Error, "no")], "dotnet " + string.Join(' ', args), ""));
        }
    }

    private sealed class Harness
    {
        public required DiagramsViewModel ViewModel { get; init; }

        public required NeverRuns Runner { get; init; }

        public required WorkspaceSettings Workspace { get; init; }

        public required DisplaySettings Display { get; init; }

        public int Persisted { get; set; }
    }

    private Harness Build(string? context = "RichContext", DisplaySettings? display = null)
    {
        var runner = new NeverRuns();
        var session = new CommandSession(runner) { PostToUiThread = action => action() };
        var settings = display ?? new DisplaySettings();
        var workspace = new WorkspaceSettings();

        Harness? harness = null;

        var viewModel = new DiagramsViewModel(
            session,
            () => new EfTarget(_project, _project, context),
            () => context,
            () => harness!.Persisted++,
            settings);

        harness = new Harness
        {
            ViewModel = viewModel,
            Runner = runner,
            Workspace = workspace,
            Display = settings,
        };

        viewModel.Restore(workspace, _root, DiagramsViewModelTests.Workspace);
        return harness;
    }

    // ---- Generation ----

    [Fact]
    public async Task GeneratesADiagramFromTheSnapshotWithoutRunningDotnetEf()
    {
        // The whole point of reading the snapshot: no build, no database, no CLI. If this tab ever
        // starts shelling out to generate, it has lost the property that makes it instant.
        var harness = Build();

        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.HasDiagram);
        Assert.Empty(harness.Runner.Calls);
        Assert.Equal("RichContext", harness.ViewModel.Model!.ContextName);
        Assert.Equal(_snapshot, harness.ViewModel.Model.SourcePath);
    }

    [Fact]
    public async Task DrawsANodeForEveryVisibleEntity()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        var scene = harness.ViewModel.Scene!;

        Assert.Contains("SampleRichModel.Blog", scene.Nodes.Keys);
        Assert.Contains("SampleRichModel.Post", scene.Nodes.Keys);
        Assert.All(scene.Nodes.Values, b => Assert.True(b.Width > 0 && b.Height > 0));
    }

    [Fact]
    public async Task ExplainsItselfWhenTheContextHasNoMigrations()
    {
        // An empty state, not an error: a context with no migrations legitimately has no snapshot.
        var harness = Build("AuditContext");

        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        Assert.False(harness.ViewModel.HasDiagram);
        Assert.NotNull(harness.ViewModel.EmptyReason);
        Assert.Contains("AuditContext", harness.ViewModel.EmptyReason);
    }

    [Fact]
    public void GeneratesNothingUntilAsked()
    {
        // Activating the tab loads a saved diagram, which is free. Parsing one the user did not ask
        // for is not the deal — same rule as DiscoveryMode.Cached.
        var harness = Build();

        _ = harness.ViewModel.OnActivatedAsync();

        Assert.False(harness.ViewModel.HasDiagram);
        Assert.Empty(harness.Runner.Calls);
    }

    // ---- Views ----

    [Fact]
    public async Task SwitchingViewRedrawsWithoutRereadingTheSnapshot()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        var before = harness.ViewModel.Scene;
        File.Delete(_snapshot);

        harness.ViewModel.SwitchViewCommand.Execute(null);

        // Still drawn with the snapshot deleted, which is only possible if the model was held rather
        // than re-read.
        Assert.Equal(DiagramKind.Class, harness.ViewModel.Kind);
        Assert.True(harness.ViewModel.HasDiagram);
        Assert.NotSame(before, harness.ViewModel.Scene);
    }

    [Fact]
    public void OpensOnTheConfiguredDefaultView()
    {
        var harness = Build(display: new DisplaySettings { DefaultDiagramKind = DiagramKind.Class });

        Assert.Equal(DiagramKind.Class, harness.ViewModel.Kind);
    }

    [Fact]
    public void PrefersTheWorkspacesOwnChoiceOverTheDefault()
    {
        var display = new DisplaySettings { DefaultDiagramKind = DiagramKind.Class };
        var harness = Build(display: display);

        harness.ViewModel.Restore(
            new WorkspaceSettings { DiagramView = DiagramKind.EntityRelationship },
            _root,
            Workspace);

        Assert.Equal(DiagramKind.EntityRelationship, harness.ViewModel.Kind);
    }

    [Fact]
    public async Task TogglingAnOptionRedrawsTheDiagram()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        Assert.DoesNotContain("PostTag", harness.ViewModel.Scene!.Nodes.Keys);

        harness.ViewModel.CollapseJoinEntities = false;

        Assert.Contains("PostTag", harness.ViewModel.Scene!.Nodes.Keys);
    }

    // ---- Selection and detail ----

    [Fact]
    public async Task SelectingAnEntityFillsTheDetailPane()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        harness.ViewModel.Select("SampleRichModel.Post");

        var groups = harness.ViewModel.Detail.ToList();
        Assert.Contains(groups, g => g.Title == "Entity");
        Assert.Contains(groups, g => g.Title == "Properties");
        Assert.Contains(groups, g => g.Title == "References");
        Assert.Contains(groups, g => g.Title == "Referenced by");

        var properties = groups.Single(g => g.Title == "Properties").Rows;
        Assert.Contains(properties, r => r.Name == "Title" && r.Note!.Contains("max 300"));
        Assert.Contains(properties, r => r.Name == "Id" && r.Note!.Contains("key"));
    }

    [Fact]
    public async Task RelationshipRowsLinkToTheOtherEnd()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);
        harness.ViewModel.Select("SampleRichModel.Post");

        var link = harness.ViewModel.Detail
            .Single(g => g.Title == "References").Rows
            .First(r => r.Target == "SampleRichModel.Blog");

        Assert.True(link.IsLink);

        harness.ViewModel.SelectEntityCommand.Execute(link.Target);
        Assert.Equal("SampleRichModel.Blog", harness.ViewModel.SelectedEntity);
    }

    [Fact]
    public async Task ClearsASelectionThatTheCurrentOptionsHide()
    {
        // Collapsing join tables removes PostTag from the diagram, and a detail pane describing a node
        // that is no longer on screen is worse than an empty one.
        var harness = Build();
        harness.ViewModel.CollapseJoinEntities = false;
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        harness.ViewModel.Select("PostTag");
        Assert.Equal("PostTag", harness.ViewModel.SelectedEntity);

        harness.ViewModel.CollapseJoinEntities = true;

        Assert.Null(harness.ViewModel.SelectedEntity);
        Assert.Empty(harness.ViewModel.Detail);
    }

    // ---- Search ----

    [Fact]
    public async Task SearchesEntityTableColumnAndIndexNames()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        harness.ViewModel.Search = "CreditLimit";

        Assert.True(harness.ViewModel.HasMatches);
        Assert.Equal("1 of 1", Advance(harness.ViewModel));
        Assert.Equal("SampleRichModel.Customer", harness.ViewModel.SelectedEntity);

        harness.ViewModel.Search = "IX_Post_Title";
        Assert.True(harness.ViewModel.HasMatches);

        harness.ViewModel.Search = "Posts";
        Assert.True(harness.ViewModel.HasMatches);
    }

    [Fact]
    public async Task FindsAColumnTheCurrentPropertyFilterIsHiding()
    {
        // Otherwise a search reports nothing and looks broken, when the column is right there.
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        harness.ViewModel.Properties = PropertyDetail.KeysOnly;
        harness.ViewModel.Search = "CreditLimit";

        Assert.True(harness.ViewModel.HasMatches);
    }

    [Fact]
    public async Task CyclesThroughMatchesAndSaysWhereItIs()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        harness.ViewModel.Search = "Id";

        // A total until Next has been pressed. "0 of 9" would describe a position that does not exist.
        Assert.EndsWith(" matches", harness.ViewModel.SearchSummary);

        var first = Advance(harness.ViewModel);
        var second = Advance(harness.ViewModel);

        Assert.StartsWith("1 of ", first);
        Assert.StartsWith("2 of ", second);
    }

    [Fact]
    public async Task ReportsNoMatchesRatherThanNothing()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        harness.ViewModel.Search = "nothing matches this";

        Assert.False(harness.ViewModel.HasMatches);
        Assert.Equal("No matches", harness.ViewModel.SearchSummary);
    }

    [Fact]
    public async Task ClearingTheSearchStopsSearching()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        harness.ViewModel.Search = "Blog";
        Assert.True(harness.ViewModel.IsSearching);

        harness.ViewModel.ClearSearchCommand.Execute(null);
        Assert.False(harness.ViewModel.IsSearching);
    }

    private static string Advance(DiagramsViewModel viewModel)
    {
        viewModel.NextMatchCommand.Execute(null);
        return viewModel.SearchSummary;
    }

    // ---- Dragging ----

    [Fact]
    public async Task RemembersWhereANodeWasDragged()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        harness.ViewModel.MoveNode("SampleRichModel.Blog", new DiagramPoint(500, 400));

        Assert.Equal(
            new DiagramRect(500, 400, 0, 0).TopLeft,
            harness.ViewModel.Scene!.Nodes["SampleRichModel.Blog"].TopLeft);
    }

    [Fact]
    public async Task DraggingOneNodeLeavesTheRestWhereTheyWere()
    {
        // Pinning only the dragged node lets the layout re-flow everything around it, so the first
        // drag rearranges the whole diagram.
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        var before = harness.ViewModel.Scene!.Nodes.ToDictionary(n => n.Key, n => n.Value.TopLeft);

        harness.ViewModel.MoveNode("SampleRichModel.Blog", new DiagramPoint(500, 400));

        foreach (var (name, position) in before.Where(p => p.Key != "SampleRichModel.Blog"))
        {
            Assert.Equal(position, harness.ViewModel.Scene!.Nodes[name].TopLeft);
        }
    }

    [Fact]
    public async Task IgnoresADragOfSomethingThatIsNotOnTheDiagram()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        harness.ViewModel.MoveNode("Nope.NotHere", new DiagramPoint(1, 1));

        Assert.DoesNotContain("Nope.NotHere", harness.ViewModel.Scene!.Nodes.Keys);
    }

    // ---- Persistence ----

    [Fact]
    public async Task ADiagramComesBackAfterARestartWithoutRegenerating()
    {
        var first = Build();
        await first.ViewModel.GenerateCommand.ExecuteAsync(null);
        first.ViewModel.MoveNode("SampleRichModel.Blog", new DiagramPoint(500, 400));
        first.ViewModel.CommitMove();

        // A second view model over the same folder is what a restart looks like.
        var second = Build();

        Assert.True(second.ViewModel.HasDiagram);
        Assert.Empty(second.Runner.Calls);
        Assert.Equal(
            new DiagramPoint(500, 400),
            second.ViewModel.Scene!.Nodes["SampleRichModel.Blog"].TopLeft);
    }

    [Fact]
    public async Task EachViewKeepsItsOwnArrangement()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        harness.ViewModel.MoveNode("SampleRichModel.Blog", new DiagramPoint(500, 400));
        harness.ViewModel.CommitMove();

        harness.ViewModel.SwitchViewCommand.Execute(null);
        Assert.NotEqual(
            new DiagramPoint(500, 400),
            harness.ViewModel.Scene!.Nodes["SampleRichModel.Blog"].TopLeft);

        harness.ViewModel.MoveNode("SampleRichModel.Blog", new DiagramPoint(900, 100));
        harness.ViewModel.CommitMove();

        harness.ViewModel.SwitchViewCommand.Execute(null);
        Assert.Equal(
            new DiagramPoint(500, 400),
            harness.ViewModel.Scene!.Nodes["SampleRichModel.Blog"].TopLeft);
    }

    [Fact]
    public async Task RegeneratingAfterTheModelChangesKeepsTheArrangement()
    {
        // Adding one entity to a hand-arranged diagram must move that entity and nothing else.
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        harness.ViewModel.MoveNode("SampleRichModel.Blog", new DiagramPoint(500, 400));
        harness.ViewModel.CommitMove();

        File.WriteAllText(_snapshot, Fixture.Text("snapshot-rich").Replace(
            """modelBuilder.Entity("SampleRichModel.Author", b =>""",
            """
            modelBuilder.Entity("SampleRichModel.Extra", b =>
                {
                    b.Property<int>("Id").HasColumnType("INTEGER");
                    b.HasKey("Id");
                    b.ToTable("Extras");
                });

            modelBuilder.Entity("SampleRichModel.Author", b =>
            """));

        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        Assert.Contains("SampleRichModel.Extra", harness.ViewModel.Scene!.Nodes.Keys);
        Assert.Equal(
            new DiagramPoint(500, 400),
            harness.ViewModel.Scene.Nodes["SampleRichModel.Blog"].TopLeft);
    }

    [Fact]
    public async Task ReLayoutDiscardsTheArrangementForThisViewOnly()
    {
        var harness = Build();
        harness.ViewModel.ConfirmAsync = _ => Task.FromResult(true);
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        harness.ViewModel.MoveNode("SampleRichModel.Blog", new DiagramPoint(500, 400));
        harness.ViewModel.SwitchViewCommand.Execute(null);
        harness.ViewModel.MoveNode("SampleRichModel.Blog", new DiagramPoint(900, 100));
        harness.ViewModel.CommitMove();

        await harness.ViewModel.ReLayoutCommand.ExecuteAsync(null);

        Assert.NotEqual(
            new DiagramPoint(900, 100),
            harness.ViewModel.Scene!.Nodes["SampleRichModel.Blog"].TopLeft);

        harness.ViewModel.SwitchViewCommand.Execute(null);
        Assert.Equal(
            new DiagramPoint(500, 400),
            harness.ViewModel.Scene!.Nodes["SampleRichModel.Blog"].TopLeft);
    }

    [Fact]
    public async Task ReLayoutCanBeDeclined()
    {
        var harness = Build();
        harness.ViewModel.ConfirmAsync = _ => Task.FromResult(false);
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        harness.ViewModel.MoveNode("SampleRichModel.Blog", new DiagramPoint(500, 400));
        await harness.ViewModel.ReLayoutCommand.ExecuteAsync(null);

        Assert.Equal(
            new DiagramPoint(500, 400),
            harness.ViewModel.Scene!.Nodes["SampleRichModel.Blog"].TopLeft);
    }

    [Fact]
    public async Task RemembersTheViewTheLockAndTheOptions()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        harness.ViewModel.SwitchViewCommand.Execute(null);
        harness.ViewModel.ToggleLockCommand.Execute(null);
        harness.ViewModel.ShowIndexes = true;

        var second = Build();

        Assert.Equal(DiagramKind.Class, second.ViewModel.Kind);
        Assert.True(second.ViewModel.IsUnlocked);
        Assert.True(second.ViewModel.ShowIndexes);
    }

    [Fact]
    public void StoresItsChoicesIntoTheWorkspaceSettings()
    {
        var harness = Build();
        harness.ViewModel.SwitchViewCommand.Execute(null);
        harness.ViewModel.ShowDeleteBehavior = true;

        var settings = new WorkspaceSettings();
        harness.ViewModel.Store(settings);

        Assert.Equal(DiagramKind.Class, settings.DiagramView);
        Assert.True(settings.DiagramLocked);
        Assert.True(settings.DiagramOptions!.ShowDeleteBehavior);
    }

    [Fact]
    public void StartsLocked()
    {
        // Panning and zooming cannot lose work; dragging can.
        Assert.False(Build().ViewModel.IsUnlocked);
    }

    // ---- Staleness ----

    [Fact]
    public async Task BadgesASavedDiagramWhoseSnapshotHasChangedSince()
    {
        var first = Build();
        await first.ViewModel.GenerateCommand.ExecuteAsync(null);
        Assert.False(first.ViewModel.IsStale);

        File.AppendAllText(_snapshot, "\n// a new migration");

        var second = Build();

        Assert.True(second.ViewModel.IsStale);

        // Still drawn. A stale diagram is readable; it just stops looking current.
        Assert.True(second.ViewModel.HasDiagram);
    }

    [Fact]
    public async Task RegeneratingClearsTheStaleBadge()
    {
        var first = Build();
        await first.ViewModel.GenerateCommand.ExecuteAsync(null);
        File.AppendAllText(_snapshot, "\n// a new migration");

        var second = Build();
        Assert.True(second.ViewModel.IsStale);

        await second.ViewModel.GenerateCommand.ExecuteAsync(null);
        Assert.False(second.ViewModel.IsStale);
    }

    [Fact]
    public async Task ThePendingChangesCheckIsTheOneThingHereThatRunsDotnetEf()
    {
        // It builds the startup project, which is exactly why it is a separate button rather than
        // part of generating.
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);
        Assert.Empty(harness.Runner.Calls);

        await harness.ViewModel.CheckPendingChangesCommand.ExecuteAsync(null);

        Assert.Single(harness.Runner.Calls);
        Assert.Contains("has-pending-model-changes", harness.Runner.Calls[0]);
    }

    // ---- Context switching ----

    [Fact]
    public async Task SwitchingContextSwapsInThatContextsDiagram()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);
        Assert.True(harness.ViewModel.HasDiagram);

        // A context with no saved diagram of its own.
        var other = Build("AuditContext");

        Assert.False(other.ViewModel.HasDiagram);

        // And back again, without regenerating.
        var back = Build();
        Assert.True(back.ViewModel.HasDiagram);
    }

    [Fact]
    public void ClearingTheWorkspaceLeavesNothingBehind()
    {
        var harness = Build();
        harness.ViewModel.Clear();

        Assert.False(harness.ViewModel.HasDiagram);
        Assert.Null(harness.ViewModel.Model);
        Assert.Null(harness.ViewModel.SelectedEntity);
    }
}
