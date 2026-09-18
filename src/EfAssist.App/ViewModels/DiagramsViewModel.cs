using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EfAssist.Core;
using EfAssist.Core.Diagrams;

namespace EfAssist.App.ViewModels;

/// <summary>One row in the selected entity's detail pane.</summary>
/// <param name="Target">
/// An entity to jump to when the row is clicked, for the relationship rows. Null on rows that are
/// not a link.
/// </param>
public sealed record DetailRow(
    string Name, string? Value = null, string? Note = null, string? Target = null)
{
    public bool IsLink => Target is not null;
}

/// <summary>The file formats the diagram can be exported as.</summary>
public enum DiagramFormat
{
    /// <summary>The extracted model. The useful artefact for another tool to read.</summary>
    Json,

    Svg,
    Png,
    Pdf,

    /// <summary>Mermaid source, for pasting into a document that renders it.</summary>
    Mermaid,
}

/// <summary>A group of <see cref="DetailRow"/>s under a heading.</summary>
public sealed record DetailGroup(string Title, IReadOnlyList<DetailRow> Rows)
{
    public bool HasRows => Rows.Count > 0;
}

/// <summary>One entity in the class filter: its tick, and whether the list's search box leaves it
/// on screen.</summary>
/// <param name="label">
/// What the row reads as. The short name, except for an owned type, where it is the short name
/// followed by its owner: two entities can own the same type, and "Address" twice over says nothing
/// about which is which.
/// </param>
public sealed partial class ClassFilterItem(string name, string shortName, string label)
    : ObservableObject
{
    /// <summary>The full <see cref="DiagramEntity.Name"/>, which is the identity everything else uses.</summary>
    public string Name { get; } = name;

    public string ShortName { get; } = shortName;

    public string Label { get; } = label;

    [ObservableProperty]
    private bool _isShown = true;

    [ObservableProperty]
    private bool _matchesSearch = true;
}

/// <summary>
/// The Diagrams tab: extract the model for the selected context from its EF snapshot, lay it out and
/// draw it.
/// </summary>
/// <remarks>
/// <para>
/// Generation is a file read and a parse, not a <c>dotnet ef</c> invocation — see
/// <c>docs/DIAGRAMS-PLAN.md</c> §2 for why the model snapshot is the source. It still goes through
/// <see cref="CommandSession.RunLocalAsync"/> so there is one busy state and one Cancel button.
/// </para>
/// <para>
/// A saved diagram loads automatically when the tab is opened, because reading a file is free.
/// Generating a new one always needs the button, matching the <see cref="DiscoveryMode.Cached"/>
/// philosophy the rest of the app already follows.
/// </para>
/// </remarks>
public partial class DiagramsViewModel : ObservableObject
{
    private readonly CommandSession _session;
    private readonly Func<EfTarget?> _target;
    private readonly Func<string?> _contextName;

    /// <summary>
    /// The migrations list as the Migrations tab last loaded it, in the order they are applied. Read
    /// rather than fetched: the picker offers what is already known, exactly like the Script tab's
    /// range pickers do.
    /// </summary>
    private readonly Func<IReadOnlyList<MigrationInfo>> _migrations;
    private readonly Action _persist;
    private readonly DisplaySettings _display;

    /// <summary>Where saved diagrams live, and which workspace they belong to. Set on open.</summary>
    private string? _settingsRoot;
    private string? _workspacePath;

    /// <summary>
    /// The parsed model and everything the user has done to it. Held rather than rebuilt so that
    /// switching views, toggling an option or dragging a node never re-reads the snapshot.
    /// </summary>
    private SavedDiagram _saved = new();

    private DiagramNodeContent.Content _content = new([], []);
    private DiagramLayout _layout = DiagramLayout.Empty;

    /// <summary>
    /// The merged model and the changes in it, when a migration is being compared with the one
    /// before it. Null means there is nothing to compare and the model is drawn as it is.
    /// </summary>
    private DiagramComparison? _comparison;

    /// <summary>Suppresses persistence and re-rendering while state is restored in bulk.</summary>
    private bool _restoring;

    /// <summary>Where the last export went, so the next Save As dialog opens somewhere useful.</summary>
    private string? _lastSaveAsFolder;

    /// <summary>The entities to draw, or null — the normal case — for all of them.</summary>
    private HashSet<string>? _visibleEntities;

    /// <summary>What <see cref="ClassFilter"/> was built from, so it is rebuilt only when the model
    /// or one of the options that fold entities away changes, rather than on every re-render.</summary>
    private (DiagramModel? Model, bool Inline, bool Collapse, DiagramKind Kind) _classFilterSource;

    /// <summary>
    /// Entities the options fold away, and so are not in <see cref="ClassFilter"/> to be ticked. Kept
    /// because they still belong in the filter set: turning the option off has to bring them back
    /// rather than find them silently unticked.
    /// </summary>
    private IReadOnlyCollection<string> _unlistedEntities = [];

    /// <summary>Suppresses the tick handler while the ticks are being set from the filter.</summary>
    private bool _syncingTicks;

