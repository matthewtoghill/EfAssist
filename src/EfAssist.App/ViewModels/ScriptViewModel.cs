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

/// <summary>Which migrations a generated script should cover.</summary>
public enum ScriptRange
{
    /// <summary>Everything, from an empty database to the latest migration. The CLI's own default.</summary>
    All,

    /// <summary>From the last applied migration to the latest — the deployment case.</summary>
    Pending,

    /// <summary>Whatever the two pickers say.</summary>
    Custom,
}

/// <summary>
/// The Script tab: turn a range of migrations into SQL. Generates to a file rather than capturing
/// stdout, so the SQL is byte-exact, then reads it back for the viewer.
/// </summary>
public partial class ScriptViewModel : ObservableObject
{
    /// <summary>Shown in the From picker for "before any migration", which EF spells "0".</summary>
    public const string FromBeginning = "0 (empty database)";

    /// <summary>Shown in the To picker for "as far as the migrations go".</summary>
    public const string ToLatest = "(latest)";

    private readonly CommandSession _session;
    private readonly Func<EfTarget?> _target;
    private readonly Func<IReadOnlyList<MigrationInfo>> _migrations;
    private readonly Action _persist;

    /// <summary>
    /// Whether the shared "Idempotent" option, which lives in the workspace options pane rather than
    /// on this tab, is currently ticked. Shared with the migration detail pane's SQL preview, so both
    /// honour the same flag and the same provider gate rather than keeping two independent ones.
    /// </summary>
    private readonly Func<bool> _idempotentRequested;

    /// <summary>Called when a provider probe finds the ticked option is not actually usable.</summary>
    private readonly Action _onIdempotentUnsupported;

    /// <summary>Provider details per context name, so the probe runs once rather than per visit.</summary>
    private readonly Dictionary<string, DbContextDetails> _providerCache = new(StringComparer.Ordinal);

    /// <summary>Stops the suggested filename overwriting a name the user typed themselves.</summary>
    private bool _fileNameEdited;

    private bool _updatingFileName;

