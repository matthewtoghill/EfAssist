using Avalonia.Layout;
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

        public required CommandSession Session { get; init; }

        public required WorkspaceSettings Workspace { get; init; }

        public required DisplaySettings Display { get; init; }

        public int Persisted { get; set; }
    }

    private Harness Build(
        string? context = "RichContext",
        DisplaySettings? display = null,
        IReadOnlyList<MigrationInfo>? migrations = null)
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
            () => migrations ?? [],
            () => harness!.Persisted++,
            settings);

        harness = new Harness
        {
            ViewModel = viewModel,
            Runner = runner,
            Session = session,
            Workspace = workspace,
            Display = settings,
        };

        viewModel.Restore(workspace, _root, DiagramsViewModelTests.Workspace);
        return harness;
    }

    // ---- Per-migration snapshots ----

    private const string InitialId = "20260101000000_InitialCreate";
    private const string AddPostsId = "20260102000000_AddPosts";

    private static readonly MigrationInfo Initial =
        new(InitialId, "InitialCreate", "InitialCreate", true);

    private static readonly MigrationInfo AddPosts =
        new(AddPostsId, "AddPosts", "AddPosts", true);

    private const string BlogEntity = """
            modelBuilder.Entity("SampleEfApp.Blog", b =>
                {
                    b.Property<int>("Id").HasColumnType("INTEGER");
                    b.HasKey("Id");
                    b.ToTable("Blogs");
                });
    """;

    private const string PostEntity = """
            modelBuilder.Entity("SampleEfApp.Post", b =>
                {
                    b.Property<int>("Id").HasColumnType("INTEGER");
                    b.Property<string>("Title").IsRequired().HasColumnType("TEXT");
                    b.HasKey("Id");
                    b.ToTable("Posts");
                });
    """;

    /// <summary>
    /// Writes a migration and its <c>.Designer.cs</c> sibling, which is where the model as of that
    /// migration lives. <paramref name="entities"/> is the body of <c>BuildTargetModel</c>.
    /// </summary>
    private void WriteMigration(string id, string? entities)
    {
        var folder = Path.Combine(_project, "Migrations");
        File.WriteAllText(Path.Combine(folder, id + ".cs"), "// the Up and Down methods");

        if (entities is null)
        {
            return;
        }

        File.WriteAllText(Path.Combine(folder, id + ".Designer.cs"), $$"""
            // <auto-generated />
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Infrastructure;
            using Microsoft.EntityFrameworkCore.Migrations;

            namespace SampleEfApp.Migrations
            {
                [DbContext(typeof(RichContext))]
                [Migration("{{id}}")]
                partial class Snapshot
                {
                    protected override void BuildTargetModel(ModelBuilder modelBuilder)
                    {
                        modelBuilder.HasAnnotation("ProductVersion", "10.0.10");
            {{entities}}
                    }
                }
            }
            """);
    }

    [Fact]
    public async Task DrawsTheModelAsOfTheSelectedMigrationFromItsDesignerFile()
    {
        WriteMigration(InitialId, BlogEntity);
        var harness = Build(migrations: [Initial]);

        harness.ViewModel.RefreshSnapshotOptions();
        harness.ViewModel.SelectedSnapshot = "InitialCreate";
        await harness.ViewModel.GenerateCommand.ExecutionTask!;

        // Not the context's ModelSnapshot.cs, which has a whole different model in it.
        Assert.Equal(
            Path.Combine(_project, "Migrations", InitialId + ".Designer.cs"),
            harness.ViewModel.Model!.SourcePath);

        Assert.Equal(["SampleEfApp.Blog"], harness.ViewModel.Model.Entities.Select(e => e.Name));
        Assert.Empty(harness.Runner.Calls);
    }

    [Fact]
    public void OffersTheCurrentModelFirstAndThenEveryMigrationNewestFirstAndNumbered()
    {
        var harness = Build(migrations: [Initial, AddPosts]);

        harness.ViewModel.RefreshSnapshotOptions();

        // Numbered by chronological position, listed newest first, so the number counts down.
        Assert.Equal(
            [DiagramsViewModel.CurrentModel, "2. AddPosts", "1. InitialCreate"],
            harness.ViewModel.SnapshotOptions);

        Assert.Equal(DiagramsViewModel.CurrentModel, harness.ViewModel.SelectedSnapshot);
        Assert.False(harness.ViewModel.IsMigrationSelected);
    }

    [Fact]
    public void KeepsNoBlankEntryWhenTheComboBoxClearsItsSelection()
    {
        var harness = Build(migrations: [Initial, AddPosts]);

        // What Avalonia writes back when the options it was bound to are cleared.
        harness.ViewModel.SelectedSnapshot = string.Empty;
        harness.ViewModel.RefreshSnapshotOptions();

        Assert.Equal(
            [DiagramsViewModel.CurrentModel, "2. AddPosts", "1. InitialCreate"],
            harness.ViewModel.SnapshotOptions);

        Assert.Equal(DiagramsViewModel.CurrentModel, harness.ViewModel.SelectedSnapshot);
    }

    [Fact]
    public async Task MarksWhatTheSelectedMigrationAddedAgainstTheOneBeforeIt()
    {
        WriteMigration(InitialId, BlogEntity);
        WriteMigration(AddPostsId, BlogEntity + PostEntity);
        var harness = Build(migrations: [Initial, AddPosts]);

        harness.ViewModel.RefreshSnapshotOptions();
        harness.ViewModel.SelectedSnapshot = "2. AddPosts";
        await harness.ViewModel.GenerateCommand.ExecutionTask!;

        Assert.True(harness.ViewModel.HasDiagram);
        Assert.True(harness.ViewModel.ShowsDiff);
        Assert.Equal("AddPosts: +1 table, +2 columns", harness.ViewModel.DiffSummary);
    }

    [Fact]
    public async Task ComparesTheFirstMigrationAgainstNothingSoAllOfItIsNew()
    {
        WriteMigration(InitialId, BlogEntity);
        var harness = Build(migrations: [Initial]);

        harness.ViewModel.RefreshSnapshotOptions();
        harness.ViewModel.SelectedSnapshot = "InitialCreate";
        await harness.ViewModel.GenerateCommand.ExecutionTask!;

        Assert.Equal("InitialCreate: +1 table, +1 column", harness.ViewModel.DiffSummary);
    }

    [Fact]
    public async Task DrawsNoDiffForTheCurrentModel()
    {
        var harness = Build(migrations: [Initial]);

        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.HasDiagram);
        Assert.False(harness.ViewModel.ShowsDiff);
        Assert.Null(harness.ViewModel.DiffSummary);
    }

    [Fact]
    public async Task TurningOffTheHighlightRedrawsWithoutRereadingAnything()
    {
        WriteMigration(InitialId, BlogEntity);
        WriteMigration(AddPostsId, BlogEntity + PostEntity);
        var harness = Build(migrations: [Initial, AddPosts]);

        harness.ViewModel.RefreshSnapshotOptions();
        harness.ViewModel.SelectedSnapshot = "AddPosts";
        await harness.ViewModel.GenerateCommand.ExecutionTask!;

        harness.ViewModel.HighlightChanges = false;

        Assert.False(harness.ViewModel.ShowsDiff);
        Assert.True(harness.ViewModel.HasDiagram);
    }

    [Fact]
    public async Task SaysWhichMigrationHasNoDesignerFileRatherThanFailingSilently()
    {
        // A migration whose .Designer.cs was deleted, or a hand-written one that never had it.
        WriteMigration(InitialId, entities: null);
        var harness = Build(migrations: [Initial]);

        harness.ViewModel.RefreshSnapshotOptions();
        harness.ViewModel.SelectedSnapshot = "InitialCreate";
        await harness.ViewModel.GenerateCommand.ExecutionTask!;

        Assert.False(harness.ViewModel.HasDiagram);
        Assert.Contains("InitialCreate", harness.ViewModel.EmptyReason);
        Assert.Contains(".Designer.cs", harness.ViewModel.EmptyReason);
    }

    [Fact]
    public async Task RemembersWhichMigrationWasBeingLookedAtAndItsDiff()
    {
        WriteMigration(InitialId, BlogEntity);
        WriteMigration(AddPostsId, BlogEntity + PostEntity);

        var first = Build(migrations: [Initial, AddPosts]);
        first.ViewModel.RefreshSnapshotOptions();
        first.ViewModel.SelectedSnapshot = "AddPosts";
        await first.ViewModel.GenerateCommand.ExecutionTask!;

        // A fresh view model over the same folder is what a restart looks like.
        var second = Build(migrations: [Initial, AddPosts]);
        second.ViewModel.RefreshSnapshotOptions();

        Assert.Equal("2. AddPosts", second.ViewModel.SelectedSnapshot);
        Assert.True(second.ViewModel.HasDiagram);
        Assert.Equal("AddPosts: +1 table, +2 columns", second.ViewModel.DiffSummary);
        Assert.Empty(second.Runner.Calls);
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

    // ---- Class filter ----

    [Fact]
    public async Task ShowsOnlyTheSelectedClassAndThenExpandsOneLevelAtATime()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        harness.ViewModel.Select("SampleRichModel.Comment");
        harness.ViewModel.FocusSelectedCommand.Execute(null);

        Assert.True(harness.ViewModel.IsFiltered);
        Assert.Equal(["SampleRichModel.Comment"], harness.ViewModel.Scene!.Nodes.Keys);

        // One level out: the post a comment belongs to. Not yet the blog that post belongs to.
        harness.ViewModel.ExpandOneLevelCommand.Execute(null);

        Assert.Contains("SampleRichModel.Post", harness.ViewModel.Scene!.Nodes.Keys);
        Assert.DoesNotContain("SampleRichModel.Blog", harness.ViewModel.Scene!.Nodes.Keys);

        // And the next level brings it in, from both ends of the post's relationships.
        harness.ViewModel.ExpandOneLevelCommand.Execute(null);

        Assert.Contains("SampleRichModel.Blog", harness.ViewModel.Scene!.Nodes.Keys);
        Assert.Contains("SampleRichModel.Author", harness.ViewModel.Scene!.Nodes.Keys);

        // A many-to-many is one level even though a hidden join entity sits in the middle of it.
        Assert.Contains("SampleRichModel.Tag", harness.ViewModel.Scene!.Nodes.Keys);

        harness.ViewModel.ShowAllClassesCommand.Execute(null);

        Assert.False(harness.ViewModel.IsFiltered);
        Assert.Contains("SampleRichModel.Person", harness.ViewModel.Scene!.Nodes.Keys);
    }

    [Fact]
    public async Task KeepsTheFullDetailOfAClassWhileTheDiagramIsFiltered()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        harness.ViewModel.Select("SampleRichModel.Post");
        harness.ViewModel.FocusSelectedCommand.Execute(null);

        // Nothing the post relates to is drawn, but the pane still lists every relationship it has.
        var groups = harness.ViewModel.Detail.ToList();
        Assert.Contains(groups, g => g.Title == "References");
        Assert.Contains(groups, g => g.Title == "Referenced by");
    }

    [Fact]
    public async Task UntickingEveryClassButOneIsTheSameAsFocusingIt()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        Assert.NotEmpty(harness.ViewModel.ClassFilter);
        Assert.All(harness.ViewModel.ClassFilter, item => Assert.True(item.IsShown));

        foreach (var item in harness.ViewModel.ClassFilter.Where(i => i.Name != "SampleRichModel.Tag"))
        {
            item.IsShown = false;
        }

        Assert.Equal(["SampleRichModel.Tag"], harness.ViewModel.Scene!.Nodes.Keys);
        Assert.Contains("1 of", harness.ViewModel.FilterSummary);

        // Ticking the last one back on is the whole model again, not a filter that happens to
        // include everything.
        foreach (var item in harness.ViewModel.ClassFilter)
        {
            item.IsShown = true;
        }

        Assert.False(harness.ViewModel.IsFiltered);
        Assert.Null(harness.ViewModel.FilterSummary);
    }

    [Fact]
    public async Task NamesAnOwnedTypeAfterItsOwnerAndLeavesOutWhateverTheOptionsFoldAway()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        // An owned collection has a table of its own, so it is drawn and can be ticked. Two owners can
        // own the same type, so the row says whose it is.
        var contacts = harness.ViewModel.ClassFilter
            .Single(i => i.Name.EndsWith("#SampleRichModel.ContactMethod", StringComparison.Ordinal));

        Assert.Equal("ContactMethod (owned by Author)", contacts.Label);

        // The inlined owned reference and the collapsed join table are not drawn whatever the filter
        // says, so neither is offered to tick.
        Assert.DoesNotContain(
            harness.ViewModel.ClassFilter,
            i => i.Name.EndsWith("#SampleRichModel.Address", StringComparison.Ordinal));

        Assert.DoesNotContain(harness.ViewModel.ClassFilter, i => i.Name == "PostTag");

        // Turning the options off puts them back, ticked, because they were never unticked.
        harness.ViewModel.InlineOwnedTypes = false;
        harness.ViewModel.CollapseJoinEntities = false;

        var address = harness.ViewModel.ClassFilter
            .Single(i => i.Name.EndsWith("#SampleRichModel.Address", StringComparison.Ordinal));

        Assert.Equal("Address (owned by Author)", address.Label);
        Assert.True(address.IsShown);
        Assert.Contains(harness.ViewModel.ClassFilter, i => i.Name == "PostTag");
    }

    [Fact]
    public async Task TellsTheSummaryAboutTheFilterAfterTheClassListIsFilledInNotBefore()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        harness.ViewModel.Select("SampleRichModel.Tag");
        harness.ViewModel.FocusSelectedCommand.Execute(null);

        // The value as of each notification, because that is all a binding ever sees. Reading the
        // property afterwards would pass whether or not anything was raised.
        string? last = null;
        harness.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DiagramsViewModel.FilterSummary))
            {
                last = harness.ViewModel.FilterSummary;
            }
        };

        // Reopening: the restored filter is notified about before the first render has built the
        // class list, so the last word on the subject has to come after it, not before.
        harness.ViewModel.Restore(harness.Workspace, _root, Workspace);

        Assert.Equal($"1 of {harness.ViewModel.ClassFilter.Count} classes", last);
    }

    [Fact]
    public async Task ExpandsFromOneClassRatherThanFromEverythingOnScreen()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        harness.ViewModel.Select("SampleRichModel.Comment");
        harness.ViewModel.FocusSelectedCommand.Execute(null);

        foreach (var item in harness.ViewModel.ClassFilter.Where(i => i.Name == "SampleRichModel.Blog"))
        {
            item.IsShown = true;
        }

        // A step from the comment only. The blog is on screen too, but this is not its step, so the
        // person who owns it stays out.
        harness.ViewModel.Select("SampleRichModel.Comment");
        harness.ViewModel.ExpandFromSelectedCommand.Execute(null);

        Assert.Contains("SampleRichModel.Post", harness.ViewModel.Scene!.Nodes.Keys);
        Assert.DoesNotContain("SampleRichModel.Person", harness.ViewModel.Scene!.Nodes.Keys);

        // The whole-diagram step does take the blog's side as well.
        harness.ViewModel.ExpandOneLevelCommand.Execute(null);

        Assert.Contains("SampleRichModel.Person", harness.ViewModel.Scene!.Nodes.Keys);
    }

    [Fact]
    public async Task OffersExpandingFromAClassOnlyWhileThereIsOneAndAFilterToGrow()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        // Everything is drawn, so a step from the selected class has nowhere to go.
        harness.ViewModel.Select("SampleRichModel.Comment");
        Assert.False(harness.ViewModel.CanExpandFromSelected);

        harness.ViewModel.FocusSelectedCommand.Execute(null);
        Assert.True(harness.ViewModel.CanExpandFromSelected);

        harness.ViewModel.Select(null);
        Assert.False(harness.ViewModel.CanExpandFromSelected);
    }

    [Fact]
    public async Task CountsOnlyTheEntitiesTheDiagramCanActuallyDraw()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        var total = harness.ViewModel.Model!.Entities.Count;
        var drawn = harness.ViewModel.Scene!.Nodes.Count;

        // The snapshot has more entities in it than the diagram has nodes: a collapsed join table and
        // an inlined owned reference. Counting those makes the figure disagree with the diagram.
        Assert.True(drawn < total);
        Assert.StartsWith($"{drawn} entities from", harness.ViewModel.SourceSummary);

        harness.ViewModel.CollapseJoinEntities = false;
        harness.ViewModel.InlineOwnedTypes = false;

        Assert.StartsWith($"{total} entities from", harness.ViewModel.SourceSummary);
    }

    [Fact]
    public async Task BringsBackAFoldedEntityWhenItsOptionIsTurnedOffMidFilter()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        // Untick one listed class while the join table is folded away and unlistable.
        harness.ViewModel.ClassFilter.Single(i => i.Name == "SampleRichModel.Comment").IsShown = false;

        harness.ViewModel.CollapseJoinEntities = false;

        // The join table was never unticked, so showing it again draws it rather than leaving it out
        // of a filter it was never offered to.
        Assert.Contains("PostTag", harness.ViewModel.Scene!.Nodes.Keys);
        Assert.DoesNotContain("SampleRichModel.Comment", harness.ViewModel.Scene!.Nodes.Keys);
    }

    [Fact]
    public async Task RemembersTheFilterForTheDiagramButNotForTheNextWorkspace()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        harness.ViewModel.Select("SampleRichModel.Tag");
        harness.ViewModel.FocusSelectedCommand.Execute(null);

        // Reopened on the same context, the saved diagram comes back filtered.
        var reopened = Build();
        Assert.True(reopened.ViewModel.IsFiltered);
        Assert.Equal(["SampleRichModel.Tag"], reopened.ViewModel.Scene!.Nodes.Keys);


        // The workspace-wide view options carry no entity names, so they cannot hide a different
        // context's model wholesale.
        var workspace = new WorkspaceSettings();
        harness.ViewModel.Store(workspace);
        Assert.Null(workspace.DiagramOptions!.VisibleEntities);
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

    // ---- Export ----

    [Theory]
    [InlineData("Json", "json")]
    [InlineData("Svg", "svg")]
    [InlineData("Png", "png")]
    [InlineData("Pdf", "pdf")]
    [InlineData("Mermaid", "mmd")]
    public async Task ExportsEachFormatToTheChosenFile(string format, string extension)
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        string? suggested = null;
        var path = Path.Combine(_root, "export." + extension);
        harness.ViewModel.PickSaveFileAsync = (name, _) =>
        {
            suggested = name;
            return Task.FromResult<string?>(path);
        };

        await harness.ViewModel.ExportCommand.ExecuteAsync(format);

        Assert.Equal($"RichContext-tables.{extension}", suggested);
        Assert.True(new FileInfo(path).Length > 0);
    }

    [Fact]
    public async Task ExportWritesNothingWhenTheDialogIsCancelled()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);
        harness.ViewModel.PickSaveFileAsync = (_, _) => Task.FromResult<string?>(null);

        await harness.ViewModel.ExportCommand.ExecuteAsync("Svg");

        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task RemembersTheFolderTheLastExportWentTo()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);
        harness.ViewModel.PickSaveFileAsync =
            (_, _) => Task.FromResult<string?>(Path.Combine(_root, "first.svg"));

        await harness.ViewModel.ExportCommand.ExecuteAsync("Svg");
        harness.ViewModel.Store(harness.Workspace);

        Assert.Equal(_root, harness.Workspace.DiagramSaveFolder);

        // And it comes back as the dialog's starting folder next session.
        string? offered = null;
        var next = Build();
        next.ViewModel.Restore(harness.Workspace, _root, Workspace);
        next.ViewModel.PickSaveFileAsync = (_, start) =>
        {
            offered = start;
            return Task.FromResult<string?>(null);
        };

        await next.ViewModel.ExportCommand.ExecuteAsync("Svg");

        Assert.Equal(_root, offered);
    }

    [Fact]
    public async Task ExportIsUnavailableUntilThereIsADiagram()
    {
        var harness = Build("AuditContext");

        Assert.False(harness.ViewModel.ExportCommand.CanExecute("Svg"));
        Assert.False(harness.ViewModel.CopyMermaidCommand.CanExecute(null));

        var withDiagram = Build();
        await withDiagram.ViewModel.GenerateCommand.ExecuteAsync(null);

        Assert.True(withDiagram.ViewModel.ExportCommand.CanExecute("Svg"));
    }

    [Fact]
    public async Task TheSuggestedNameFollowsTheViewOnScreen()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);
        harness.ViewModel.SwitchViewCommand.Execute(null);

        string? suggested = null;
        harness.ViewModel.PickSaveFileAsync = (name, _) =>
        {
            suggested = name;
            return Task.FromResult<string?>(null);
        };

        await harness.ViewModel.ExportCommand.ExecuteAsync("Svg");

        Assert.Equal("RichContext-classes.svg", suggested);
    }

    [Fact]
    public async Task CopiesMermaidForTheCurrentView()
    {
        var harness = Build();
        await harness.ViewModel.GenerateCommand.ExecuteAsync(null);

        string? copied = null;
        harness.Session.CopyToClipboardAsync = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };

        await harness.ViewModel.CopyMermaidCommand.ExecuteAsync(null);

        Assert.StartsWith("erDiagram", copied);
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

    [Fact]
    public void Rebuilding_the_snapshot_list_never_draws_anything()
    {
        var harness = Build(migrations: [Initial, AddPosts]);

        // What the shell does as soon as a workspace's migrations have loaded.
        harness.ViewModel.RefreshSnapshotOptions();

        // A ComboBox bound to SelectedItem writes null back while its list is being rebuilt. That
        // used to arrive as a snapshot change and start a generation on workspace open. IsRunning is
        // the synchronous signal: the generation itself finishes on another turn, so an empty run
        // list on its own would pass whether or not one had been started.
        harness.ViewModel.SelectedSnapshot = "";
        Assert.False(harness.Session.IsRunning);

        harness.ViewModel.RefreshSnapshotOptions();
        Assert.False(harness.Session.IsRunning);

        Assert.Empty(harness.Session.Runs);
        Assert.False(harness.ViewModel.HasDiagram);
        Assert.Equal(DiagramsViewModel.CurrentModel, harness.ViewModel.SelectedSnapshot);
    }

    [Fact]
    public void The_legend_corner_maps_to_alignments_and_is_remembered_app_wide()
    {
        var display = new DisplaySettings();
        var harness = Build(display: display);

        // Where the legend has always been.
        Assert.Equal(SurfaceCorner.TopLeft, harness.ViewModel.LegendCorner);
        Assert.Equal(HorizontalAlignment.Left, harness.ViewModel.LegendHorizontalAlignment);
        Assert.Equal(VerticalAlignment.Top, harness.ViewModel.LegendVerticalAlignment);
        Assert.True(harness.ViewModel.LegendIsTopLeft);

        harness.ViewModel.SetLegendCornerCommand.Execute("BottomRight");

        Assert.Equal(HorizontalAlignment.Right, harness.ViewModel.LegendHorizontalAlignment);
        Assert.Equal(VerticalAlignment.Bottom, harness.ViewModel.LegendVerticalAlignment);
        Assert.False(harness.ViewModel.LegendIsTopLeft);
        Assert.True(harness.ViewModel.LegendIsBottomRight);

        // App-wide: where a reader likes the key is not a fact about one solution.
        Assert.Equal(SurfaceCorner.BottomRight, display.DiagramLegendCorner);
        Assert.True(harness.Persisted > 0);

        // A menu parameter that is not a corner is ignored rather than throwing at a click.
        harness.ViewModel.SetLegendCornerCommand.Execute("Middle");
        Assert.Equal(SurfaceCorner.BottomRight, harness.ViewModel.LegendCorner);

        // And the choice comes back on the next workspace.
        var next = Build(display: display);
        Assert.Equal(SurfaceCorner.BottomRight, next.ViewModel.LegendCorner);
    }
}
