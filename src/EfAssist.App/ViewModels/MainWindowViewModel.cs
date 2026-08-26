using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EfAssist.App.Updates;
using EfAssist.Core;
using EfAssist.Core.Diagrams;

namespace EfAssist.App.ViewModels;

/// <summary>A recent workspace, split for display: the name on top, where it lives underneath.</summary>
/// <param name="Name">Solution name, or folder name when the workspace is a bare folder.</param>
/// <param name="Location">The containing directory, shown as a subtitle.</param>
/// <param name="Path">The full path, which is what actually gets reopened.</param>
public sealed record RecentWorkspace(string Name, string Location, string Path)
{
    public static RecentWorkspace FromPath(string path)
    {
        var isDirectory = Directory.Exists(path);
        var name = isDirectory
            ? System.IO.Path.GetFileName(path.TrimEnd(
                System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))
            : System.IO.Path.GetFileNameWithoutExtension(path);

        var location = isDirectory ? path : System.IO.Path.GetDirectoryName(path) ?? path;

        // A deleted or renamed workspace still has a usable path to show.
        return new RecentWorkspace(
            string.IsNullOrEmpty(name) ? path : name,
            location,
            path);
    }
}

/// <summary>
/// The tabs, in the order they appear in <c>MainWindow.axaml</c>.
/// </summary>
/// <remarks>
/// Bound to <see cref="TabControl.SelectedIndex"/> through its numeric value, so the order here and
/// the order there have to match. It exists because the tab-activation switch used to compare against
/// a bare <c>1</c>, which is exactly the kind of thing that silently means something else the moment a
/// tab is inserted — as the Diagrams tab did.
/// </remarks>
public enum SelectedTab
{
    Migrations = 0,
    Script = 1,
    Diagrams = 2,
    Tools = 3,
}