    public ScriptViewModel(
        CommandSession session,
        Func<EfTarget?> target,
        Func<IReadOnlyList<MigrationInfo>> migrations,
        Action persist,
        Func<bool>? idempotentRequested = null,
        Action? onIdempotentUnsupported = null)
    {
        _session = session;
        _target = target;
        _migrations = migrations;
        _persist = persist;
        _idempotentRequested = idempotentRequested ?? (() => false);
        _onIdempotentUnsupported = onIdempotentUnsupported ?? (() => { });

        _session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CommandSession.IsRunning))
            {
                NotifyCommandStates();
            }
        };
    }

    // ---- Supplied by the view ----

    public Func<ConfirmRequest, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>Save As dialog. Returns the chosen path, or null if cancelled.</summary>
    public Func<string, string?, Task<string?>>? PickSaveFileAsync { get; set; }

    public Func<Task<string?>>? PickFolderAsync { get; set; }

    public Func<string, Task>? OpenFileAsync { get; set; }

    public Func<string, Task>? RevealFileAsync { get; set; }

    // ---- Range ----

    public IReadOnlyList<ScriptRange> Ranges { get; } = Enum.GetValues<ScriptRange>();

    [ObservableProperty]
    private ScriptRange _range = ScriptRange.All;

    public ObservableCollection<string> FromOptions { get; } = [FromBeginning];

    public ObservableCollection<string> ToOptions { get; } = [ToLatest];

    [ObservableProperty]
    private string _selectedFrom = FromBeginning;

    [ObservableProperty]
    private string _selectedTo = ToLatest;

    public bool IsCustomRange => Range == ScriptRange.Custom;

    // ---- Options ----

    /// <summary>
    /// Null until the provider has been probed. Unknown providers get the benefit of the doubt: it is
    /// better to attempt and surface a clear error than to grey out a box that would have worked.
    /// </summary>
    [ObservableProperty]
    private DbContextDetails? _providerDetails;

    public bool CanUseIdempotent => ProviderDetails?.SupportsIdempotentScripts ?? true;

    public string IdempotentTooltip => CanUseIdempotent
        ? "Generate a script that checks the migrations history table, so it is safe to run more than once."
        : $"{ProviderDetails?.ProviderName} does not support idempotent scripts.";

    // ---- Destination ----

    /// <summary>Configured folder for generated scripts. Empty means ask with a Save As dialog.</summary>
    [ObservableProperty]
    private string _outputFolder = "";

    [ObservableProperty]
    private string _fileName = "";

    private string? _lastSaveAsFolder;

    public bool UsesConfiguredFolder => OutputFolder.Trim().Length > 0;

    public string DestinationHint => UsesConfiguredFolder
        ? $"Scripts are written to {OutputFolder.Trim()}."
        : "No folder is set, so you will be asked where to save each script.";

    // ---- Result ----

    [ObservableProperty]
    private string _sql = "";

    [ObservableProperty]
    private string? _generatedPath;

    /// <summary>
    /// True when the SQL on screen came from an earlier run and something has happened since that
    /// could invalidate it — a failed generation, or a migrations list that is itself out of date.
    /// The SQL is kept, because a stale script is still readable; it just stops looking current.
    /// </summary>
    [ObservableProperty]
    private bool _isStale;

    public bool HasScript => !string.IsNullOrEmpty(GeneratedPath);

    public bool HasSql => Sql.Length > 0;

    public bool ShowsStaleWarning => IsStale && HasSql;

    public bool IsReady => !_session.IsRunning && _target() is not null;

    public bool HasMigrations => _migrations().Count > 0;

    /// <summary>Warns that a Pending range cannot be trusted when applied state was never fetched.</summary>
    public string? RangeWarning
    {
        get
        {
            if (Range != ScriptRange.Pending)
            {
                return null;
            }

            var migrations = _migrations();
            if (migrations.Count == 0)
            {
                return "The migrations list is empty — refresh it on the Migrations tab first.";
            }

            return migrations.Any(m => m.State == MigrationState.Unknown)
                ? "Applied state is unknown, so \"pending\" is a guess. Refresh the list with a "
                  + "database connection for an accurate range."
                : null;
        }
    }

    public bool HasRangeWarning => RangeWarning is not null;

    // ---- Lifecycle ----

    /// <summary>
    /// Called when the tab becomes visible. The provider probe builds, so it is deliberately not done
    /// on every context change — a workspace that never generates a script never pays for it.
    /// </summary>
    public async Task OnActivatedAsync()
    {
        RefreshOptions();
        await EnsureProviderKnownAsync();
    }

    /// <summary>
    /// Probes the provider if it is not already known. Public so the migration detail pane can share
    /// the same probe and cache rather than running its own — the capability depends only on the
    /// context, which the two views have in common.
    /// </summary>
    public Task EnsureProviderKnownAsync() => EnsureProviderKnownAsyncCore();

    public void Restore(WorkspaceSettings saved)
    {
        OutputFolder = saved.ScriptOutputFolder ?? "";
        _lastSaveAsFolder = saved.LastSaveAsFolder;
        Sql = "";
        GeneratedPath = null;
        IsStale = false;
        _fileNameEdited = false;
        RefreshOptions();
    }

    public void Store(WorkspaceSettings saved)
    {
        var folder = OutputFolder.Trim();
        saved.ScriptOutputFolder = folder.Length == 0 ? null : folder;
        saved.LastSaveAsFolder = _lastSaveAsFolder;
    }

    public void Clear()
    {
        Sql = "";
        GeneratedPath = null;
        IsStale = false;
        ProviderDetails = null;
        _providerCache.Clear();
        _fileNameEdited = false;
        RefreshOptions();
    }

    /// <summary>Rebuilds the pickers from the current migrations list, keeping valid selections.</summary>
    public void RefreshOptions()
    {
        var names = _migrations().Select(m => m.Name).ToList();

        Replace(FromOptions, [FromBeginning, .. names]);
        Replace(ToOptions, [.. names, ToLatest]);

        // A migration that has since been removed cannot stay selected.
        if (!FromOptions.Contains(SelectedFrom))
        {
            SelectedFrom = FromBeginning;
        }

        if (!ToOptions.Contains(SelectedTo))
        {
            SelectedTo = ToLatest;
        }

        OnPropertyChanged(nameof(HasMigrations));
        OnPropertyChanged(nameof(RangeWarning));
        OnPropertyChanged(nameof(HasRangeWarning));
        UpdateSuggestedFileName();

        static void Replace(ObservableCollection<string> options, IReadOnlyList<string> values)
        {
            options.Clear();
            foreach (var value in values)
            {
                options.Add(value);
            }
        }
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

        var (from, to) = ResolveRange();

        var path = await ResolveOutputPathAsync();
        if (path is null)
        {
            return;
        }

        var result = await _session.RunAsync(
            EfArgs.MigrationsScript(target, path, from, to, _idempotentRequested() && CanUseIdempotent),
            "Generating SQL script");

        if (result is null)
        {
            return;
        }

        if (!result.Success)
        {
            // Whatever is in the viewer is from a previous run and no longer reflects the choices
            // on screen, so it is marked rather than left looking like the result of this attempt.
            IsStale = true;
            _session.ReportFailure(result, "Could not generate the script.");
            return;
        }

        IsStale = false;
        GeneratedPath = path;
        LoadGeneratedSql(path);
        _persist();
    }

    [RelayCommand(CanExecute = nameof(HasScript))]
    private async Task OpenAsync()
    {
        if (OpenFileAsync is not null && GeneratedPath is not null)
        {
            await OpenFileAsync(GeneratedPath);
        }
    }

    [RelayCommand(CanExecute = nameof(HasScript))]
    private async Task RevealAsync()
    {
        if (RevealFileAsync is not null && GeneratedPath is not null)
        {
            await RevealFileAsync(GeneratedPath);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSql))]
    private async Task CopySqlAsync()
    {
        if (_session.CopyToClipboardAsync is null)
        {
            return;
        }

        await _session.CopyToClipboardAsync(Sql);
        _session.StatusMessage = "SQL copied to the clipboard.";
    }

    [RelayCommand]
    private async Task BrowseOutputFolderAsync()
    {
        var folder = PickFolderAsync is null ? null : await PickFolderAsync();
        if (folder is not null)
        {
            OutputFolder = folder;
        }
    }

    [RelayCommand]
    private void ClearOutputFolder() => OutputFolder = "";

    [RelayCommand]
    private void ResetFileName()
    {
        _fileNameEdited = false;
        UpdateSuggestedFileName();
    }

    // ---- Work ----

    /// <summary>Translates the chosen range into the arguments EF expects.</summary>
    /// <returns>Null <c>from</c> means from the beginning; null <c>to</c> means the latest.</returns>
    private (string? From, string? To) ResolveRange()
    {
        switch (Range)
        {
            case ScriptRange.Pending:
                // From the last applied migration forward. Nothing applied means from the beginning.
                var lastApplied = _migrations()
                    .LastOrDefault(m => m.State == MigrationState.Applied)?.Name;
                return (lastApplied, null);

            case ScriptRange.Custom:
                var from = SelectedFrom == FromBeginning ? null : SelectedFrom;
                var to = SelectedTo == ToLatest ? null : SelectedTo;

                // EF takes these positionally, so a "to" without a "from" would be read as a "from".
                // "0" is what EF calls the empty database.
                return (from ?? (to is null ? null : "0"), to);

            default:
                return (null, null);
        }
    }

    private async Task<string?> ResolveOutputPathAsync()
    {
        var name = FileName.Trim();
        if (name.Length == 0)
        {
            name = SuggestFileName();
        }

        if (!name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
        {
            name += ".sql";
        }

        if (!UsesConfiguredFolder)
        {
            // The OS Save As dialog does its own overwrite prompt, so no second confirmation here.
            var chosen = PickSaveFileAsync is null ? null : await PickSaveFileAsync(name, _lastSaveAsFolder);
            if (chosen is not null)
            {
                _lastSaveAsFolder = Path.GetDirectoryName(chosen);
            }

            return chosen;
        }

        var path = Path.Combine(OutputFolder.Trim(), name);

        if (File.Exists(path) && !await ConfirmedAsync(new ConfirmRequest(
                "Overwrite script",
                $"'{name}' already exists in the scripts folder. Replace it?",
                "Overwrite",
                "The existing file is replaced. Any hand edits to it are lost.")))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(OutputFolder.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _session.StatusMessage = $"Could not use the scripts folder: {ex.Message}";
            return null;
        }

        return path;
    }

    private void LoadGeneratedSql(string path)
    {
        try
        {
            Sql = File.ReadAllText(path);
            _session.StatusMessage = $"Wrote {Sql.Length:N0} characters to {path}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The command succeeded, so the file is there even if we cannot show it.
            Sql = "";
            _session.StatusMessage = $"Script written to {path}, but it could not be read back: {ex.Message}";
        }
    }

    private async Task EnsureProviderKnownAsyncCore()
    {
        var target = _target();
        var context = target?.Context;
        if (target is null || string.IsNullOrEmpty(context))
        {
            return;
        }

        if (_providerCache.TryGetValue(context, out var cached))
        {
            ProviderDetails = cached;
            return;
        }

        var result = await _session.RunAsync(EfArgs.DbContextInfo(target), "Reading provider details");
        if (result is null || !result.Success)
        {
            // Leave the checkbox enabled: an unknown provider is not a reason to block a valid option.
            return;
        }

        var details = EfJson.ContextDetails(result);
        if (details is not null)
        {
            _providerCache[context] = details;
            ProviderDetails = details;
        }
    }

    private async Task<bool> ConfirmedAsync(ConfirmRequest request)
    {
        if (ConfirmAsync is null)
        {
            _session.StatusMessage = "Cannot confirm this action.";
            return false;
        }

        return await ConfirmAsync(request);
    }

    private string SuggestFileName()
    {
        var (from, to) = ResolveRange();
        return ScriptFileName.Suggest(
            _target()?.Context,
            Range == ScriptRange.All ? null : from,
            to,
            _idempotentRequested() && CanUseIdempotent);
    }

    private void UpdateSuggestedFileName()
    {
        // Once the user types their own name, stop moving it under them.
        if (_fileNameEdited)
        {
            return;
        }

        _updatingFileName = true;
        try
        {
            FileName = SuggestFileName();
        }
        finally
        {
            _updatingFileName = false;
        }
    }

    /// <summary>
    /// Called by the shell when the project or context selection changes. <see cref="IsReady"/> is
    /// derived from <see cref="_target"/>, which lives in the shell and raises nothing of its own,
    /// so without this the buttons keep reporting the readiness of the previous selection.
    /// </summary>
    public void NotifyTargetChanged() => NotifyCommandStates();

    /// <summary>Called by the shell when the shared Idempotent option is toggled.</summary>
    public void NotifyIdempotentChanged() => UpdateSuggestedFileName();

    private void NotifyCommandStates()
    {
        OnPropertyChanged(nameof(IsReady));
        GenerateCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
        RevealCommand.NotifyCanExecuteChanged();
        CopySqlCommand.NotifyCanExecuteChanged();
    }

    // ---- Property change plumbing ----

    partial void OnRangeChanged(ScriptRange value)
    {
        OnPropertyChanged(nameof(IsCustomRange));
        OnPropertyChanged(nameof(RangeWarning));
        OnPropertyChanged(nameof(HasRangeWarning));
        UpdateSuggestedFileName();
    }

    partial void OnSelectedFromChanged(string value) => UpdateSuggestedFileName();

    partial void OnSelectedToChanged(string value) => UpdateSuggestedFileName();

    partial void OnProviderDetailsChanged(DbContextDetails? value)
    {
        OnPropertyChanged(nameof(CanUseIdempotent));
        OnPropertyChanged(nameof(IdempotentTooltip));

        // A provider that cannot do it must not leave the option ticked from a previous context.
        if (!CanUseIdempotent)
        {
            _onIdempotentUnsupported();
        }
    }

    partial void OnOutputFolderChanged(string value)
    {
        OnPropertyChanged(nameof(UsesConfiguredFolder));
        OnPropertyChanged(nameof(DestinationHint));
        _persist();
    }

    partial void OnFileNameChanged(string value)
    {
        if (!_updatingFileName)
        {
            _fileNameEdited = true;
        }
    }

    partial void OnGeneratedPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasScript));
        OpenCommand.NotifyCanExecuteChanged();
        RevealCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsStaleChanged(bool value) => OnPropertyChanged(nameof(ShowsStaleWarning));

    partial void OnSqlChanged(string value)
    {
        OnPropertyChanged(nameof(HasSql));
        OnPropertyChanged(nameof(ShowsStaleWarning));
        CopySqlCommand.NotifyCanExecuteChanged();
    }
}