    public DiagramsViewModel(
        CommandSession session,
        Func<EfTarget?> target,
        Func<string?> contextName,
        Func<IReadOnlyList<MigrationInfo>> migrations,
        Action persist,
        DisplaySettings display)
    {
        _session = session;
        _target = target;
        _contextName = contextName;
        _migrations = migrations;
        _persist = persist;
        _display = display;

        _detailVisible = display.DiagramDetailVisible;
        _legendCorner = display.DiagramLegendCorner;
        _kind = display.DefaultDiagramKind;

        _session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CommandSession.IsRunning))
            {
                NotifyCommandStates();
            }
        };
    }

    // ---- Supplied by the view ----

    /// <summary>
    /// Measures text in the fonts the surface draws with. Null until the view supplies one, in which
    /// case layout falls back to Core's character-count approximation.
    /// </summary>
    public Func<string, double, double>? MeasureText { get; set; }

    /// <summary>Centres the surface on an entity. Supplied by the view, which owns the transform.</summary>
    public Action<string>? CentreOn { get; set; }

    public Action? FitToWindow { get; set; }

    public Func<ConfirmRequest, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>Save As dialog: suggested file name and starting folder in, chosen path out.</summary>
    public Func<string, string?, Task<string?>>? PickSaveFileAsync { get; set; }

    // ---- The diagram ----

    [ObservableProperty]
    private DiagramScene? _scene;

    public bool HasDiagram => Scene is { IsEmpty: false };

    public DiagramModel? Model => _saved.Model;

    /// <summary>
    /// The model actually drawn: the merged one while a diff is on screen, so a removed entity still
    /// has a node and a removed column still has a row.
    /// </summary>
    private DiagramModel? Rendered => _comparison?.Model ?? _saved.Model;

    // ---- Which snapshot ----

    /// <summary>The picker entry for the context's own <c>ModelSnapshot.cs</c> rather than a migration.</summary>
    public const string CurrentModel = "Current model";

    /// <summary>
    /// <see cref="CurrentModel"/> followed by every migration, newest first. Labelled
    /// <c>&lt;position&gt;. &lt;name&gt;</c> rather than by id, because the id is a timestamp and the
    /// name is what the user chose, and the position is the same 1-based chronological number the
    /// Migrations list shows.
    /// </summary>
    public ObservableCollection<string> SnapshotOptions { get; } = [CurrentModel];

    [ObservableProperty]
    private string _selectedSnapshot = CurrentModel;

    /// <summary>
    /// Mark up what the selected migration added, removed and changed, against the migration before
    /// it. Meaningless for <see cref="CurrentModel"/>, which is not a point in the history.
    /// </summary>
    [ObservableProperty]
    private bool _highlightChanges = true;

    public bool IsMigrationSelected => SelectedSnapshot != CurrentModel;

    public bool HasMigrations => _migrations().Count > 0;

    /// <summary>
    /// The diff legend, or null when nothing is being compared. Says so explicitly when a migration
    /// turns out to change nothing in the model — a data-only migration, or one that only touches
    /// indexes — because an empty legend would read as a failure to look.
    /// </summary>
    public string? DiffSummary
    {
        get
        {
            if (_comparison is null || _saved.MigrationId is null)
            {
                return null;
            }

            var name = NameOf(_saved.MigrationId);
            return _comparison.Diff.Summary is { Length: > 0 } summary
                ? $"{name}: {summary}"
                : $"{name} makes no change to the model.";
        }
    }

    public bool ShowsDiff => DiffSummary is not null;

    /// <summary>
    /// The two halves of <see cref="DiffSummary"/>, for the legend on the surface: which migration is
    /// being compared, and what it changed. One line each — the sentence is read once and the change
    /// list is read repeatedly, and they were sharing a row.
    /// </summary>
    public string? DiffMigration => _comparison is null || _saved.MigrationId is null
        ? null
        : NameOf(_saved.MigrationId);

    /// <summary>
    /// Which corner the legend sits in. Moved by right-clicking it, and kept app-wide — see
    /// <see cref="DisplaySettings.DiagramLegendCorner"/>. The zoom cluster keeps the bottom-right
    /// corner it has always had; putting the legend there too is allowed, because it is the reader's
    /// diagram and there are four corners and two overlays.
    /// </summary>
    [ObservableProperty]
    private SurfaceCorner _legendCorner;

    partial void OnLegendCornerChanged(SurfaceCorner value)
    {
        OnPropertyChanged(nameof(LegendHorizontalAlignment));
        OnPropertyChanged(nameof(LegendVerticalAlignment));
        OnPropertyChanged(nameof(LegendIsTopLeft));
        OnPropertyChanged(nameof(LegendIsTopRight));
        OnPropertyChanged(nameof(LegendIsBottomLeft));
        OnPropertyChanged(nameof(LegendIsBottomRight));

        if (_restoring)
        {
            return;
        }

        _display.DiagramLegendCorner = value;
        _persist();
    }

    /// <summary>The corner as the two alignments the overlay actually needs.</summary>
    public HorizontalAlignment LegendHorizontalAlignment =>
        LegendCorner is SurfaceCorner.TopRight or SurfaceCorner.BottomRight
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;

    public VerticalAlignment LegendVerticalAlignment =>
        LegendCorner is SurfaceCorner.BottomLeft or SurfaceCorner.BottomRight
            ? VerticalAlignment.Bottom
            : VerticalAlignment.Top;

    // One per corner, so the context menu can show which one is current.
    public bool LegendIsTopLeft => LegendCorner == SurfaceCorner.TopLeft;

    public bool LegendIsTopRight => LegendCorner == SurfaceCorner.TopRight;

    public bool LegendIsBottomLeft => LegendCorner == SurfaceCorner.BottomLeft;

    public bool LegendIsBottomRight => LegendCorner == SurfaceCorner.BottomRight;

    /// <summary>
    /// Moves the legend. The parameter arrives as a string because that is what a MenuItem's
    /// CommandParameter is; anything unparseable is ignored rather than throwing at a click.
    /// </summary>
    [RelayCommand]
    private void SetLegendCorner(string? corner)
    {
        if (Enum.TryParse<SurfaceCorner>(corner, out var value))
        {
            LegendCorner = value;
        }
    }

    public string? DiffChanges
    {
        get
        {
            if (_comparison is null || _saved.MigrationId is null)
            {
                return null;
            }

            return _comparison.Diff.Summary is { Length: > 0 } summary
                ? summary
                : "Makes no change to the model.";
        }
    }

    // ---- View selection ----

    public IReadOnlyList<DiagramKind> Kinds { get; } = Enum.GetValues<DiagramKind>();

    [ObservableProperty]
    private DiagramKind _kind;

    public string KindLabel => Kind == DiagramKind.EntityRelationship
        ? "Entity relationships"
        : "Classes";

    /// <summary>
    /// The two halves of the view switch. Both are bound, so the segments read as one choice rather
    /// than as a button that relabels itself; setting one is what selects it, and the false the other
    /// segment writes on its way out is ignored.
    /// </summary>
    public bool IsTableView
    {
        get => Kind == DiagramKind.EntityRelationship;
        set
        {
            if (value)
            {
                Kind = DiagramKind.EntityRelationship;
            }
        }
    }

    public bool IsClassView
    {
        get => Kind == DiagramKind.Class;
        set
        {
            if (value)
            {
                Kind = DiagramKind.Class;
            }
        }
    }

    // ---- Rank direction ----

    [ObservableProperty]
    private DiagramFlow _flow;

    /// <summary>What pressing the button does, in the same voice as the view and lock buttons.</summary>
    public string FlowLabel => Flow == DiagramFlow.LeftToRight
        ? "Top to bottom"
        : "Left to right";

    // ---- View options ----

    public IReadOnlyList<PropertyDetail> PropertyDetails { get; } = Enum.GetValues<PropertyDetail>();

    [ObservableProperty]
    private PropertyDetail _properties = PropertyDetail.All;

    [ObservableProperty]
    private bool _showTypes = true;

    [ObservableProperty]
    private bool _showNullability = true;

    [ObservableProperty]
    private bool _showIndexes;

    [ObservableProperty]
    private bool _showNavigations = true;

    [ObservableProperty]
    private bool _collapseJoinEntities = true;

    [ObservableProperty]
    private bool _inlineOwnedTypes = true;

    [ObservableProperty]
    private bool _showDeleteBehavior;

    [ObservableProperty]
    private bool _showInheritance = true;

    /// <summary>Navigations are a class-view idea; in the entity view there is nothing to toggle.</summary>
    public bool CanShowNavigations => Kind == DiagramKind.Class;

    // ---- Lock ----

    [ObservableProperty]
    private bool _isUnlocked;

    public string LockLabel => IsUnlocked ? "Lock layout" : "Unlock layout";

    public string LockTooltip => IsUnlocked
        ? "Locked: the diagram pans and zooms, but nodes cannot be moved."
        : "Unlocked: drag nodes to arrange them. Their positions are remembered.";

    // ---- Class filter ----

    /// <summary>
    /// Every entity in the model with a tick. One list whose rows the filter's own search box hides,
    /// rather than a second filtered collection, so a tick survives typing in that box.
    /// </summary>
    public ObservableCollection<ClassFilterItem> ClassFilter { get; } = [];

    /// <summary>Narrows the filter list itself. Nothing to do with what is drawn.</summary>
    [ObservableProperty]
    private string _classFilterSearch = "";

    public bool IsFiltered => _visibleEntities is not null;

    /// <summary>
    /// Expanding from one class needs both a class and something to expand into: with no filter on,
    /// everything is drawn already and the step has nowhere to go.
    /// </summary>
    public bool CanExpandFromSelected => IsFiltered && HasSelection;

    /// <summary>
    /// Says a narrowed diagram is narrowed. A filtered diagram looks exactly like a small model, and
    /// mistaking one for the other is the whole risk of this feature.
    /// </summary>
    public string? FilterSummary => _visibleEntities is null
        ? null
        : $"{ClassFilter.Count(i => i.IsShown)} of {ClassFilter.Count} classes";

    // ---- Search ----

    [ObservableProperty]
    private string _search = "";

    /// <summary>Entities matching <see cref="Search"/>, in the order they are laid out.</summary>
    private readonly List<string> _matches = [];

    private int _matchIndex = -1;

    public bool IsSearching => Search.Trim().Length > 0;

    /// <summary>
    /// The match counter. Reads as a total until Next has been pressed — before that there is no
    /// current match, and "0 of 9" describes a position that does not exist.
    /// </summary>
    public string SearchSummary
    {
        get
        {
            if (!IsSearching)
            {
                return "";
            }

            if (_matches.Count == 0)
            {
                return "No matches";
            }

            return _matchIndex < 0
                ? $"{_matches.Count} match{(_matches.Count == 1 ? "" : "es")}"
                : $"{_matchIndex + 1} of {_matches.Count}";
        }
    }

    public bool HasMatches => _matches.Count > 0;

    // ---- Selection ----

    [ObservableProperty]
    private string? _selectedEntity;

    public ObservableCollection<DetailGroup> Detail { get; } = [];

    public bool HasSelection => SelectedEntity is not null;

    /// <summary>
    /// The selected entity as the pane heads it: the short name, matching what the node is titled.
    /// The full name is in the Entity group underneath, where there is room for it.
    /// </summary>
    public string? SelectedEntityTitle => SelectedEntity is null ? null : Short(SelectedEntity);

    /// <summary>Whether the detail pane is open. App-wide, like the migration actions expander.</summary>
    [ObservableProperty]
    private bool _detailVisible = true;

    /// <summary>What the pane shows with nothing selected, so it is never blank for no reason.</summary>
    public string DetailPlaceholder => HasDiagram
        ? "Select an entity to see its columns, keys, indexes and relationships."
        : "Generate a diagram to inspect its entities.";

    // ---- State messages ----

    /// <summary>Why there is nothing to draw. Null when there is.</summary>
    [ObservableProperty]
    private string? _emptyReason;

    /// <summary>
    /// True when the snapshot has changed since this diagram was built. The diagram stays on screen —
    /// a stale one is still readable, it just stops looking current, the same rule
    /// <see cref="ScriptViewModel.IsStale"/> follows.
    /// </summary>
    [ObservableProperty]
    private bool _isStale;

    /// <summary>
    /// The second, weaker staleness signal: the code has changed since the last migration, so even a
    /// current snapshot is behind the model. Costs a build, so it only runs on request.
    /// </summary>
    [ObservableProperty]
    private ModelCheckState _modelCheckState = ModelCheckState.Unknown;

    public bool ShowsPendingChangesWarning => ModelCheckState == ModelCheckState.Pending;

    /// <summary>
    /// What was read, and how much of it there is to draw. The count is of entities the diagram can
    /// actually show: a collapsed join table and an inlined owned type are in the snapshot but never
    /// on screen, so counting them makes the figure disagree with the diagram and with the filter.
    /// </summary>
    public string? SourceSummary => _saved.Model is { } model && model.Entities.Count > 0
        ? $"{model.Entities.Count - _unlistedEntities.Count} entities from {Path.GetFileName(model.SourcePath)}"
          + (model.EfVersion is null ? "" : $" (EF {model.EfVersion})")
        : null;

    public bool IsReady => !_session.IsRunning && _target() is not null;

    // ---- Lifecycle ----

    /// <summary>
    /// Called when the tab becomes visible. Loads a saved diagram if there is one; never generates.
    /// </summary>
    public Task OnActivatedAsync()
    {
        RefreshSnapshotOptions();

        if (Scene is null)
        {
            LoadSaved();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Rebuilds the snapshot picker from the migrations list, keeping the current selection when it
    /// is still there. Called on activation and whenever the context changes, the same way the Script
    /// tab refreshes its range pickers.
    /// </summary>
    public void RefreshSnapshotOptions()
    {
        var selected = string.IsNullOrWhiteSpace(SelectedSnapshot) ? CurrentModel : SelectedSnapshot;

        var migrations = _migrations();

        // The whole rebuild is guarded, not just the final assignment. Clearing the list under a
        // ComboBox bound to SelectedItem makes it write null back, and that arrives here as a
        // snapshot change like any other — which used to start a generation on workspace open,
        // because this runs as soon as the migrations list has loaded.
        SetWithoutRegenerating(() =>
        {
            SnapshotOptions.Clear();
            SnapshotOptions.Add(CurrentModel);
            for (var i = migrations.Count - 1; i >= 0; i--)
            {
                SnapshotOptions.Add(LabelFor(migrations[i].Name, i + 1));
            }

            // A saved diagram of a migration the list does not have — because it has not been loaded
            // yet, or because the migration has since been removed — keeps its entry rather than
            // silently becoming a diagram of something else. Unnumbered: there is no known position
            // for it.
            if (selected != CurrentModel && !SnapshotOptions.Contains(selected))
            {
                SnapshotOptions.Insert(1, selected);
            }

            SelectedSnapshot = selected;
        });

        OnPropertyChanged(nameof(HasMigrations));
    }

    /// <summary>Called when a workspace opens, so saved diagrams can be found.</summary>
    public void Restore(WorkspaceSettings saved, string? settingsRoot, string workspacePath)
    {
        _restoring = true;
        try
        {
            _settingsRoot = settingsRoot;
            _workspacePath = workspacePath;

            Kind = saved.DiagramView ?? _display.DefaultDiagramKind;
            Flow = saved.DiagramLayoutFlow;
            SelectedSnapshot = CurrentModel;
            HighlightChanges = true;
            IsUnlocked = !saved.DiagramLocked;
            _lastSaveAsFolder = saved.DiagramSaveFolder;
            ApplyOptions(saved.DiagramOptions ?? new DiagramViewOptions());

            ClearDiagram();
        }
        finally
        {
            _restoring = false;
        }

        LoadSaved();
    }

    public void Store(WorkspaceSettings saved)
    {
        saved.DiagramView = Kind;
        saved.DiagramLayoutFlow = Flow;
        saved.DiagramLocked = !IsUnlocked;
        saved.DiagramOptions = CurrentOptions() with { VisibleEntities = null };
        saved.DiagramSaveFolder = _lastSaveAsFolder;
    }

    public void Clear()
    {
        _settingsRoot = null;
        _workspacePath = null;
        ClearDiagram();
    }

    /// <summary>Called by the shell when the project or context selection changes.</summary>
    public void NotifyTargetChanged()
    {
        ModelCheckState = ModelCheckState.Unknown;
        NotifyCommandStates();
    }

    /// <summary>
    /// Called by the shell when the selected context changes. Each context has its own saved diagram,
    /// so the one on screen is replaced rather than left describing something else.
    /// </summary>
    public void NotifyContextChanged()
    {
        ClearDiagram();
        RefreshSnapshotOptions();
        LoadSaved();
        NotifyTargetChanged();
    }

    // ---- Commands ----

    [RelayCommand(CanExecute = nameof(IsReady))]
    private async Task GenerateAsync()
    {
        var target = _target();
        if (target is null)
        {
            return;
        }

        var context = _contextName();
        var project = target.Project;

        var migrationId = MigrationIdFor(SelectedSnapshot);
        var previousId = migrationId is null || !HighlightChanges
            ? null
            : PreviousMigrationId(migrationId);

        var extracted = await _session.RunLocalAsync("Generating diagram", async token =>
        {
            // Off the UI thread: a large snapshot is still only milliseconds, but the read is I/O and
            // there is no reason to do it where it can stutter the window.
            return await Task.Run(() =>
            {
                var path = migrationId is null
                    ? ModelSnapshotLocator.Find(project, context, token)
                    : ModelSnapshotLocator.FindForMigration(project, migrationId);

                if (path is null)
                {
                    return null;
                }

                token.ThrowIfCancellationRequested();
                var model = ModelSnapshotParser.Parse(
                    File.ReadAllText(path), path, context, token);

                // The earlier migration's snapshot, for the diff. A missing one is not a failure:
                // the first migration has no predecessor, and Compare treats null as "everything
                // here is new", which for the first migration is the truth.
                DiagramModel? previous = null;
                if (previousId is not null
                    && ModelSnapshotLocator.FindForMigration(project, previousId) is { } earlier)
                {
                    token.ThrowIfCancellationRequested();
                    previous = ModelSnapshotParser.Parse(
                        File.ReadAllText(earlier), earlier, context, token);
                }

                return new Extracted(model, previous);
            }, token);
        });

        if (extracted is null)
        {
            // Cancelled, already running, or no snapshot. Only the last of those needs explaining,
            // and only when nothing else has already put a message up.
            if (!_session.IsRunning)
            {
                ReportMissingSnapshot(project, context, migrationId);
            }

            return;
        }

        _saved.Model = extracted.Model;
        _saved.Previous = extracted.Previous;
        _saved.MigrationId = migrationId;
        _saved.HighlightChanges = HighlightChanges;
        _saved.Kind = Kind;
        _saved.Flow = Flow;
        ApplyComparison();
        IsStale = false;
        EmptyReason = null;

        // Positions match by entity name, so an entity that survived a regeneration keeps where it
        // was put. Anything new is placed by the layout and anything gone is dropped.
        Rebuild();
        SaveDiagram();

        // Only here and on load, never on a re-render. Fitting after every option toggle or drag
        // would fight whatever zoom the user had chosen.
        FitToWindow?.Invoke();

        _session.StatusMessage = SourceSummary is null
            ? "The snapshot contains no entities."
            : $"Diagram generated: {SourceSummary}."
              + (DiffSummary is null ? "" : $" {DiffSummary}.");

        OnPropertyChanged(nameof(SourceSummary));
    }

    /// <summary>The model read out of one snapshot, with the earlier one to compare it against.</summary>
    private sealed record Extracted(DiagramModel Model, DiagramModel? Previous);

    /// <summary>
    /// Why there was nothing to read. Worded per case: a context with no migrations has no snapshot
    /// at all, while a migration with no <c>.Designer.cs</c> is a specific file that is missing.
    /// </summary>
    private void ReportMissingSnapshot(string project, string? context, string? migrationId)
    {
        if (migrationId is not null)
        {
            if (ModelSnapshotLocator.FindForMigration(project, migrationId) is not null)
            {
                return;
            }

            EmptyReason =
                $"{NameOf(migrationId)} has no .Designer.cs file beside it, so there is no model "
                + "snapshot for that migration. Choose the current model instead.";
            _session.StatusMessage = $"No model snapshot found for {NameOf(migrationId)}.";
            return;
        }

        if (ModelSnapshotLocator.Find(project, context) is not null)
        {
            return;
        }

        EmptyReason =
            $"{context ?? "This context"} has no migrations, so there is no model snapshot "
            + "to draw. Add a migration on the Migrations tab first.";
        _session.StatusMessage = "No model snapshot found for this context.";
    }

    /// <summary>How a migration is shown in the picker: its chronological position, then its name.</summary>
    private static string LabelFor(string name, int position) => $"{position}. {name}";

    /// <summary>
    /// The id of the migration a picker entry names, or null for the current model. Accepts the
    /// unnumbered name too, so an entry kept from a saved diagram still resolves once the migrations
    /// list catches up.
    /// </summary>
    private string? MigrationIdFor(string selected)
    {
        if (selected == CurrentModel)
        {
            return null;
        }

        var migrations = _migrations();
        for (var i = 0; i < migrations.Count; i++)
        {
            if (selected == LabelFor(migrations[i].Name, i + 1) || selected == migrations[i].Name)
            {
                return migrations[i].Id;
            }
        }

        return null;
    }

    /// <summary>
    /// The picker entry for a migration id: numbered when the migrations list has it, the bare name
    /// when it does not.
    /// </summary>
    private string SnapshotOptionFor(string migrationId)
    {
        var migrations = _migrations();
        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Id == migrationId)
            {
                return LabelFor(migrations[i].Name, i + 1);
            }
        }

        return NameOf(migrationId);
    }

    /// <summary>
    /// The migration applied immediately before this one, or null when it is the first. Position in
    /// the list, not the timestamp in the id: the list is already in the order EF applies them.
    /// </summary>
    private string? PreviousMigrationId(string migrationId)
    {
        var migrations = _migrations();
        var index = -1;
        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Id == migrationId)
            {
                index = i;
                break;
            }
        }

        return index > 0 ? migrations[index - 1].Id : null;
    }

    /// <summary>
    /// A migration's name from its id. EF ids are <c>&lt;timestamp&gt;_&lt;name&gt;</c>, so this is
    /// what lets a restored diagram label itself without the migrations list having been loaded.
    /// </summary>
    private static string NameOf(string migrationId)
    {
        var underscore = migrationId.IndexOf('_');
        return underscore < 0 ? migrationId : migrationId[(underscore + 1)..];
    }

    /// <summary>
    /// Recomputes the comparison from what is saved. Pure — no file access — so toggling the
    /// highlight off and on again costs nothing.
    /// </summary>
    private void ApplyComparison()
    {
        _comparison = _saved.Model is not null && _saved.MigrationId is not null
            && _saved.HighlightChanges
            ? DiagramDiff.Compare(_saved.Previous, _saved.Model)
            : null;

        OnPropertyChanged(nameof(DiffSummary));
        OnPropertyChanged(nameof(DiffMigration));
        OnPropertyChanged(nameof(DiffChanges));
        OnPropertyChanged(nameof(ShowsDiff));
    }

    /// <summary>
    /// Sets a picker property without the change handler treating it as the user choosing something,
    /// which would generate a diagram nobody asked for.
    /// </summary>
    private void SetWithoutRegenerating(Action set)
    {
        var wasRestoring = _restoring;
        _restoring = true;
        try
        {
            set();
        }
        finally
        {
            _restoring = wasRestoring;
        }
    }

    [RelayCommand]
    private void SwitchView() =>
        Kind = Kind == DiagramKind.EntityRelationship
            ? DiagramKind.Class
            : DiagramKind.EntityRelationship;

    /// <summary>
    /// Turns the layout through ninety degrees. Not confirmed and not destructive: each orientation
    /// keeps its own dragged positions, so switching back finds the arrangement it left behind.
    /// </summary>
    [RelayCommand]
    private void SwitchFlow() =>
        Flow = Flow == DiagramFlow.LeftToRight
            ? DiagramFlow.TopToBottom
            : DiagramFlow.LeftToRight;

    [RelayCommand]
    private void ToggleLock() => IsUnlocked = !IsUnlocked;

    /// <summary>
    /// Throws away hand-dragged positions for the current view and lays it out again. Confirmed,
    /// because arranging a diagram is work and this is the one action that destroys it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasDiagram))]
    private async Task ReLayoutAsync()
    {
        if (_saved.PositionsFor(Kind, Flow).Count > 0
            && ConfirmAsync is not null
            && !await ConfirmAsync(new ConfirmRequest(
                "Re-layout diagram",
                $"Arrange the {KindLabel.ToLowerInvariant()} diagram automatically?",
                "Re-layout",
                "Nodes you have moved by hand in this view go back to their computed positions. The "
                + "other view, and the other rank direction, are not affected.")))
        {
            return;
        }

        _saved.Positions.Remove(SavedDiagram.PositionKey(Kind, Flow));
        Rebuild();
        SaveDiagram();
        FitToWindow?.Invoke();
    }

    /// <summary>
    /// Asks EF whether the code has moved on since the last migration. Separate from Generate because
    /// it builds the startup project, which generation deliberately never does.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsReady))]
    private async Task CheckPendingChangesAsync()
    {
        var target = _target();
        if (target is null)
        {
            return;
        }

        ModelCheckState = ModelCheckState.Unknown;

        var result = await _session.RunAsync(
            EfArgs.MigrationsHasPendingModelChanges(target),
            "Checking for pending model changes");

        if (result is null)
        {
            return;
        }

        if (result.Success)
        {
            ModelCheckState = ModelCheckState.UpToDate;
            _session.StatusMessage = "The diagram is up to date with the model.";
        }
        else if (EfDiagnostics.IsPendingModelChanges(result))
        {
            ModelCheckState = ModelCheckState.Pending;
            _session.StatusMessage = "The model has changed since the last migration.";
        }
        else
        {
            _session.ReportFailure(result, "Could not check for pending model changes.");
        }
    }

    [RelayCommand(CanExecute = nameof(HasMatches))]
    private void NextMatch()
    {
        if (_matches.Count == 0)
        {
            return;
        }

        _matchIndex = (_matchIndex + 1) % _matches.Count;
        SelectedEntity = _matches[_matchIndex];
        CentreOn?.Invoke(_matches[_matchIndex]);
        OnPropertyChanged(nameof(SearchSummary));
    }

    [RelayCommand]
    private void ClearSearch() => Search = "";

    [RelayCommand]
    private void ToggleDetail() => DetailVisible = !DetailVisible;

    // ---- Class filter ----

    [RelayCommand]
    private void ShowAllClasses() => ApplyFilter(null, fit: true);

    /// <summary>Starting point for ticking a handful back on.</summary>
    [RelayCommand]
    private void HideAllClasses() => ApplyFilter([], fit: false);

    /// <summary>Draw the selected entity and nothing else. Expand outwards from there.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void FocusSelected()
    {
        if (SelectedEntity is { } entity)
        {
            ApplyFilter([entity], fit: true);
        }
    }

    /// <summary>
    /// Brings in everything one relationship away from what is already drawn, so the whole diagram
    /// grows outwards a ring at a time.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsFiltered))]
    private void ExpandOneLevel() =>
        ExpandFrom(_visibleEntities ?? [], "the classes on screen");

    /// <summary>
    /// The same step from one class only, so a diagram can be grown along the branch being read
    /// rather than on every side at once.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExpandFromSelected))]
    private void ExpandFromSelected()
    {
        if (SelectedEntity is { } entity)
        {
            ExpandFrom([entity], Short(entity));
        }
    }

    /// <summary>
    /// Adds everything one relationship away from <paramref name="seeds"/> - either direction, and
    /// inheritance as well as foreign keys - to what is already drawn.
    /// </summary>
    /// <param name="what">
    /// What found nothing, for the status line. A step that adds nothing has to say so, or it reads
    /// as a dead menu item.
    /// </param>
    private void ExpandFrom(IEnumerable<string> seeds, string what)
    {
        if (_visibleEntities is not { } shown || Rendered is not { } model)
        {
            return;
        }

        var from = seeds.ToHashSet(StringComparer.Ordinal);

        // The seeds themselves, so expanding from a class reached through the detail pane brings that
        // class in rather than only its neighbours.
        var next = new HashSet<string>(shown, StringComparer.Ordinal);
        next.UnionWith(from);

        foreach (var relationship in model.Relationships)
        {
            if (from.Contains(relationship.DependentEntity))
            {
                next.Add(relationship.PrincipalEntity);
            }

            if (from.Contains(relationship.PrincipalEntity))
            {
                next.Add(relationship.DependentEntity);
            }
        }

        foreach (var entity in model.Entities.Where(e => e.BaseType is not null))
        {
            if (from.Contains(entity.Name))
            {
                next.Add(entity.BaseType!);
            }

            if (from.Contains(entity.BaseType!))
            {
                next.Add(entity.Name);
            }
        }

        // A collapsed join entity is not drawn, so arriving at one is not a level. Its far end comes
        // in the same step, or expanding across a many-to-many would appear to do nothing.
        if (CollapseJoinEntities)
        {
            var joins = model.Entities
                .Where(e => e.IsImplicitJoin && next.Contains(e.Name) && !shown.Contains(e.Name))
                .Select(e => e.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var relationship in model.Relationships.Where(r => joins.Contains(r.DependentEntity)))
            {
                next.Add(relationship.PrincipalEntity);
            }
        }

        if (next.Count == shown.Count)
        {
            _session.StatusMessage = $"Nothing further connects to {what}.";
            return;
        }

        ApplyFilter(next, fit: true);
    }

    /// <param name="entities">The entities to draw, or null for all of them.</param>
    /// <param name="fit">
    /// Whether to reframe afterwards. True for the commands, which change the extent wholesale; false
    /// for a single tick, where the view jumping under the pointer would be a nuisance.
    /// </param>
    private void ApplyFilter(IEnumerable<string>? entities, bool fit)
    {
        _visibleEntities = entities is null
            ? null
            : new HashSet<string>(entities, StringComparer.Ordinal);

        SyncTicks();
        NotifyFilterChanged();
        Rebuild();
        Persist();

        if (fit)
        {
            FitToWindow?.Invoke();
        }
    }

    private void NotifyFilterChanged()
    {
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(FilterSummary));
        OnPropertyChanged(nameof(CanExpandFromSelected));
        ExpandOneLevelCommand.NotifyCanExecuteChanged();
        ExpandFromSelectedCommand.NotifyCanExecuteChanged();
    }

    private void SyncTicks()
    {
        _syncingTicks = true;
        try
        {
            foreach (var item in ClassFilter)
            {
                item.IsShown = _visibleEntities?.Contains(item.Name) ?? true;
            }
        }
        finally
        {
            _syncingTicks = false;
        }
    }

    /// <summary>
    /// Rebuilds the tick list when the model behind it changes, and drops filtered names that model
    /// no longer has.
    /// </summary>
    private void RefreshClassFilter(DiagramModel model)
    {
        _visibleEntities?.IntersectWith(model.Entities.Select(e => e.Name));

        var source = (model, InlineOwnedTypes, CollapseJoinEntities, Kind);
        if (_classFilterSource == source)
        {
            return;
        }

        _classFilterSource = source;

        // A collapsed join table and an inlined owned type are not drawn whatever the filter says, so
        // a tick against one would do nothing. They stay out of the list and in the filter set.
        var folded = DiagramNodeContent.FoldedAway(model, CurrentOptions());
        _unlistedEntities = folded;

        foreach (var item in ClassFilter)
        {
            item.PropertyChanged -= OnClassFilterItemChanged;
        }

        ClassFilter.Clear();

        var listed = model.Entities
            .Where(e => !folded.Contains(e.Name))
            .OrderBy(e => e.ShortName, StringComparer.OrdinalIgnoreCase);

        foreach (var entity in listed)
        {
            // Ticked before the handler is attached, so restoring a saved filter is not mistaken for
            // the user clicking every box in turn.
            var item = new ClassFilterItem(entity.Name, entity.ShortName, ClassFilterLabel(entity, model))
            {
                IsShown = _visibleEntities?.Contains(entity.Name) ?? true,
            };

            item.PropertyChanged += OnClassFilterItemChanged;
            ClassFilter.Add(item);
        }

        ApplyClassFilterSearch();

        // Both counts are of this list, and it has just been rebuilt. Without this a filter restored
        // on load reads "0 of 0": the filter is notified about before the first render fills the list
        // in, and nothing raised them again afterwards.
        OnPropertyChanged(nameof(FilterSummary));

        // The folded-away set is what the source summary counts, and an option toggle has just
        // changed it.
        OnPropertyChanged(nameof(SourceSummary));
    }

    /// <summary>
    /// What a row reads as. An owned type is named after the type it owns, which two owners can share,
    /// so it says whose it is.
    /// </summary>
    private static string ClassFilterLabel(DiagramEntity entity, DiagramModel model)
    {
        if (!entity.IsOwned || entity.OwnerName is null)
        {
            return entity.ShortName;
        }

        var owner = model.Entity(entity.OwnerName)?.ShortName ?? Short(entity.OwnerName);
        return $"{entity.ShortName} (owned by {owner})";
    }

    private void OnClassFilterItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingTicks || e.PropertyName != nameof(ClassFilterItem.IsShown))
        {
            return;
        }

        // Everything ticked is not a filter but the whole model, and saying so keeps the summary and
        // the Show all button honest.
        var shown = ClassFilter.Where(i => i.IsShown).Select(i => i.Name).ToList();

        ApplyFilter(
            shown.Count == ClassFilter.Count ? null : [.. shown, .. _unlistedEntities],
            fit: false);
    }

    private void ApplyClassFilterSearch()
    {
        var term = ClassFilterSearch.Trim();

        foreach (var item in ClassFilter)
        {
            item.MatchesSearch = term.Length == 0
                || item.Label.Contains(term, StringComparison.OrdinalIgnoreCase)
                || item.Name.Contains(term, StringComparison.OrdinalIgnoreCase);
        }
    }

    partial void OnClassFilterSearchChanged(string value) => ApplyClassFilterSearch();

    /// <summary>Jumps to a related entity from the detail pane.</summary>
    [RelayCommand]
    private void SelectEntity(string? entityName)
    {
        if (entityName is null || Scene is null || !Scene.Nodes.ContainsKey(entityName))
        {
            return;
        }

        SelectedEntity = entityName;
        CentreOn?.Invoke(entityName);
    }

    // ---- Export ----

    /// <summary>
    /// Writes the diagram out in one of the five formats.
    /// </summary>
    /// <remarks>
    /// One command taking the format as its parameter rather than five near-identical ones: the
    /// destination handling, the error handling and the status line are the same either way, and only
    /// the last two lines differ.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync(string? format)
    {
        var scene = Scene;
        var model = _saved.Model;

        if (scene is null || model is null
            || !Enum.TryParse<DiagramFormat>(format, ignoreCase: true, out var chosen))
        {
            return;
        }

        // Always a dialog. A diagram is exported to be pasted somewhere specific, unlike a script that
        // a project writes to the same folder every time, so a configured folder would be one more
        // setting to explain and no fewer clicks.
        var path = PickSaveFileAsync is null
            ? null
            : await PickSaveFileAsync(SuggestFileName(chosen), _lastSaveAsFolder);

        if (path is null)
        {
            return;
        }

        _lastSaveAsFolder = Path.GetDirectoryName(path);
        _persist();

        try
        {
            switch (chosen)
            {
                case DiagramFormat.Json:
                    File.WriteAllText(path, DiagramStore.ToJson(Filtered(model)));
                    break;

                case DiagramFormat.Svg:
                    File.WriteAllText(
                        path,
                        SvgWriter.Write(
                            scene,
                            DiagramPalette.Light,
                            MeasureText ?? LayoutOptions.Default.MeasureText));
                    break;

                case DiagramFormat.Png:
                    DiagramExport.WritePng(scene, path);
                    break;

                case DiagramFormat.Pdf:
                    DiagramExport.WritePdf(scene, path);
                    break;

                case DiagramFormat.Mermaid:
                    File.WriteAllText(path, MermaidWriter.Write(model, CurrentOptions()));
                    break;
            }

            _session.StatusMessage = $"Diagram exported to {path}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _session.StatusMessage = $"Could not write {path}: {ex.Message}";
        }
    }

    private bool CanExport(string? format) => HasDiagram;

    /// <summary>
    /// The model narrowed to what is on screen, so the JSON export describes the same diagram the
    /// image exports do. The others go through the scene or the view options and follow the filter
    /// already.
    /// </summary>
    private DiagramModel Filtered(DiagramModel model) => _visibleEntities is not { } shown
        ? model
        : model with
        {
            Entities = [.. model.Entities.Where(e => shown.Contains(e.Name))],
            Relationships = [.. model.Relationships.Where(r =>
                shown.Contains(r.DependentEntity) && shown.Contains(r.PrincipalEntity))],
        };

    /// <summary>
    /// Mermaid to the clipboard, since pasting it into a pull request description is what it is for.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasDiagram))]
    private async Task CopyMermaidAsync()
    {
        if (_saved.Model is not { } model || _session.CopyToClipboardAsync is null)
        {
            return;
        }

        await _session.CopyToClipboardAsync(MermaidWriter.Write(model, CurrentOptions()));
        _session.StatusMessage = "Mermaid diagram copied to the clipboard.";
    }

    /// <summary>
    /// The context, the view it is of, and the extension — enough that two exports of the same model
    /// do not land on top of each other.
    /// </summary>
    private string SuggestFileName(DiagramFormat format)
    {
        var context = Short(_contextName() ?? "model");
        var view = Kind == DiagramKind.Class ? "classes" : "tables";
        var safe = string.Concat(
            context.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        return $"{safe}-{view}.{Extension(format)}";
    }

    private static string Extension(DiagramFormat format) => format switch
    {
        DiagramFormat.Json => "json",
        DiagramFormat.Svg => "svg",
        DiagramFormat.Png => "png",
        DiagramFormat.Pdf => "pdf",
        // .mmd is what the Mermaid CLI and the editor extensions expect.
        _ => "mmd",
    };

    // ---- Called by the view ----

    /// <summary>A node was clicked, or the background was.</summary>
    public void Select(string? entityName) => SelectedEntity = entityName;

    /// <summary>
    /// A node was dragged. Rebuilds the scene from the layout rather than re-laying out, so dragging
    /// one node cannot shuffle the others.
    /// </summary>
    public void MoveNode(string entityName, DiagramPoint position)
    {
        if (_layout.Node(entityName) is null)
        {
            return;
        }

        var positions = new Dictionary<string, DiagramPoint>(
            _saved.PositionsFor(Kind, Flow), StringComparer.Ordinal);

        // Every node's current position is captured, not just the moved one. Without that, the first
        // drag pins one node and lets the layout re-flow the rest around it.
        foreach (var (name, point) in _layout.Positions())
        {
            positions[name] = point;
        }

        positions[entityName] = position;
        _saved.SetPositions(Kind, Flow, positions);

        Rebuild();
    }

    /// <summary>Called when a drag finishes, so a save happens once rather than per pointer move.</summary>
    public void CommitMove() => SaveDiagram();

    // ---- Work ----

    private void LoadSaved()
    {
        if (_workspacePath is null)
        {
            return;
        }

        var loaded = DiagramStore.Load(_settingsRoot, _workspacePath, _contextName());
        if (loaded?.Model is null)
        {
            ClearDiagram();
            return;
        }

        _saved = loaded;

        // The persisted view and lock win over the workspace defaults, because they are what this
        // diagram was actually left as.
        _restoring = true;
        try
        {
            Kind = loaded.Kind;
            Flow = loaded.Flow;
            IsUnlocked = !loaded.Locked;
            HighlightChanges = loaded.HighlightChanges;
            SelectedSnapshot = loaded.MigrationId is null
                ? CurrentModel
                : SnapshotOptionFor(loaded.MigrationId);

            ApplyOptions(loaded.Options ?? new DiagramViewOptions());

            _visibleEntities = loaded.VisibleEntities is { } filtered
                ? new HashSet<string>(filtered, StringComparer.Ordinal)
                : null;
        }
        finally
        {
            _restoring = false;
        }

        // After the selection is restored, so a migration missing from the list keeps its entry.
        RefreshSnapshotOptions();
        ApplyComparison();

        IsStale = DiagramStore.IsStale(loaded.Model);
        EmptyReason = null;
        NotifyFilterChanged();
        Rebuild();
        FitToWindow?.Invoke();
        OnPropertyChanged(nameof(SourceSummary));
    }

    private void SaveDiagram()
    {
        if (_workspacePath is null || _saved.Model is null)
        {
            return;
        }

        _saved.Kind = Kind;
        _saved.Flow = Flow;
        _saved.Locked = !IsUnlocked;
        _saved.HighlightChanges = HighlightChanges;
        _saved.Options = CurrentOptions() with { VisibleEntities = null };
        _saved.VisibleEntities = _visibleEntities?.ToList();

        DiagramStore.Save(_settingsRoot, _workspacePath, _contextName(), _saved);
    }

    /// <summary>Rebuilds nodes, layout and scene from the model already in hand. No file access.</summary>
    private void Rebuild()
    {
        if (_restoring)
        {
            return;
        }

        if (Rendered is not { } model)
        {
            Scene = null;
            return;
        }

        RefreshClassFilter(model);

        var options = CurrentOptions();
        _content = DiagramNodeContent.Build(model, options, _comparison?.Diff);

        var layoutOptions = CurrentLayoutOptions();

        _layout = DiagramLayoutEngine.Compute(
            _content, layoutOptions, _saved.PositionsFor(Kind, Flow));

        RefreshMatches();

        Scene = SceneBuilder.Build(
            _layout,
            layoutOptions,
            new SceneState(
                SelectedEntity,
                _matches.ToHashSet(StringComparer.Ordinal),
                IsSearching),
            options);

        // A selected entity that the current options hide — an inlined owned type, a collapsed join
        // table — has nothing left to describe.
        if (SelectedEntity is not null && !Scene.Nodes.ContainsKey(SelectedEntity))
        {
            SelectedEntity = null;
        }
        else
        {
            RefreshDetail();
        }
    }

    private void ClearDiagram()
    {
        _saved = new SavedDiagram();
        _comparison = null;
        _visibleEntities = null;
        _classFilterSource = default;
        _unlistedEntities = [];
        ClassFilter.Clear();
        NotifyFilterChanged();
        SetWithoutRegenerating(() => SelectedSnapshot = CurrentModel);
        _content = new DiagramNodeContent.Content([], []);
        _layout = DiagramLayout.Empty;
        Scene = null;
        SelectedEntity = null;
        IsStale = false;
        ModelCheckState = ModelCheckState.Unknown;
        EmptyReason = null;
        _matches.Clear();
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(DiffSummary));
        OnPropertyChanged(nameof(DiffMigration));
        OnPropertyChanged(nameof(DiffChanges));
        OnPropertyChanged(nameof(ShowsDiff));
    }

    /// <summary>
    /// The measurement and spacing the layout and the scene both have to agree on. One place, because
    /// a scene built with a different rank direction from the layout draws its markers and its edge
    /// labels the wrong way round.
    /// </summary>
    private LayoutOptions CurrentLayoutOptions()
    {
        var options = LayoutOptions.Default with { Flow = Flow };
        return MeasureText is null ? options : options with { MeasureText = MeasureText };
    }

    private DiagramViewOptions CurrentOptions() => new()
    {
        Kind = Kind,
        Properties = Properties,
        ShowTypes = ShowTypes,
        ShowNullability = ShowNullability,
        ShowIndexes = ShowIndexes,
        ShowNavigations = ShowNavigations,
        CollapseJoinEntities = CollapseJoinEntities,
        InlineOwnedTypes = InlineOwnedTypes,
        ShowDeleteBehavior = ShowDeleteBehavior,
        ShowInheritance = ShowInheritance,
        VisibleEntities = _visibleEntities,
    };

    private void ApplyOptions(DiagramViewOptions options)
    {
        Properties = options.Properties;
        ShowTypes = options.ShowTypes;
        ShowNullability = options.ShowNullability;
        ShowIndexes = options.ShowIndexes;
        ShowNavigations = options.ShowNavigations;
        CollapseJoinEntities = options.CollapseJoinEntities;
        InlineOwnedTypes = options.InlineOwnedTypes;
        ShowDeleteBehavior = options.ShowDeleteBehavior;
        ShowInheritance = options.ShowInheritance;
    }

    // ---- Search ----

    /// <summary>
    /// Recomputes the match set. Matches on everything a person might be looking for — the type, the
    /// table, a column, an index — because "where does OrderId live" is the question this answers.
    /// </summary>
    private void RefreshMatches()
    {
        var previous = _matchIndex >= 0 && _matchIndex < _matches.Count ? _matches[_matchIndex] : null;

        _matches.Clear();
        var term = Search.Trim();

        if (term.Length > 0 && Rendered is { } searchable)
        {
            foreach (var node in _content.Nodes)
            {
                var entity = searchable.Entity(node.EntityName);
                if (Matches(node, entity, term))
                {
                    _matches.Add(node.EntityName);
                }
            }
        }

        // Keep the cursor on the same entity across a re-render, so toggling an option mid-search
        // does not send "next match" back to the beginning.
        _matchIndex = previous is null ? -1 : _matches.IndexOf(previous);

        OnPropertyChanged(nameof(HasMatches));
        OnPropertyChanged(nameof(SearchSummary));
        OnPropertyChanged(nameof(IsSearching));
        NextMatchCommand.NotifyCanExecuteChanged();
    }

    private static bool Matches(DiagramNode node, DiagramEntity? entity, string term)
    {
        if (Contains(node.Title) || Contains(node.Subtitle) || Contains(node.EntityName))
        {
            return true;
        }

        if (node.Rows.Any(r => Contains(r.Name)))
        {
            return true;
        }

        // Also the underlying entity, so a search finds a column that the current property filter
        // happens to be hiding rather than reporting nothing and looking broken.
        return entity is not null
            && (entity.Properties.Any(p => Contains(p.Name) || Contains(p.ColumnName))
                || entity.Indexes.Any(i => Contains(i.DatabaseName)));

        bool Contains(string? value) =>
            value is not null && value.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Detail pane ----

    /// <summary>
    /// Everything the snapshot knows about the selected entity. All of it was extracted already, so
    /// there genuinely is more detail to show than the node has room for.
    /// </summary>
    private void RefreshDetail()
    {
        Detail.Clear();

        var model = Rendered;
        var entity = SelectedEntity is null ? null : model?.Entity(SelectedEntity);
        if (model is null || entity is null)
        {
            return;
        }

        List<DetailRow> about =
        [
            new("Type", entity.ShortName),
            new("Namespace", entity.Namespace ?? "—"),
            new("Table", ResolvedTable(entity, model) ?? "—"),
        ];

        if (entity.BaseType is not null)
        {
            about.Add(new DetailRow(
                "Inherits", Short(entity.BaseType), Target: entity.BaseType));
        }

        if (entity.DiscriminatorProperty is not null)
        {
            about.Add(new DetailRow(
                "Discriminator",
                entity.DiscriminatorProperty,
                entity.DiscriminatorValue is null ? null : $"= {entity.DiscriminatorValue}"));
        }

        if (entity.OwnerName is not null)
        {
            about.Add(new DetailRow("Owned by", Short(entity.OwnerName), Target: entity.OwnerName));
        }

        if (entity.IsImplicitJoin)
        {
            about.Add(new DetailRow(
                "Kind", "Join table", "Generated by EF for a many-to-many relationship"));
        }

        if (_comparison?.Diff.ForEntity(entity.Name) is { } change
            && change != DiagramChange.None)
        {
            about.Add(new DetailRow(
                "Change", change.ToString(), "against the previous migration"));
        }

        Detail.Add(new DetailGroup("Entity", about));

        Detail.Add(new DetailGroup("Properties",
        [
            .. entity.Properties.Select(p => new DetailRow(
                p.Name,
                p.DisplayType + (p.IsNotNull ? "" : " ?"),
                Note(p)))
        ]));

        if (entity.Indexes.Count > 0)
        {
            Detail.Add(new DetailGroup("Indexes",
            [
                .. entity.Indexes.Select(i => new DetailRow(
                    i.DisplayName,
                    string.Join(", ", i.Properties),
                    i.IsUnique ? "unique" : null))
            ]));
        }

        var outgoing = model.Relationships
            .Where(r => r.DependentEntity == entity.Name)
            .Select(r => new DetailRow(
                r.PrincipalNavigation ?? r.DependentNavigation ?? Short(r.PrincipalEntity),
                $"→ {Short(r.PrincipalEntity)}",
                Describe(r),
                r.PrincipalEntity))
            .ToList();

        if (outgoing.Count > 0)
        {
            Detail.Add(new DetailGroup("References", outgoing));
        }

        var incoming = model.Relationships
            .Where(r => r.PrincipalEntity == entity.Name)
            .Select(r => new DetailRow(
                r.PrincipalNavigation ?? Short(r.DependentEntity),
                $"← {Short(r.DependentEntity)}",
                Describe(r),
                r.DependentEntity))
            .ToList();

        if (incoming.Count > 0)
        {
            Detail.Add(new DetailGroup("Referenced by", incoming));
        }

        static string? Note(DiagramProperty p)
        {
            List<string> parts = [];
            if (p.IsKey)
            {
                parts.Add("key");
            }

            if (p.IsAlternateKey)
            {
                parts.Add("alternate key");
            }

            if (p.IsForeignKey)
            {
                parts.Add("foreign key");
            }

            if (p.MaxLength is { } length)
            {
                parts.Add($"max {length}");
            }

            if (p.DefaultValueSql is { } sql)
            {
                parts.Add($"default {sql}");
            }
            else if (p.DefaultValue is { } value)
            {
                parts.Add($"default {value}");
            }

            if (p.ValueGenerated is { } generated)
            {
                parts.Add($"generated {generated}");
            }

            return parts.Count == 0 ? null : string.Join(", ", parts);
        }

        static string Describe(DiagramRelationship r)
        {
            var kind = r.IsOwnership ? "owns" : r.Cardinality.ToString();
            var keys = r.ForeignKeyProperties.Count == 0
                ? ""
                : $" on {string.Join(", ", r.ForeignKeyProperties)}";
            var delete = r.DeleteBehavior is null ? "" : $", {r.DeleteBehavior} on delete";
            return kind + keys + delete;
        }
    }

    private static string? ResolvedTable(DiagramEntity entity, DiagramModel model)
    {
        var current = entity;
        var guard = 0;

        while (current.Table is null && current.BaseType is not null && guard++ < 16)
        {
            var next = model.Entity(current.BaseType);
            if (next is null)
            {
                break;
            }

            current = next;
        }

        return current.QualifiedTable;
    }

    private static string Short(string name)
    {
        var hash = name.LastIndexOf('#');
        var relevant = hash < 0 ? name : name[(hash + 1)..];
        var dot = relevant.LastIndexOf('.');
        return dot < 0 ? relevant : relevant[(dot + 1)..];
    }

    private void NotifyCommandStates()
    {
        OnPropertyChanged(nameof(IsReady));
        GenerateCommand.NotifyCanExecuteChanged();
        ReLayoutCommand.NotifyCanExecuteChanged();
        CheckPendingChangesCommand.NotifyCanExecuteChanged();
    }

    // ---- Property change plumbing ----

    partial void OnSceneChanged(DiagramScene? value)
    {
        OnPropertyChanged(nameof(HasDiagram));
        OnPropertyChanged(nameof(DetailPlaceholder));
        ReLayoutCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        CopyMermaidCommand.NotifyCanExecuteChanged();
    }

    partial void OnKindChanged(DiagramKind value)
    {
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(IsTableView));
        OnPropertyChanged(nameof(IsClassView));
        OnPropertyChanged(nameof(CanShowNavigations));
        Rebuild();
        Persist();
    }

    partial void OnFlowChanged(DiagramFlow value)
    {
        OnPropertyChanged(nameof(FlowLabel));
        Rebuild();
        Persist();

        // The diagram's extent changes shape entirely, so whatever zoom and offset suited the old
        // orientation frames the new one badly. Same reasoning as a fresh load.
        if (!_restoring)
        {
            FitToWindow?.Invoke();
        }
    }

    partial void OnIsUnlockedChanged(bool value)
    {
        OnPropertyChanged(nameof(LockLabel));
        OnPropertyChanged(nameof(LockTooltip));
        Persist();
    }

    partial void OnDetailVisibleChanged(bool value)
    {
        _display.DiagramDetailVisible = value;
        _persist();
    }

    partial void OnSelectedEntityChanged(string? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedEntityTitle));
        FocusSelectedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanExpandFromSelected));
        ExpandFromSelectedCommand.NotifyCanExecuteChanged();
        RefreshDetail();

        // Only the highlight changes, so the layout is left alone and the diagram does not jump.
        if (Scene is not null && _layout.Nodes.Count > 0)
        {
            Scene = SceneBuilder.Build(
                _layout,
                CurrentLayoutOptions(),
                new SceneState(value, _matches.ToHashSet(StringComparer.Ordinal), IsSearching),
                CurrentOptions());
        }
    }

    partial void OnSearchChanged(string value) => Rebuild();

    partial void OnSelectedSnapshotChanged(string value)
    {
        OnPropertyChanged(nameof(IsMigrationSelected));

        // Nothing selected is not a snapshot: a ComboBox whose list is being rebuilt writes null
        // back, and there is nothing to draw for it.
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        // Generated rather than offered: switching migration costs one file read, and flicking
        // through the history to watch the model grow is the whole point of the picker. The
        // Generate button stays for the case where nothing has been drawn yet.
        if (!_restoring && IsReady)
        {
            GenerateCommand.Execute(null);
        }
    }

    partial void OnHighlightChangesChanged(bool value)
    {
        if (_restoring)
        {
            return;
        }

        _saved.HighlightChanges = value;

        // Turning it on for a diagram generated without it means the earlier snapshot was never
        // read, so there is nothing to compare against yet and the files have to be read again.
        if (value && _saved.MigrationId is not null && _saved.Previous is null && IsReady)
        {
            GenerateCommand.Execute(null);
            return;
        }

        ApplyComparison();
        Rebuild();
        Persist();
    }

    partial void OnPropertiesChanged(PropertyDetail value) => OptionChanged();

    partial void OnShowTypesChanged(bool value) => OptionChanged();

    partial void OnShowNullabilityChanged(bool value) => OptionChanged();

    partial void OnShowIndexesChanged(bool value) => OptionChanged();

    partial void OnShowNavigationsChanged(bool value) => OptionChanged();

    partial void OnCollapseJoinEntitiesChanged(bool value) => OptionChanged();

    partial void OnInlineOwnedTypesChanged(bool value) => OptionChanged();

    partial void OnShowDeleteBehaviorChanged(bool value) => OptionChanged();

    partial void OnShowInheritanceChanged(bool value) => OptionChanged();

    private void OptionChanged()
    {
        Rebuild();
        Persist();
    }

    private void Persist()
    {
        if (_restoring)
        {
            return;
        }

        SaveDiagram();
        _persist();
    }
}
