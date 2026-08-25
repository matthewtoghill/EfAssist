using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

/// <summary>A group of <see cref="DetailRow"/>s under a heading.</summary>
public sealed record DetailGroup(string Title, IReadOnlyList<DetailRow> Rows)
{
    public bool HasRows => Rows.Count > 0;
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

    /// <summary>Suppresses persistence and re-rendering while state is restored in bulk.</summary>
    private bool _restoring;

    public DiagramsViewModel(
        CommandSession session,
        Func<EfTarget?> target,
        Func<string?> contextName,
        Action persist,
        DisplaySettings display)
    {
        _session = session;
        _target = target;
        _contextName = contextName;
        _persist = persist;
        _display = display;

        _optionsExpanded = display.DiagramOptionsExpanded;
        _detailVisible = display.DiagramDetailVisible;
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

    // ---- The diagram ----

    [ObservableProperty]
    private DiagramScene? _scene;

    public bool HasDiagram => Scene is { IsEmpty: false };

    public DiagramModel? Model => _saved.Model;

    // ---- View selection ----

    public IReadOnlyList<DiagramKind> Kinds { get; } = Enum.GetValues<DiagramKind>();

    [ObservableProperty]
    private DiagramKind _kind;

    public string KindLabel => Kind == DiagramKind.EntityRelationship
        ? "Entity relationships"
        : "Classes";

    public string SwitchViewLabel => Kind == DiagramKind.EntityRelationship
        ? "Show classes"
        : "Show tables";

    // ---- View options ----

    [ObservableProperty]
    private bool _optionsExpanded;

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

    public string? SourceSummary => _saved.Model is { } model && model.Entities.Count > 0
        ? $"{model.Entities.Count} entities from {Path.GetFileName(model.SourcePath)}"
          + (model.EfVersion is null ? "" : $" (EF {model.EfVersion})")
        : null;

    public bool IsReady => !_session.IsRunning && _target() is not null;

    // ---- Lifecycle ----

    /// <summary>
    /// Called when the tab becomes visible. Loads a saved diagram if there is one; never generates.
    /// </summary>
    public Task OnActivatedAsync()
    {
        if (Scene is null)
        {
            LoadSaved();
        }

        return Task.CompletedTask;
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
            IsUnlocked = !saved.DiagramLocked;
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
        saved.DiagramLocked = !IsUnlocked;
        saved.DiagramOptions = CurrentOptions();
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

        var model = await _session.RunLocalAsync("Generating diagram", async token =>
        {
            // Off the UI thread: a large snapshot is still only milliseconds, but the read is I/O and
            // there is no reason to do it where it can stutter the window.
            return await Task.Run(() =>
            {
                var path = ModelSnapshotLocator.Find(project, context, token);
                if (path is null)
                {
                    return null;
                }

                token.ThrowIfCancellationRequested();
                return ModelSnapshotParser.Parse(File.ReadAllText(path), path, context, token);
            }, token);
        });

        if (model is null)
        {
            // Cancelled, already running, or no snapshot. Only the last of those needs explaining,
            // and only when nothing else has already put a message up.
            if (!_session.IsRunning && ModelSnapshotLocator.Find(project, context) is null)
            {
                EmptyReason =
                    $"{context ?? "This context"} has no migrations, so there is no model snapshot "
                    + "to draw. Add a migration on the Migrations tab first.";
                _session.StatusMessage = "No model snapshot found for this context.";
            }

            return;
        }

        _saved.Model = model;
        _saved.Kind = Kind;
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
            : $"Diagram generated: {SourceSummary}.";

        OnPropertyChanged(nameof(SourceSummary));
    }

    [RelayCommand]
    private void SwitchView() =>
        Kind = Kind == DiagramKind.EntityRelationship
            ? DiagramKind.Class
            : DiagramKind.EntityRelationship;

    [RelayCommand]
    private void ToggleLock() => IsUnlocked = !IsUnlocked;

    /// <summary>
    /// Throws away hand-dragged positions for the current view and lays it out again. Confirmed,
    /// because arranging a diagram is work and this is the one action that destroys it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasDiagram))]
    private async Task ReLayoutAsync()
    {
        if (_saved.PositionsFor(Kind).Count > 0
            && ConfirmAsync is not null
            && !await ConfirmAsync(new ConfirmRequest(
                "Re-layout diagram",
                $"Arrange the {KindLabel.ToLowerInvariant()} diagram automatically?",
                "Re-layout",
                "Nodes you have moved by hand in this view go back to their computed positions. The "
                + "other view is not affected.")))
        {
            return;
        }

        _saved.Positions.Remove(Kind.ToString());
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
            _saved.PositionsFor(Kind), StringComparer.Ordinal);

        // Every node's current position is captured, not just the moved one. Without that, the first
        // drag pins one node and lets the layout re-flow the rest around it.
        foreach (var (name, point) in _layout.Positions())
        {
            positions[name] = point;
        }

        positions[entityName] = position;
        _saved.SetPositions(Kind, positions);

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
            IsUnlocked = !loaded.Locked;
            ApplyOptions(loaded.Options ?? new DiagramViewOptions());
        }
        finally
        {
            _restoring = false;
        }

        IsStale = DiagramStore.IsStale(loaded.Model);
        EmptyReason = null;
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
        _saved.Locked = !IsUnlocked;
        _saved.Options = CurrentOptions();

        DiagramStore.Save(_settingsRoot, _workspacePath, _contextName(), _saved);
    }

    /// <summary>Rebuilds nodes, layout and scene from the model already in hand. No file access.</summary>
    private void Rebuild()
    {
        if (_restoring)
        {
            return;
        }

        if (_saved.Model is null)
        {
            Scene = null;
            return;
        }

        var options = CurrentOptions();
        _content = DiagramNodeContent.Build(_saved.Model, options);

        var layoutOptions = MeasureText is null
            ? LayoutOptions.Default
            : LayoutOptions.Default with { MeasureText = MeasureText };

        _layout = DiagramLayoutEngine.Compute(
            _content, layoutOptions, _saved.PositionsFor(Kind));

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
        _content = new DiagramNodeContent.Content([], []);
        _layout = DiagramLayout.Empty;
        Scene = null;
        SelectedEntity = null;
        IsStale = false;
        ModelCheckState = ModelCheckState.Unknown;
        EmptyReason = null;
        _matches.Clear();
        OnPropertyChanged(nameof(SourceSummary));
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

        if (term.Length > 0 && _saved.Model is not null)
        {
            foreach (var node in _content.Nodes)
            {
                var entity = _saved.Model.Entity(node.EntityName);
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

        var model = _saved.Model;
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
    }

    partial void OnKindChanged(DiagramKind value)
    {
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(SwitchViewLabel));
        OnPropertyChanged(nameof(CanShowNavigations));
        Rebuild();
        Persist();
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
        RefreshDetail();

        // Only the highlight changes, so the layout is left alone and the diagram does not jump.
        if (Scene is not null && _layout.Nodes.Count > 0)
        {
            Scene = SceneBuilder.Build(
                _layout,
                MeasureText is null
                    ? LayoutOptions.Default
                    : LayoutOptions.Default with { MeasureText = MeasureText },
                new SceneState(value, _matches.ToHashSet(StringComparer.Ordinal), IsSearching),
                CurrentOptions());
        }
    }

    partial void OnSearchChanged(string value) => Rebuild();

    partial void OnOptionsExpandedChanged(bool value)
    {
        _display.DiagramOptionsExpanded = value;
        _persist();
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