/// <summary>
/// The whole shell. One view model rather than a navigation stack: there are two states (no
/// workspace, one workspace) and a bool tells them apart.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly IEfRunner _runner;
    private readonly AppSettings _settings;

    /// <summary>Null means the real per-user location. Overridden by tests.</summary>
    private readonly string? _settingsPath;

    /// <summary>Suppresses persistence while selections are being restored from settings.</summary>
    private bool _restoring;

    /// <summary>
    /// The context name from settings, kept separately because it must survive an empty context list
    /// — discovery has not run yet when selections are restored.
    /// </summary>
    private string? _savedContextName;

    /// <summary>
    /// The last tooling probe, kept so the versions line can be rebuilt when the project selection
    /// changes without re-running the probe. Null until <see cref="InitialiseAsync"/> has run.
    /// </summary>
    private ToolStatus? _toolStatus;

    public MainWindowViewModel() : this(new EfRunner(), SettingsStore.Load())
    {
    }

    public MainWindowViewModel(
        IEfRunner runner,
        AppSettings settings,
        string? settingsPath = null,
        IAppUpdater? updater = null)
    {
        _runner = runner;
        _settings = settings;
        _settingsPath = settingsPath;
        RecentWorkspaces = new ObservableCollection<RecentWorkspace>(
            settings.RecentWorkspaces.Select(RecentWorkspace.FromPath));
        _wrapOutput = settings.Display.WrapOutput;
        _wrapSql = settings.Display.WrapSql;
        _showLineNumbers = settings.Display.ShowLineNumbers;
        _migrationActionsExpanded = settings.Display.MigrationActionsExpanded;
        _outputExpanded = settings.Display.OutputExpanded;
        _leftPanelExpanded = settings.Display.LeftPanelExpanded;
        _defaultDiagramKind = settings.Display.DefaultDiagramKind;
        _openMaximised = settings.Display.Window.Maximised;
        Appearance = new SettingsViewModel(
            settings.Display,
            () => SettingsStore.Save(settings, settingsPath));

        Session = new CommandSession(runner) { DiagnosticsHeader = BuildDiagnosticsHeader };
        Session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CommandSession.IsRunning))
            {
                OnIsRunningChanged();
            }
        };

        // Script is constructed first: it owns the provider probe (CanUseIdempotent), which
        // Migrations.Detail's SQL preview shares rather than probing a second time. The Func
        // arguments below are lambdas, so the forward reference to Script inside Migrations'
        // construction only ever runs after both fields are assigned.
        Script = new ScriptViewModel(
            Session,
            BuildTargetForCommands,
            () => Migrations.Ordered,
            Persist,
            idempotentRequested: () => Idempotent,
            onIdempotentUnsupported: () => Idempotent = false);
        Migrations = new MigrationsViewModel(
            Session,
            BuildTargetForCommands,
            Persist,
            settings.Display,
            idempotentRequested: () => Idempotent,
            canUseIdempotent: () => Script.CanUseIdempotent,
            ensureProviderKnownAsync: Script.EnsureProviderKnownAsync);
        Tools = new ToolsViewModel(Session, BuildTargetForCommands);
        Diagrams = new DiagramsViewModel(
            Session,
            BuildTargetForCommands,
            () => SelectedContext?.Name ?? _savedContextName,
            () => Migrations.Ordered,
            Persist,
            settings.Display);
        Update = new UpdateViewModel(updater ?? new VelopackUpdater());
    }

    /// <summary>Runs commands and owns the output console. Shared by every tab.</summary>
    public CommandSession Session { get; }

    public MigrationsViewModel Migrations { get; }

    public ScriptViewModel Script { get; }

    public ToolsViewModel Tools { get; }

    /// <summary>The Diagrams tab: the model snapshot, drawn.</summary>
    public DiagramsViewModel Diagrams { get; }

    /// <summary>The in-app updater. Independent of any workspace, so it lives on the shell.</summary>
    public UpdateViewModel Update { get; }

    /// <summary>
    /// Theme, colours and font sizes. Lives on the shell rather than inside the settings window so
    /// the choices survive closing it, and so nothing has to be re-read on reopen.
    /// </summary>
    public SettingsViewModel Appearance { get; }

    /// <summary>
    /// Which tab is showing, as the index the <c>TabControl</c> binds to. Some tabs do work on first
    /// sight rather than on every context change, so they need to know when they become visible.
    /// </summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    /// <summary>The same thing, named. See <see cref="SelectedTab"/>.</summary>
    public SelectedTab CurrentTab => (SelectedTab)SelectedTabIndex;

    // ---- Supplied by the view, which owns the TopLevel these need ----

    public Func<Task<string?>>? PickSolutionAsync { get; set; }

    public Func<Task<string?>>? PickFolderAsync { get; set; }

    public Func<string, Task>? CopyToClipboardAsync
    {
        get => Session.CopyToClipboardAsync;
        set => Session.CopyToClipboardAsync = value;
    }

    /// <summary>See <see cref="CommandSession.PostToUiThread"/>.</summary>
    public Action<Action> PostToUiThread
    {
        get => Session.PostToUiThread;
        set => Session.PostToUiThread = value;
    }

    /// <summary>Shows a modal confirmation dialog. Supplied by the view.</summary>
    public Func<ConfirmRequest, Task<bool>>? ConfirmAsync
    {
        get => Migrations.ConfirmAsync;
        set
        {
            Migrations.ConfirmAsync = value;
            Script.ConfirmAsync = value;
            Diagrams.ConfirmAsync = value;
        }
    }

    // ---- Landing ----

    public ObservableCollection<RecentWorkspace> RecentWorkspaces { get; }

    public bool HasRecentWorkspaces => RecentWorkspaces.Count > 0;

    [ObservableProperty]
    private bool _hasWorkspace;

    // ---- Preflight ----

    [ObservableProperty]
    private string? _preflightProblem;

    [ObservableProperty]
    private string? _environmentSummary;

    public static string InstallCommand => ToolStatus.InstallCommand;

    public bool HasPreflightProblem => !string.IsNullOrEmpty(PreflightProblem);

    [ObservableProperty]
    private bool _efToolAvailable;

    /// <summary>Set once an update finishes successfully; cleared as soon as another one starts.</summary>
    [ObservableProperty]
    private bool _efToolUpdateSucceeded;

    /// <summary>The first line of the failure, shown next to the icon without needing the modal.</summary>
    [ObservableProperty]
    private string? _efToolUpdateErrorSummary;

    /// <summary>The full output, shown in <see cref="ShowErrorAsync"/> on request.</summary>
    [ObservableProperty]
    private string? _efToolUpdateErrorDetail;

    public bool HasEfToolUpdateError => !string.IsNullOrEmpty(EfToolUpdateErrorSummary);

    /// <summary>Shows a dismissible modal with the full text of a failure. Supplied by the view.</summary>
    public Func<ErrorDetail, Task>? ShowErrorAsync { get; set; }

    // ---- Workspace ----

    [ObservableProperty]
    private string? _workspacePath;

    [ObservableProperty]
    private string? _solutionPath;

    public ObservableCollection<ProjectRef> Projects { get; } = [];

    public ObservableCollection<DbContextRef> Contexts { get; } = [];

    /// <summary>
    /// True when the context list could not be refreshed. The previous list stays selectable — it is
    /// usually still right — but the app stops implying it was just confirmed.
    /// </summary>
    [ObservableProperty]
    private bool _contextsStale;

    public bool ShowsStaleContexts => ContextsStale && Contexts.Count > 0;

    public IReadOnlyList<DiscoveryMode> DiscoveryModes { get; } = Enum.GetValues<DiscoveryMode>();

    [ObservableProperty]
    private ProjectRef? _startupProject;

    [ObservableProperty]
    private ProjectRef? _migrationsProject;

    [ObservableProperty]
    private DbContextRef? _selectedContext;

    [ObservableProperty]
    private DiscoveryMode _discoveryMode = DiscoveryMode.Cached;

    [ObservableProperty]
    private bool _noBuild;

    /// <summary>
    /// Generate scripts that check the migrations history table, so they are safe to run more than
    /// once. Lives here rather than on the Script tab because the migration detail pane's SQL
    /// preview needs the same flag — one option in the shared workspace pane, not two independent
    /// ones that could disagree.
    /// </summary>
    [ObservableProperty]
    private bool _idempotent;

    // ---- Command execution ----

    private bool IsRunning => Session.IsRunning;

    /// <summary>
    /// Wrap long console lines rather than scrolling horizontally. Application-wide: it is a reading
    /// preference, not a property of any one solution.
    /// </summary>
    [ObservableProperty]
    private bool _wrapOutput;

    /// <summary>
    /// Wrap long lines in the Script tab's SQL viewer. Application-wide for the same reason as
    /// <see cref="WrapOutput"/>, and kept here rather than on <see cref="ScriptViewModel"/> so it
    /// saves through the same path — <c>Persist</c> bails out when no workspace is open.
    /// </summary>
    [ObservableProperty]
    private bool _wrapSql;

    /// <summary>
    /// Which diagram a workspace opens on before it has been switched. App-wide: it is a property of
    /// how the person thinks about their model, not of the solution.
    /// </summary>
    [ObservableProperty]
    private DiagramKind _defaultDiagramKind;

    public IReadOnlyList<DiagramKind> DiagramKinds { get; } = Enum.GetValues<DiagramKind>();

    /// <summary>
    /// Show line numbers beside the migration source and the generated SQL. App-wide for the same
    /// reason as <see cref="WrapOutput"/>, and shared by both viewers because wrapping already is.
    /// </summary>
    [ObservableProperty]
    private bool _showLineNumbers;

    /// <summary>
    /// Whether the workspace settings panel down the left is open. App-wide and persisted, on the
    /// same footing as <see cref="OutputExpanded"/>: the panel is shared by every tab, so folding it
    /// away to a rail is a single choice rather than one per screen.
    /// </summary>
    [ObservableProperty]
    private bool _leftPanelExpanded;

    /// <summary>
    /// Whether the output console is open. App-wide and persisted, on the same footing as
    /// <see cref="MigrationActionsExpanded"/>: the console is shared by every tab, so folding it
    /// away is a single choice rather than one per screen.
    /// </summary>
    [ObservableProperty]
    private bool _outputExpanded;

    /// <summary>
    /// Whether the Migrations tab's action panel is open. App-wide and persisted, so a user who
    /// folds it away does not have to fold it again next launch.
    /// </summary>
    [ObservableProperty]
    private bool _migrationActionsExpanded;

    /// <summary>
    /// Open the main window maximised. Also written when the window closes, so leaving it maximised
    /// is enough to keep it that way without visiting the settings screen.
    /// </summary>
    [ObservableProperty]
    private bool _openMaximised;

    /// <summary>
    /// Where the main window was last left. Exposed rather than mirrored property by property,
    /// because a window's size and position are the view's to read and nothing else here needs them.
    /// </summary>
    public WindowSettings WindowLayout => _settings.Display.Window;

    /// <summary>
    /// Records how the window was left, so the next launch matches. Bounds are null for a maximised
    /// window: its size is the screen's, and storing it would leave nothing to un-maximise to, so
    /// the previously remembered bounds are kept instead.
    /// </summary>
    public void SaveWindowLayout(bool maximised, (int X, int Y, double Width, double Height)? bounds)
    {
        var window = _settings.Display.Window;
        window.Maximised = maximised;

        if (bounds is { } b && b.Width > 0 && b.Height > 0)
        {
            window.X = b.X;
            window.Y = b.Y;
            window.Width = b.Width;
            window.Height = b.Height;
        }

        // Direct save rather than through OpenMaximised: this runs as the window closes, so no
        // checkbox is left on screen to keep in step, and the property's handler would only write
        // the same file a second time.
        SettingsStore.Save(_settings, _settingsPath);
    }

    /// <summary>
    /// The open workspace's own name — the solution file or folder, without its path. Empty when
    /// nothing is open, which is also when the toolbar showing it is hidden.
    /// </summary>
    public string WorkspaceName
    {
        get
        {
            if (WorkspacePath is null)
            {
                return string.Empty;
            }

            var trimmed = WorkspacePath.TrimEnd(Path.DirectorySeparatorChar);

            // A folder keeps its whole name — a directory called "Acme.Api" has no extension to
            // drop — while a solution file loses the .sln/.slnx that adds nothing on screen.
            return Directory.Exists(trimmed)
                ? Path.GetFileName(trimmed)
                : Path.GetFileNameWithoutExtension(trimmed);
        }
    }

    public string WindowTitle => WorkspacePath is null
        ? "EfAssist"
        : $"EfAssist — {WorkspaceName}";

    /// <summary>
    /// Shows a folder in the OS file browser. Supplied by the view, which owns the shell. Named
    /// apart from <c>OpenFolderCommand</c>, which picks a workspace rather than revealing one.
    /// </summary>
    public Func<string, Task>? ShowFolderAsync { get; set; }

    /// <summary>Reveals a recent workspace's folder without opening the workspace itself.</summary>
    [RelayCommand]
    private async Task ShowRecentFolderAsync(string? path)
    {
        if (ShowFolderAsync is null || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        // A recent entry is a solution file or a folder; either way the folder is what to show.
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (folder is not null)
        {
            await ShowFolderAsync(folder);
        }
    }

    [RelayCommand]
    private async Task ShowWorkspaceFolderAsync()
    {
        if (ShowFolderAsync is not null && WorkspacePath is not null)
        {
            await ShowFolderAsync(WorkingDirectory);
        }
    }

    /// <summary>
    /// Where commands are launched from. <c>dotnet ef</c> anchors itself to the target project
    /// regardless, so this only decides which local tool manifest applies.
    /// </summary>
    private string WorkingDirectory
    {
        get
        {
            if (SolutionPath is not null)
            {
                return Path.GetDirectoryName(SolutionPath) ?? AppContext.BaseDirectory;
            }

            if (WorkspacePath is null)
            {
                return AppContext.BaseDirectory;
            }

            return Directory.Exists(WorkspacePath)
                ? WorkspacePath
                : Path.GetDirectoryName(WorkspacePath) ?? AppContext.BaseDirectory;
        }
    }

    /// <summary>Called once the window is up, so the tooling check does not delay first paint.</summary>
    public Task InitialiseAsync() => CheckToolingAsync(AppContext.BaseDirectory);

    // ---- Opening a workspace ----

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenSolutionAsync()
    {
        var path = PickSolutionAsync is null ? null : await PickSolutionAsync();
        if (path is not null)
        {
            await OpenWorkspaceAsync(path);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenFolderAsync()
    {
        var path = PickFolderAsync is null ? null : await PickFolderAsync();
        if (path is not null)
        {
            await OpenWorkspaceAsync(path);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenRecentAsync(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            await OpenWorkspaceAsync(path);
        }
    }

    private bool CanOpen() => !IsRunning;

    [RelayCommand]
    private void RemoveRecent(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _settings.RemoveRecent(path);
        SettingsStore.Save(_settings, _settingsPath);
        SyncRecentWorkspaces();
    }

    [RelayCommand]
    private void CloseWorkspace()
    {
        Session.CancelCommand.Execute(null);
        HasWorkspace = false;
        WorkspacePath = null;
        SolutionPath = null;
        Projects.Clear();
        Contexts.Clear();
        Migrations.Clear();
        Script.Clear();
        Tools.Clear();
        Diagrams.Clear();
        Session.Reset();
    }

    // ---- Running commands ----

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshContextsAsync() => DiscoverContextsAsync(noBuildFirst: false);

    private bool CanRefresh() => !IsRunning && MigrationsProject is not null;




    [RelayCommand]
    private async Task CopyInstallCommandAsync()
    {
        if (CopyToClipboardAsync is null)
        {
            return;
        }

        await CopyToClipboardAsync(InstallCommand);
        Session.StatusMessage = "Install command copied to the clipboard.";
    }

    /// <summary>
    /// Updates the <c>dotnet-ef</c> tool in place and refreshes <see cref="EnvironmentSummary"/>
    /// afterwards. Runs against a local tool manifest when one pins <c>dotnet-ef</c> here, otherwise
    /// updates the global tool — see <see cref="Preflight.HasLocalDotnetEfTool"/>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task UpdateEfToolAsync()
    {
        EfToolUpdateSucceeded = false;
        EfToolUpdateErrorSummary = null;
        EfToolUpdateErrorDetail = null;

        var args = Preflight.HasLocalDotnetEfTool(WorkingDirectory)
            ? new[] { "tool", "update", "dotnet-ef" }
            : new[] { "tool", "update", "--global", "dotnet-ef" };

        var result = await Session.RunAsync(args, "Update dotnet-ef");
        if (result is null)
        {
            // Session already reported why: already running, cancelled, or an unhandled exception.
            return;
        }

        if (!result.Success)
        {
            Session.ReportFailure(result, "Could not update dotnet-ef.");
            EfToolUpdateErrorSummary = result.ErrorMessage.Length > 0
                ? FirstLine(result.ErrorMessage)
                : "Could not update dotnet-ef.";
            EfToolUpdateErrorDetail = result.Diagnostics;
            return;
        }

        Session.StatusMessage = "dotnet-ef update finished.";
        EfToolUpdateSucceeded = true;
        await CheckToolingAsync(WorkingDirectory);
    }

    [RelayCommand]
    private async Task ShowEfToolUpdateErrorAsync()
    {
        if (ShowErrorAsync is null || EfToolUpdateErrorDetail is null)
        {
            return;
        }

        await ShowErrorAsync(new ErrorDetail("Update dotnet-ef failed", EfToolUpdateErrorDetail));
    }

    // ---- Workspace loading ----

    private async Task OpenWorkspaceAsync(string path)
    {
        WorkspaceInfo workspace;
        try
        {
            workspace = await Workspace.DiscoverAsync(path, _runner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Session.StatusMessage = $"Could not open '{path}': {ex.Message}";
            return;
        }

        WorkspacePath = workspace.Path;
        SolutionPath = workspace.SolutionPath;

        Projects.Clear();
        foreach (var project in workspace.Projects)
        {
            Projects.Add(project);
        }

        Contexts.Clear();
        Session.Reset();
        Session.WorkingDirectory = WorkingDirectory;
        HasWorkspace = true;

        // Key settings off the solution when there is one, so opening the folder and opening the
        // solution inside it are treated as the same workspace.
        var key = workspace.SolutionPath ?? workspace.Path;
        RestoreSelections(key, workspace);

        _settings.MarkRecent(key);
        SettingsStore.Save(_settings, _settingsPath);
        SyncRecentWorkspaces();

        if (Projects.Count == 0)
        {
            Session.StatusMessage = "No projects found in this workspace.";
            return;
        }

        // A local tool manifest makes dotnet ef availability directory-dependent, so re-check here.
        await CheckToolingAsync(WorkingDirectory);
        if (HasPreflightProblem)
        {
            return;
        }

        await DiscoverForModeAsync();

        // Contexts alone are not much use: load the migrations for whichever one ended up selected.
        if (SelectedContext is not null)
        {
            await Migrations.LoadForContextAsync();
            Script.RefreshOptions();
            Diagrams.RefreshSnapshotOptions();
        }
    }

    private async Task DiscoverForModeAsync()
    {
        switch (DiscoveryMode)
        {
            case DiscoveryMode.Manual:
                Session.StatusMessage = Contexts.Count > 0
                    ? $"Using {Describe(Contexts.Count)} remembered from last time. Discovery is set to Manual."
                    : "Discovery is set to Manual for this workspace — use Refresh to load contexts.";
                return;

            case DiscoveryMode.Cached when Contexts.Count > 0:
                // The set of DbContext types rarely changes, so the remembered list is almost always
                // right and costs nothing. Refresh is there for when it isn't.
                Session.StatusMessage = $"Using {Describe(Contexts.Count)} remembered from last time. Refresh to re-scan.";
                return;

            case DiscoveryMode.Cached:
            case DiscoveryMode.Auto:
                await DiscoverContextsAsync(noBuildFirst: false);
                return;

            case DiscoveryMode.AutoNoBuildFirst:
                await DiscoverContextsAsync(noBuildFirst: true);
                return;
        }
    }

    private static string Describe(int contextCount) =>
        contextCount == 1 ? "1 DbContext" : $"{contextCount} DbContexts";

    private void RestoreSelections(string key, WorkspaceInfo workspace)
    {
        var saved = _settings.For(key);

        _restoring = true;
        try
        {
            DiscoveryMode = saved.Discovery;
            NoBuild = saved.NoBuild;
            Idempotent = saved.Idempotent;
            _savedContextName = saved.Context;

            StartupProject = Match(saved.StartupProject) ?? workspace.SuggestedStartupProject;
            MigrationsProject = Match(saved.MigrationsProject) ?? workspace.SuggestedMigrationsProject;

            // Populate from the remembered list straight away. Discovery may replace it below, but
            // the dropdown is usable immediately either way.
            foreach (var context in saved.KnownContexts)
            {
                Contexts.Add(context);
            }

            SelectedContext =
                Contexts.FirstOrDefault(c => c.Name == _savedContextName)
                ?? Contexts.FirstOrDefault();

            Migrations.Restore(saved);
            Script.Restore(saved);
            Diagrams.Restore(saved, _settings.Root, SolutionPath ?? WorkspacePath!);
        }
        finally
        {
            _restoring = false;
        }

        ProjectRef? Match(string? projectPath) => projectPath is null
            ? null
            : Projects.FirstOrDefault(p =>
                string.Equals(p.Path, projectPath, StringComparison.OrdinalIgnoreCase));
    }

    private void SyncRecentWorkspaces()
    {
        RecentWorkspaces.Clear();
        foreach (var recent in _settings.RecentWorkspaces)
        {
            RecentWorkspaces.Add(RecentWorkspace.FromPath(recent));
        }

        OnPropertyChanged(nameof(HasRecentWorkspaces));
    }

    private void Persist()
    {
        if (_restoring || WorkspacePath is null)
        {
            return;
        }

        var saved = _settings.For(SolutionPath ?? WorkspacePath);
        saved.StartupProject = StartupProject?.Path;
        saved.MigrationsProject = MigrationsProject?.Path;
        saved.Context = SelectedContext?.Name ?? _savedContextName;
        saved.Discovery = DiscoveryMode;
        saved.NoBuild = NoBuild;
        saved.Idempotent = Idempotent;

        // Only overwrite the remembered list when we actually have one; clearing it on an incidental
        // save would force a build on the next open.
        if (Contexts.Count > 0)
        {
            saved.KnownContexts = [.. Contexts];
        }

        Migrations.Store(saved);
        Script.Store(saved);
        Diagrams.Store(saved);
        SettingsStore.Save(_settings, _settingsPath);
    }

    // ---- Discovery ----

    private async Task DiscoverContextsAsync(bool noBuildFirst)
    {
        if (MigrationsProject is null)
        {
            Session.StatusMessage = "Select a migrations project first.";
            return;
        }

        // Only override the Skip build checkbox when this mode specifically demands --no-build.
        // Passing "false" here would silently ignore the user's choice.
        var result = await RunAsync(
            EfArgs.DbContextList(BuildTarget(forceNoBuild: noBuildFirst ? true : null)),
            "Listing DbContexts");

        if (result is null)
        {
            return;
        }

        // Retry with a build only if we were the ones who asked to skip it. If the user ticked
        // Skip build, a failure is theirs to see and act on, not something to silently undo.
        if (!result.Success && noBuildFirst && !NoBuild)
        {
            // --no-build only works when the project has been built before.
            Session.StatusMessage = "No usable existing build; building and retrying…";
            result = await RunAsync(
                EfArgs.DbContextList(BuildTarget(forceNoBuild: false)),
                "Listing DbContexts");

            if (result is null)
            {
                return;
            }
        }

        // Whatever it reported, that command restored the project — so an EF Core version that was
        // not readable before this point may be now.
        RefreshEnvironmentSummary();

        if (!result.Success)
        {
            ContextsStale = true;
            Session.StatusMessage = result.ErrorMessage.Length > 0
                ? FirstLine(result.ErrorMessage)
                : "Listing DbContexts failed — use Copy diagnostics for the full output.";
            return;
        }

        var contexts = EfJson.Contexts(result);
        if (contexts is null)
        {
            ContextsStale = true;
            Session.StatusMessage = "Could not read the DbContext list — use Copy diagnostics for the full output.";
            return;
        }

        ContextsStale = false;
        Contexts.Clear();
        foreach (var context in contexts)
        {
            Contexts.Add(context);
        }

        _restoring = true;
        try
        {
            // Prefer the saved context over "first in the list", so a refresh never silently moves
            // the user to a different context.
            SelectedContext =
                Contexts.FirstOrDefault(c => c.Name == _savedContextName)
                ?? Contexts.FirstOrDefault();
        }
        finally
        {
            _restoring = false;
        }

        Persist();

        Session.StatusMessage = Contexts.Count switch
        {
            0 => "No DbContext found in this project.",
            1 => "Found 1 DbContext.",
            var n => $"Found {n} DbContexts.",
        };
    }

    private EfTarget BuildTarget(bool? forceNoBuild = null) => new(
        Project: MigrationsProject!.Path,
        StartupProject: StartupProject?.Path,
        Context: null,
        NoBuild: forceNoBuild ?? NoBuild);

    /// <summary>
    /// The target the tabs use. Unlike <see cref="BuildTarget"/> it carries the selected context, and
    /// returns null until enough is selected to run anything at all.
    /// </summary>
    private EfTarget? BuildTargetForCommands() =>
        MigrationsProject is null || SelectedContext is null
            ? null
            : new EfTarget(
                Project: MigrationsProject.Path,
                StartupProject: StartupProject?.Path,
                Context: SelectedContext.Name,
                NoBuild: NoBuild);

    private Task<EfResult?> RunAsync(IReadOnlyList<string> args, string label) =>
        Session.RunAsync(args, label);

    private async Task CheckToolingAsync(string workingDirectory)
    {
        var status = await Preflight.CheckAsync(_runner, workingDirectory);

        _toolStatus = status;
        PreflightProblem = status.Problem;
        EfToolAvailable = status.EfToolAvailable;
        RefreshEnvironmentSummary();
    }

    /// <summary>
    /// Rebuilds the versions line. Separate from <see cref="CheckToolingAsync"/> because the
    /// project's EF Core version is part of it: that changes with the project selection, and appears
    /// the first time a restore has run, neither of which involves probing the tool again.
    /// </summary>
    private void RefreshEnvironmentSummary()
    {
        if (_toolStatus is not { } status)
        {
            EnvironmentSummary = null;
            return;
        }

        // The migrations project is the one dotnet ef loads the model from, so its resolved version
        // is the one that has to match the tool. The startup project is a fallback for the layout
        // where migrations live in a library that has not been restored on its own.
        var projectVersion =
            (MigrationsProject is null ? null : Preflight.ProjectEfCoreVersion(MigrationsProject.Path))
            ?? (StartupProject is null ? null : Preflight.ProjectEfCoreVersion(StartupProject.Path));

        var parts = new List<string>(3);

        if (status.EfToolAvailable)
        {
            parts.Add($"dotnet-ef {status.EfToolVersion}");
        }

        if (projectVersion is not null)
        {
            parts.Add(Preflight.ToolIsOlderThanProject(status.EfToolVersion, projectVersion)
                ? $"EF Core {projectVersion} (newer than the tool)"
                : $"EF Core {projectVersion}");
        }

        parts.Add($"SDK {status.SdkVersion ?? "unknown"}");

        EnvironmentSummary = string.Join(" · ", parts);
    }

    private string BuildDiagnosticsHeader() => string.Join(
        Environment.NewLine,
        EnvironmentSummary,
        $"Workspace:  {WorkspacePath}",
        $"Solution:   {SolutionPath}",
        $"Startup:    {StartupProject?.Path}",
        $"Migrations: {MigrationsProject?.Path}",
        $"Context:    {SelectedContext?.Name}",
        "");

    private static string FirstLine(string text)
    {
        var newline = text.IndexOf('\n');
        return newline < 0 ? text : text[..newline].TrimEnd('\r');
    }

    // ---- Property change plumbing ----

    private void OnIsRunningChanged()
    {
        OpenSolutionCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
        OpenRecentCommand.NotifyCanExecuteChanged();
        RefreshContextsCommand.NotifyCanExecuteChanged();
        UpdateEfToolCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The tabs derive their command states from <see cref="BuildTargetForCommands"/>, which reads
    /// these selections through a delegate and so raises nothing of its own. Whenever a selection
    /// that feeds the target changes, the tabs have to be told.
    /// </summary>
    private void NotifyTargetChanged()
    {
        Migrations.NotifyTargetChanged();
        Script.NotifyTargetChanged();
        Tools.NotifyTargetChanged();
        Diagrams.NotifyTargetChanged();
    }

    partial void OnWorkspacePathChanged(string? value)
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(WorkspaceName));
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentTab));

        switch ((SelectedTab)value)
        {
            // Probing the provider costs a build, so it happens on first sight rather than every time
            // a context is selected.
            case SelectedTab.Script:
                _ = Script.OnActivatedAsync();
                break;

            // Loads a saved diagram if there is one. Never generates: reading a file is free, parsing
            // a snapshot the user did not ask for is not the deal.
            case SelectedTab.Diagrams:
                _ = Diagrams.OnActivatedAsync();
                break;
        }
    }

    partial void OnPreflightProblemChanged(string? value) =>
        OnPropertyChanged(nameof(HasPreflightProblem));

    partial void OnEfToolUpdateErrorSummaryChanged(string? value) =>
        OnPropertyChanged(nameof(HasEfToolUpdateError));

    partial void OnContextsStaleChanged(bool value) => OnPropertyChanged(nameof(ShowsStaleContexts));

    partial void OnStartupProjectChanged(ProjectRef? value)
    {
        RefreshEnvironmentSummary();
        Persist();
    }

    partial void OnMigrationsProjectChanged(ProjectRef? value)
    {
        RefreshContextsCommand.NotifyCanExecuteChanged();
        RefreshEnvironmentSummary();
        NotifyTargetChanged();
        Persist();
    }

    partial void OnSelectedContextChanged(DbContextRef? value)
    {
        if (value is not null)
        {
            _savedContextName = value.Name;
        }

        // Outside the _restoring guard below on purpose. The tabs' commands are gated on a target
        // built from these selections, so they must be re-evaluated even when restoring or when a
        // context refresh reselects the same context — otherwise the buttons stay stuck reporting
        // the readiness of a moment ago.
        NotifyTargetChanged();

        Persist();

        // Migrations belong to a context, so switching context invalidates the list. Not while
        // restoring, though — that would wipe the remembered list before it is ever shown.
        if (!_restoring)
        {
            Migrations.Clear();
            Script.Clear();
            Tools.Clear();

            // Not Clear(): each context has its own saved diagram, so this swaps one for another
            // rather than throwing the feature's state away on every context switch.
            Diagrams.NotifyContextChanged();
            _ = Migrations.LoadForContextAsync();
        }
    }

    partial void OnDiscoveryModeChanged(DiscoveryMode value) => Persist();

    partial void OnNoBuildChanged(bool value) => Persist();

    partial void OnIdempotentChanged(bool value)
    {
        Script.NotifyIdempotentChanged();
        Persist();
    }

    partial void OnWrapOutputChanged(bool value)
    {
        // App-wide, so this saves even with no workspace open — Persist() would bail out.
        _settings.Display.WrapOutput = value;
        SettingsStore.Save(_settings, _settingsPath);
    }

    partial void OnWrapSqlChanged(bool value)
    {
        // App-wide, same as WrapOutput.
        _settings.Display.WrapSql = value;
        SettingsStore.Save(_settings, _settingsPath);
    }

    partial void OnShowLineNumbersChanged(bool value)
    {
        // App-wide, same as WrapOutput.
        _settings.Display.ShowLineNumbers = value;
        SettingsStore.Save(_settings, _settingsPath);
    }

    partial void OnDefaultDiagramKindChanged(DiagramKind value)
    {
        // Only decides which view a workspace opens on the first time. A workspace that has been
        // switched keeps its own choice, so changing this never overrides one already made.
        _settings.Display.DefaultDiagramKind = value;
        SettingsStore.Save(_settings, _settingsPath);
    }

    partial void OnMigrationActionsExpandedChanged(bool value)
    {
        // App-wide, same as WrapOutput.
        _settings.Display.MigrationActionsExpanded = value;
        SettingsStore.Save(_settings, _settingsPath);
    }

    partial void OnLeftPanelExpandedChanged(bool value)
    {
        // App-wide, same as WrapOutput.
        _settings.Display.LeftPanelExpanded = value;
        SettingsStore.Save(_settings, _settingsPath);
    }

    [RelayCommand]
    private void ToggleLeftPanel() => LeftPanelExpanded = !LeftPanelExpanded;

    /// <summary>
    /// Selects a tab by index, for the Alt+1..4 accelerators. The parameter arrives as a string
    /// because that is what a KeyBinding's CommandParameter is; anything unparseable is ignored
    /// rather than throwing at a keystroke.
    /// </summary>
    [RelayCommand]
    private void SelectTab(string? index)
    {
        if (int.TryParse(index, out var value) && value >= 0 && value <= (int)SelectedTab.Tools)
        {
            SelectedTabIndex = value;
        }
    }

    partial void OnOutputExpandedChanged(bool value)
    {
        // App-wide, same as WrapOutput.
        _settings.Display.OutputExpanded = value;
        SettingsStore.Save(_settings, _settingsPath);
    }

    partial void OnOpenMaximisedChanged(bool value)
    {
        // Takes effect on the next launch: maximising the window from here would fight the user's
        // own window management for the rest of the session.
        _settings.Display.Window.Maximised = value;
        SettingsStore.Save(_settings, _settingsPath);
    }

    /// <summary>
    /// Opens the settings modal. Supplied by the view, which owns the window it has to be modal to.
    /// </summary>
    public Func<Task>? ShowSettingsAsync { get; set; }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        if (ShowSettingsAsync is not null)
        {
            await ShowSettingsAsync();
        }
    }

    /// <summary>
    /// Relaunches the app. Supplied by the view, which owns the process and the application lifetime.
    /// </summary>
    public Action? RestartRequested { get; set; }

    /// <summary>
    /// Restarts so that a colour change takes effect. Blocked while a command is running: a restart
    /// kills the <c>dotnet ef</c> child process, and losing a half-finished "update database" to a
    /// theme change would be indefensible.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRestart))]
    private void Restart() => RestartRequested?.Invoke();

    private bool CanRestart() => !Session.IsRunning;
}
