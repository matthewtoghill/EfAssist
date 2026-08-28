using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EfAssist.Core;

namespace EfAssist.App.ViewModels;

/// <summary>
/// The Migrations tab: list the migrations for the selected context, and add, remove, apply or drop.
/// Every command that can change a database is confirmed first, naming its exact target.
/// </summary>
public partial class MigrationsViewModel : ObservableObject
{
    private readonly CommandSession _session;

    /// <summary>Reads the current project and context selections from the shell, always fresh.</summary>
    private readonly Func<EfTarget?> _target;

    /// <summary>Called after any change, so the shell can save settings and remember the list.</summary>
    private readonly Action _persist;

    private readonly DisplaySettings _display;

    /// <summary>
    /// Chronological order, as EF reports it. <see cref="Migrations"/> is only a view of this, so
    /// changing the sort order can never change which migration is "the last one".
    /// </summary>
    private readonly List<MigrationInfo> _ordered = [];

    public MigrationsViewModel(
        CommandSession session,
        Func<EfTarget?> target,
        Action persist,
        DisplaySettings? display = null,
        Func<bool>? idempotentRequested = null,
        Func<bool>? canUseIdempotent = null,
        Func<Task>? ensureProviderKnownAsync = null)
    {
        _session = session;
        _target = target;
        _persist = persist;
        _display = display ?? new DisplaySettings();
        _sortNewestFirst = _display.SortNewestFirst;
        Detail = new MigrationDetailViewModel(
            session, target, () => _ordered, idempotentRequested, canUseIdempotent, ensureProviderKnownAsync);

        _session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CommandSession.IsRunning))
            {
                NotifyCommandStates();
            }
        };
    }

    /// <summary>
    /// The pane beside the list: the selected migration's source, and on request its SQL.
    /// </summary>
    public MigrationDetailViewModel Detail { get; }

    /// <summary>Supplied by the view: shows a modal confirmation and reports what the user chose.</summary>
    public Func<ConfirmRequest, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>
    /// Supplied by the view: shows generated SQL read-only in its own window, over the confirmation
    /// that asked for it. Null means no preview is on offer, which is what the dialog reads to decide
    /// whether to show the button at all.
    /// </summary>
    public Func<SqlPreviewRequest, Task>? ShowSqlPreviewAsync { get; set; }

    /// <summary>The list as displayed, which may be newest-first. Row numbers stay chronological.</summary>
    public ObservableCollection<MigrationRow> Migrations { get; } = [];

    public IReadOnlyList<DiscoveryMode> RefreshModes { get; } = Enum.GetValues<DiscoveryMode>();

    [ObservableProperty]
    private MigrationRow? _selectedMigration;

    /// <summary>When to load the list on opening a workspace or switching context.</summary>
    [ObservableProperty]
    private DiscoveryMode _refreshMode = DiscoveryMode.Cached;

    /// <summary>Newest at the top. Display only — see <see cref="_ordered"/>.</summary>
    [ObservableProperty]
    private bool _sortNewestFirst;

    /// <summary>Pass <c>--no-connect</c>: list migration names without reaching the database.</summary>
    [ObservableProperty]
    private bool _offline;

    /// <summary>Set when the database could not be reached and the list was loaded offline anyway.</summary>
    [ObservableProperty]
    private string? _connectionWarning;

    /// <summary>
    /// True when what is on screen may no longer match reality: a refresh failed, or a command that
    /// changes migrations or the database did not finish. The list is kept rather than cleared —
    /// out-of-date names are still worth reading — but it stops presenting itself as current.
    /// </summary>
    [ObservableProperty]
    private bool _isStale;

    [ObservableProperty]
    private string _newMigrationName = "";

    /// <summary>Optional output directory for a new migration.</summary>
    [ObservableProperty]
    private string _outputDirectory = "";

    /// <summary>
    /// Removing with force also reverts the migration from the database if it has been applied. Off
    /// by default, because that is a database change hiding behind a file operation.
    /// </summary>
    [ObservableProperty]
    private bool _forceRemove;

    public bool HasConnectionWarning => !string.IsNullOrEmpty(ConnectionWarning);

    /// <summary>Only worth saying when there is a list on screen to distrust.</summary>
    public bool ShowsStaleWarning => IsStale && HasMigrations;

    public bool HasMigrations => _ordered.Count > 0;

    /// <summary>
    /// The list in EF's own chronological order, for anything that needs to reason about sequence
    /// rather than display it. The Script tab builds its range pickers from this.
    /// </summary>
    public IReadOnlyList<MigrationInfo> Ordered => _ordered;

    public bool IsReady => !_session.IsRunning && _target() is not null;

    /// <summary>Arrow for the sort toggle: down for newest first, up for oldest first.</summary>
    public string SortGlyph => SortNewestFirst ? "↓" : "↑";

    public string SortTooltip => SortNewestFirst
        ? "Newest first. Click for oldest first."
        : "Oldest first, the order migrations are applied in. Click for newest first.";

    /// <summary>
    /// EF can only remove the most recent migration, so Remove always targets the chronologically
    /// last one — never whatever happens to be at the bottom of the displayed list.
    /// </summary>
    public MigrationInfo? LastMigration => _ordered.LastOrDefault();

    public string? NewMigrationNameError =>
        NewMigrationName.Length == 0
            ? null
            : MigrationName.Validate(NewMigrationName, _ordered.Select(m => m.Name));

    public bool HasNewMigrationNameError => NewMigrationNameError is not null;

    public int AppliedCount => _ordered.Count(m => m.State == MigrationState.Applied);

    public int PendingCount => _ordered.Count(m => m.State == MigrationState.Pending);

    public string Summary => _ordered.Count == 0
        ? "No migrations."
        : $"{_ordered.Count} migrations · {AppliedCount} applied · {PendingCount} pending";

    // ---- Restore and reset ----

    /// <summary>
    /// Repopulates from the workspace's remembered list. Applied state is deliberately dropped: a
    /// remembered row reads as Unknown until refreshed, because a stale "applied" is worse than none.
    /// </summary>
    public void Restore(WorkspaceSettings saved)
    {
        RefreshMode = saved.MigrationRefresh;
        Offline = saved.Offline;
        ConnectionWarning = null;
        IsStale = false;

        _ordered.Clear();
        _ordered.AddRange(saved.KnownMigrations.Select(m => m with { Applied = null }));
        Detail.Clear();
        RebuildDisplay();
    }

    /// <summary>Copies current state into the settings object the shell is about to save.</summary>
    public void Store(WorkspaceSettings saved)
    {
        saved.MigrationRefresh = RefreshMode;
        saved.Offline = Offline;

        if (_ordered.Count > 0)
        {
            // Never persist applied state, so it cannot come back as a stale truth next session.
            // Always chronological, whatever the display order happens to be.
            saved.KnownMigrations = [.. _ordered.Select(m => m with { Applied = null })];
        }
    }

    public void Clear()
    {
        _ordered.Clear();
        Migrations.Clear();
        SelectedMigration = null;
        ConnectionWarning = null;
        IsStale = false;
        NewMigrationName = "";
        ForceRemove = false;
        Detail.Clear();
        NotifyListChanged();
    }

    /// <summary>
    /// Loads the list according to <see cref="RefreshMode"/>, for use when a workspace opens or the
    /// context changes.
    /// </summary>
    public Task LoadForContextAsync() => RefreshMode switch
    {
        DiscoveryMode.Manual => Task.CompletedTask,
        DiscoveryMode.Cached when _ordered.Count > 0 => Task.CompletedTask,
        DiscoveryMode.AutoNoBuildFirst => LoadAsync(noBuildFirst: true),
        _ => LoadAsync(noBuildFirst: false),
    };

    // ---- Commands ----

    [RelayCommand]
    private void ToggleSortOrder() => SortNewestFirst = !SortNewestFirst;

    [RelayCommand(CanExecute = nameof(IsReady))]
    private Task RefreshAsync() => LoadAsync(noBuildFirst: false);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        var target = _target();
        if (target is null)
        {
            return;
        }

        var name = NewMigrationName.Trim();
        var error = MigrationName.Validate(name, _ordered.Select(m => m.Name));
        if (error is not null)
        {
            _session.StatusMessage = error;
            return;
        }

        // Anything from here on can leave the list behind: the command may add a migration and then
        // fail, or be cancelled mid-flight. Cleared again by a successful reload.
        IsStale = true;

        // Not confirmed: adding a migration writes source files and touches no database.
        var directory = OutputDirectory.Trim();
        var result = await _session.RunAsync(
            EfArgs.MigrationsAdd(target, name, directory.Length == 0 ? null : directory),
            $"Adding migration '{name}'");

        if (result is null)
        {
            return;
        }

        if (!result.Success)
        {
            _session.ReportFailure(result, $"Could not add '{name}'.");
            return;
        }

        NewMigrationName = "";
        _session.StatusMessage = $"Added migration '{name}'.";
        await LoadAsync(noBuildFirst: false);
    }

    private bool CanAdd() => IsReady && NewMigrationName.Trim().Length > 0 && !HasNewMigrationNameError;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAsync()
    {
        var target = _target();
        var last = LastMigration;
        if (target is null || last is null)
        {
            return;
        }

        var detail = ForceRemove
            ? "Force is on: if this migration has been applied, it will be reverted in the database "
              + "before the files are deleted."
            : "Only the migration files are deleted. If it has already been applied to the database, "
              + "EF will refuse — revert it first, or use Force.";

        if (!await ConfirmedAsync(new ConfirmRequest(
                "Remove migration",
                $"Remove the most recent migration, '{last.Name}'?",
                "Remove",
                detail)))
        {
            return;
        }

        IsStale = true;
        var result = await _session.RunAsync(
            EfArgs.MigrationsRemove(target, ForceRemove),
            $"Removing migration '{last.Name}'");

        if (result is null)
        {
            return;
        }

        if (!result.Success)
        {
            _session.ReportFailure(result, $"Could not remove '{last.Name}'.");
            return;
        }

        _session.StatusMessage = $"Removed migration '{last.Name}'.";
        await LoadAsync(noBuildFirst: false);
    }

    private bool CanRemove() => IsReady && LastMigration is not null;

    [RelayCommand(CanExecute = nameof(IsReady))]
    private Task UpdateToLatestAsync() => UpdateDatabaseAsync(null);

    [RelayCommand(CanExecute = nameof(CanUpdateToSelected))]
    private Task UpdateToSelectedAsync() => UpdateDatabaseAsync(SelectedMigration!.Name);

    private bool CanUpdateToSelected() => IsReady && SelectedMigration is not null;

    /// <summary>Target "0" reverts every migration, which EF accepts in place of a migration name.</summary>
    [RelayCommand(CanExecute = nameof(CanRevertAll))]
    private Task RevertAllAsync() => UpdateDatabaseAsync("0");

    private bool CanRevertAll() => IsReady && _ordered.Count > 0;

    [RelayCommand(CanExecute = nameof(IsReady))]
    private async Task DropDatabaseAsync()
    {
        var target = _target();
        if (target is null)
        {
            return;
        }

        // Ask EF what the database is actually called; guessing here would make the typed
        // confirmation meaningless.
        var info = await _session.RunAsync(EfArgs.DbContextInfo(target), "Reading database details");
        if (info is null)
        {
            return;
        }

        if (!info.Success)
        {
            _session.ReportFailure(info, "Could not read the database details.");
            return;
        }

        var details = EfJson.ContextDetails(info);
        var name = details?.ConfirmationName;
        if (details is null || string.IsNullOrWhiteSpace(name))
        {
            // Without a name there is nothing to type, and an ungated drop is not on offer.
            _session.StatusMessage =
                "Could not determine the database name, so the drop was not offered. Use Copy diagnostics.";
            return;
        }

        if (!await ConfirmedAsync(new ConfirmRequest(
                "Drop database",
                $"Permanently delete the database '{name}' used by {details.Type}?",
                "Drop database",
                $"Every table and row is destroyed. This cannot be undone. Provider: {details.ProviderName}.",
                RequiredTypedValue: name)))
        {
            return;
        }

        IsStale = true;
        var result = await _session.RunAsync(EfArgs.DatabaseDrop(target), $"Dropping database '{name}'");
        if (result is null)
        {
            return;
        }

        if (!result.Success)
        {
            _session.ReportFailure(result, $"Could not drop '{name}'.");
            return;
        }

        _session.StatusMessage = $"Dropped database '{name}'.";
        await LoadAsync(noBuildFirst: false);
    }

    // ---- Shared work ----

    /// <param name="targetMigration">Null applies everything outstanding; "0" reverts everything.</param>
    private async Task UpdateDatabaseAsync(string? targetMigration)
    {
        var target = _target();
        if (target is null)
        {
            return;
        }

        // Every route to `database update` is confirmed, including applying forward. A misclick on
        // "Update to latest" would otherwise run migrations against whatever database the startup
        // project is currently pointed at, with no way back.
        //
        // The preview is attached here rather than inside BuildUpdateConfirmation so all three routes
        // — forward, rollback and revert-all — get it from one line, and dropping a database, which
        // has no migration SQL to show, keeps its own request untouched.
        var confirmation = BuildUpdateConfirmation(targetMigration);
        if (ShowSqlPreviewAsync is not null)
        {
            confirmation = confirmation with { PreviewAsync = () => PreviewUpdateAsync(targetMigration) };
        }

        if (!await ConfirmedAsync(confirmation))
        {
            return;
        }

        var label = targetMigration switch
        {
            null => "Updating database to the latest migration",
            "0" => "Reverting all migrations",
            var name => $"Updating database to '{name}'",
        };

        IsStale = true;
        var result = await _session.RunAsync(EfArgs.DatabaseUpdate(target, targetMigration), label);
        if (result is null)
        {
            return;
        }

        if (!result.Success)
        {
            _session.ReportFailure(result, "The database update failed.");
            return;
        }

        _session.StatusMessage = targetMigration switch
        {
            null => "Database is up to date.",
            "0" => "Reverted all migrations.",
            var name => $"Database updated to '{name}'.",
        };

        await LoadAsync(noBuildFirst: false);
    }

    /// <summary>
    /// Generates the SQL a pending <c>database update</c> would run and hands it to the view to show.
    /// Called from the confirmation dialog's Preview button, so it runs while that dialog is open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never cached. The other SQL preview — a single migration's, in the detail pane — is safe to
    /// cache because a migration's file does not change under it. This one describes what is about to
    /// happen to a database, and showing a script generated before the last apply would be worse than
    /// showing none. Each press regenerates, and the press is the user asking for exactly that.
    /// </para>
    /// <para>
    /// Never <c>--idempotent</c> either, whatever the shared option says: <c>database update</c> does
    /// not run idempotent SQL, and a preview has one job, which is to be what the run will execute.
    /// </para>
    /// </remarks>
    private async Task PreviewUpdateAsync(string? targetMigration)
    {
        var target = _target();
        if (target is null || ShowSqlPreviewAsync is null)
        {
            return;
        }

        var (from, to, uncertain) = PreviewRange(targetMigration);
        var path = MigrationFiles.UpdatePreviewPath(target.Project, target.Context, from, to);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _session.StatusMessage = $"Could not use the temp folder for the preview: {ex.Message}";
            return;
        }

        var result = await _session.RunAsync(
            EfArgs.MigrationsScript(target, path, from, to),
            "Generating the SQL for this update");

        // Null means another command was already running, or this one was cancelled. Either way there
        // is nothing to show, and the confirmation stays up so the user can decide without it.
        if (result is null)
        {
            return;
        }

        if (!result.Success)
        {
            _session.ReportFailure(result, "Could not generate the SQL for this update.");
            return;
        }

        string sql;
        try
        {
            sql = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _session.StatusMessage = $"The SQL was written to {path} but could not be read: {ex.Message}";
            return;
        }

        var start = from == "0" ? "an empty database" : $"'{from}'";
        await ShowSqlPreviewAsync(new SqlPreviewRequest(
            Title: targetMigration switch
            {
                null => "SQL preview — apply all outstanding migrations",
                "0" => "SQL preview — revert all migrations",
                var name => $"SQL preview — update to '{name}'",
            },
            Sql: sql,
            Path: path,
            Caveat: uncertain
                ? $"Scripted from {start}, because the applied state is unknown — the list was loaded "
                  + "without a database connection. The real update starts from wherever the database "
                  + "actually is, so it may run less than this."
                : null,
            Wrap: _display.WrapSql,
            ShowLineNumbers: _display.ShowLineNumbers));
    }

    /// <summary>
    /// The range to script for a preview of <paramref name="targetMigration"/>: from where the
    /// database is now, to where the update would take it.
    /// </summary>
    /// <remarks>
    /// One rule covers all three routes, because <c>migrations script</c> reverses itself when the
    /// range runs backwards — a <c>from</c> later than <c>to</c> produces the Down SQL, which is
    /// exactly what a rollback runs. So the start is always the last migration known to be applied,
    /// and "0" — EF's name for the empty database — when none is.
    /// </remarks>
    /// <returns>
    /// <c>Uncertain</c> is set when any migration's applied state is unknown, which makes the start a
    /// best guess rather than a fact. The caller says so on the preview rather than hiding it.
    /// </returns>
    private (string From, string? To, bool Uncertain) PreviewRange(string? targetMigration)
    {
        var lastApplied = _ordered.LastOrDefault(m => m.State == MigrationState.Applied)?.Name;
        var uncertain = _ordered.Any(m => m.State == MigrationState.Unknown);

        return (lastApplied ?? "0", targetMigration, uncertain);
    }

    /// <summary>
    /// Describes exactly what a database update would do. Rolling anything back is called out
    /// separately from applying forward, because only one of them destroys data.
    /// </summary>
    private ConfirmRequest BuildUpdateConfirmation(string? targetMigration)
    {
        var where = _target()?.Context is { Length: > 0 } context
            ? $"the database for {context}"
            : "the database";

        if (targetMigration == "0")
        {
            var applied = AppliedCount > 0 ? $"{AppliedCount} applied migration(s)" : "every migration";
            return new ConfirmRequest(
                "Revert all migrations",
                $"Revert {applied} on {where}, taking it back to an empty schema?",
                "Revert all",
                "Every migration's Down method runs. Data in the tables they created is lost.");
        }

        // Rows after the target that are applied, or whose state we do not know, would be undone.
        // With Unknown state — an offline list — we cannot tell, so we warn rather than guess in the
        // risky direction.
        var index = targetMigration is null ? _ordered.Count - 1 : IndexOf(targetMigration);
        var undone = index < 0
            ? []
            : _ordered.Skip(index + 1).Where(m => m.State != MigrationState.Pending).ToList();

        if (undone.Count > 0)
        {
            var uncertain = undone.Any(m => m.State == MigrationState.Unknown);
            return new ConfirmRequest(
                "Revert migrations",
                $"Roll {where} back to '{targetMigration}'?",
                "Roll back",
                uncertain
                    ? $"{undone.Count} later migration(s) would be reverted if they have been applied. "
                      + "The applied state is unknown because the list was loaded without a database "
                      + "connection, so this is being confirmed to be safe."
                    : $"{undone.Count} later migration(s) will be reverted: "
                      + string.Join(", ", undone.Select(m => m.Name))
                      + ". Their Down methods run and data in the tables they created is lost.");
        }

        // Forward only. Still confirmed, but the wording says so rather than warning about loss.
        var applying = (index < 0 ? _ordered : _ordered.Take(index + 1))
            .Where(m => m.State != MigrationState.Applied)
            .ToList();

        var detail = applying.Count switch
        {
            0 => "Nothing is outstanding, so this should make no changes.",
            _ when applying.Any(m => m.State == MigrationState.Unknown) =>
                $"Up to {applying.Count} migration(s) may be applied: "
                + string.Join(", ", applying.Select(m => m.Name))
                + ". The applied state is unknown because the list was loaded without a database "
                + "connection, so some may already be in place.",
            _ => $"{applying.Count} migration(s) will be applied: "
                 + string.Join(", ", applying.Select(m => m.Name)) + ".",
        };

        return new ConfirmRequest(
            "Apply migrations",
            targetMigration is null
                ? $"Apply all outstanding migrations to {where}?"
                : $"Update {where} to '{targetMigration}'?",
            "Apply",
            detail);
    }

    private int IndexOf(string migrationName)
    {
        for (var i = 0; i < _ordered.Count; i++)
        {
            if (string.Equals(_ordered[i].Name, migrationName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_ordered[i].Id, migrationName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private async Task<bool> ConfirmedAsync(ConfirmRequest request)
    {
        // No dialog wired up means no confirmation happened, so the action must not proceed.
        if (ConfirmAsync is null)
        {
            _session.StatusMessage = "Cannot confirm this action.";
            return false;
        }

        return await ConfirmAsync(request);
    }

    private async Task LoadAsync(bool noBuildFirst)
    {
        var target = _target();
        if (target is null)
        {
            _session.StatusMessage = "Select a migrations project and context first.";
            return;
        }

        ConnectionWarning = null;

        // Only override the Skip build checkbox when this mode specifically demands --no-build.
        var attempt = await _session.RunAsync(
            EfArgs.MigrationsList(target with { NoBuild = noBuildFirst || target.NoBuild }, Offline),
            "Listing migrations");

        if (attempt is null)
        {
            IsStale = true;
            return;
        }

        if (!attempt.Success && noBuildFirst && !target.NoBuild)
        {
            _session.StatusMessage = "No usable existing build; building and retrying…";
            attempt = await _session.RunAsync(
                EfArgs.MigrationsList(target with { NoBuild = false }, Offline),
                "Listing migrations");

            if (attempt is null)
            {
                IsStale = true;
                return;
            }
        }

        // A failure while connected is usually an unreachable database. Rather than match on error
        // text, just try again offline: if that works, the build and model were fine and the database
        // was the problem. If it also fails, the original error is the real one.
        if (!attempt.Success && !Offline)
        {
            var offline = await _session.RunAsync(
                EfArgs.MigrationsList(target with { NoBuild = true }, noConnect: true),
                "Listing migrations without a database connection");

            if (offline is null)
            {
                IsStale = true;
                return;
            }

            if (offline.Success)
            {
                ConnectionWarning =
                    "The database could not be reached, so applied state is unknown. Migration names "
                    + "below come from the project, not the database.";
                Populate(offline);
                return;
            }
        }

        if (!attempt.Success)
        {
            IsStale = true;
            _session.ReportFailure(attempt, "Listing migrations failed — use Copy diagnostics.");
            return;
        }

        Populate(attempt);
    }

    private void Populate(EfResult result)
    {
        var migrations = EfJson.Migrations(result);
        if (migrations is null)
        {
            IsStale = true;
            _session.StatusMessage = "Could not read the migrations list — use Copy diagnostics.";
            return;
        }

        _ordered.Clear();
        _ordered.AddRange(migrations);
        IsStale = false;

        // The files behind the list may have changed since the SQL was generated from them.
        Detail.InvalidateSql();

        RebuildDisplay();
        _persist();

        if (!HasConnectionWarning)
        {
            _session.StatusMessage = Summary;
        }
    }

    /// <summary>Rebuilds the displayed list, keeping the selection where the migration still exists.</summary>
    private void RebuildDisplay()
    {
        var selectedId = SelectedMigration?.Id;

        // Number from the chronological list first, then order for display, so the numbers describe
        // the sequence migrations are applied in rather than wherever a row happens to sit.
        var rows = _ordered.Select((migration, index) => new MigrationRow(index + 1, migration));

        Migrations.Clear();
        foreach (var row in SortNewestFirst ? rows.Reverse() : rows)
        {
            Migrations.Add(row);
        }

        SelectedMigration = Migrations.FirstOrDefault(m => m.Id == selectedId);
        NotifyListChanged();
    }

    private void NotifyListChanged()
    {
        OnPropertyChanged(nameof(HasMigrations));
        OnPropertyChanged(nameof(ShowsStaleWarning));
        OnPropertyChanged(nameof(LastMigration));
        OnPropertyChanged(nameof(AppliedCount));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(NewMigrationNameError));
        OnPropertyChanged(nameof(HasNewMigrationNameError));
        NotifyCommandStates();
    }

    /// <summary>
    /// Called by the shell when the project or context selection changes. <see cref="IsReady"/> is
    /// derived from <see cref="_target"/>, which lives in the shell and raises nothing of its own,
    /// so without this the buttons keep reporting the readiness of the previous selection.
    /// </summary>
    public void NotifyTargetChanged() => NotifyCommandStates();

    private void NotifyCommandStates()
    {
        OnPropertyChanged(nameof(IsReady));
        RefreshCommand.NotifyCanExecuteChanged();
        AddCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        UpdateToLatestCommand.NotifyCanExecuteChanged();
        UpdateToSelectedCommand.NotifyCanExecuteChanged();
        RevertAllCommand.NotifyCanExecuteChanged();
        DropDatabaseCommand.NotifyCanExecuteChanged();
        Detail.NotifyTargetChanged();
    }

    partial void OnSortNewestFirstChanged(bool value)
    {
        _display.SortNewestFirst = value;
        OnPropertyChanged(nameof(SortGlyph));
        OnPropertyChanged(nameof(SortTooltip));
        RebuildDisplay();
        _persist();
    }

    partial void OnConnectionWarningChanged(string? value) =>
        OnPropertyChanged(nameof(HasConnectionWarning));

    partial void OnIsStaleChanged(bool value) => OnPropertyChanged(nameof(ShowsStaleWarning));

    partial void OnSelectedMigrationChanged(MigrationRow? value)
    {
        UpdateToSelectedCommand.NotifyCanExecuteChanged();
        Detail.Show(value);
    }

    partial void OnNewMigrationNameChanged(string value)
    {
        OnPropertyChanged(nameof(NewMigrationNameError));
        OnPropertyChanged(nameof(HasNewMigrationNameError));
        AddCommand.NotifyCanExecuteChanged();
    }

    partial void OnRefreshModeChanged(DiscoveryMode value) => _persist();

    partial void OnOfflineChanged(bool value) => _persist();
}
