using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EfAssist.Core;

namespace EfAssist.App.ViewModels;

/// <summary>Outcome of the last pending-model-changes check.</summary>
public enum ModelCheckState
{
    /// <summary>Never run, or the workspace/context changed since the last run.</summary>
    Unknown,

    UpToDate,

    Pending,
}

/// <summary>
/// The Tools tab: cross-cutting checks and whole-workspace actions that do not belong to one
/// context's migration list — the pending-model-changes check, and producing a migrations bundle.
/// </summary>
public partial class ToolsViewModel : ObservableObject
{
    private readonly CommandSession _session;
    private readonly Func<EfTarget?> _target;

    /// <summary>Shown in the runtime picker for "whatever this machine is", which EF spells as no RID.</summary>
    public const string RuntimeThisMachine = "(this machine)";

    /// <summary>Where the last bundle was saved, so the next Save As opens there. Session-only.</summary>
    /// <remarks>
    /// ponytail: not persisted, unlike the script tab's LastSaveAsFolder. Bundling is a
    /// once-a-deployment action rather than a loop; persist it if someone is retyping the path.
    /// </remarks>
    private string? _lastBundleFolder;

    public ToolsViewModel(CommandSession session, Func<EfTarget?> target)
    {
        _session = session;
        _target = target;

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

    public Func<string, Task>? RevealFileAsync { get; set; }

    [ObservableProperty]
    private ModelCheckState _modelCheckState = ModelCheckState.Unknown;

    public bool IsReady => !_session.IsRunning && _target() is not null;

    // ---- Migrations bundle ----

    /// <summary>
    /// The RIDs offered in the picker. Deliberately a short list of the ones a deployment actually
    /// lands on rather than the whole RID graph, and the box is editable so an unlisted one can be
    /// typed — the CLI, not this list, decides what is valid.
    /// </summary>
    public IReadOnlyList<string> TargetRuntimes { get; } =
    [
        RuntimeThisMachine,
        "win-x64",
        "win-arm64",
        "linux-x64",
        "linux-arm64",
        "linux-musl-x64",
        "osx-x64",
        "osx-arm64",
    ];

    [ObservableProperty]
    private string _selectedTargetRuntime = RuntimeThisMachine;

    /// <summary>
    /// Bundle the .NET runtime too. Off by default: a server that already runs the application has a
    /// runtime on it, and a self-contained bundle is an order of magnitude larger.
    /// </summary>
    [ObservableProperty]
    private bool _bundleSelfContained;

    /// <summary>The bundle written by the last successful run, or null. Cleared when the target changes.</summary>
    [ObservableProperty]
    private string? _bundlePath;

    public bool HasBundle => BundlePath is not null;

    /// <summary>The picker value as EF wants it: null for "this machine".</summary>
    private string? Runtime =>
        SelectedTargetRuntime == RuntimeThisMachine || string.IsNullOrWhiteSpace(SelectedTargetRuntime)
            ? null
            : SelectedTargetRuntime.Trim();

    /// <summary>
    /// Spells out what the chosen combination needs on the far end. That difference is the whole
    /// reason a bundle is or is not usable when it gets there, and it is not visible from the file.
    /// </summary>
    public string BundleHint
    {
        get
        {
            var built = $"Built for {Runtime ?? "this machine"}.";
            return BundleSelfContained
                ? $"The bundle carries its own .NET runtime, so the target machine needs nothing installed. {built}"
                : $"The target machine needs a matching .NET runtime already installed. {built}";
        }
    }

    [RelayCommand(CanExecute = nameof(IsReady))]
    private async Task CheckPendingModelChangesAsync()
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
            _session.StatusMessage = "No changes have been made to the model since the last migration.";
            return;
        }

        if (EfDiagnostics.IsPendingModelChanges(result))
        {
            ModelCheckState = ModelCheckState.Pending;
            _session.StatusMessage = "The model has changes that are not captured in a migration.";
            return;
        }

        ModelCheckState = ModelCheckState.Unknown;
        _session.ReportFailure(result, "Could not check for pending model changes.");
    }

    /// <summary>
    /// Produces a migrations bundle — a standalone executable that applies every migration on a
    /// machine with no SDK and no source. Writes wherever the Save As dialog says; there is no
    /// configured folder for these, because a bundle is produced for a deployment rather than
    /// repeatedly into the same place the way a script is.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsReady))]
    private async Task CreateBundleAsync()
    {
        var target = _target();
        if (target is null)
        {
            return;
        }

        if (PickSaveFileAsync is null)
        {
            _session.StatusMessage = "Cannot ask where to save the bundle.";
            return;
        }

        // Asked before the file dialog: it decides how the command runs, and backing out of it
        // should not have cost the user a filename first.
        var resolved = await ResolveNoBuildAsync(target);
        if (resolved is null)
        {
            return;
        }

        target = resolved;
        var runtime = Runtime;

        // The OS dialog does its own overwrite prompt and EfArgs passes --force, so a replacement
        // the user has already agreed to is not asked about twice.
        var path = await PickSaveFileAsync(BundleFileName.Suggest(target.Context, runtime), _lastBundleFolder);
        if (path is null)
        {
            return;
        }

        _lastBundleFolder = Path.GetDirectoryName(path);

        var result = await _session.RunAsync(
            EfArgs.MigrationsBundle(target, path, BundleSelfContained, runtime),
            "Creating migrations bundle");

        if (result is null)
        {
            return;
        }

        if (!result.Success)
        {
            // Whatever was offered before is not what this attempt produced, so it stops being offered.
            BundlePath = null;
            _session.ReportFailure(result, "Could not create the migrations bundle.");
            return;
        }

        BundlePath = path;
        _session.StatusMessage = $"Bundle written to {path}.";
    }

    /// <summary>
    /// Checks the workspace's "Don't build" option before bundling, and offers to ignore it.
    /// </summary>
    /// <remarks>
    /// Every other command honours <c>NoBuild</c> silently, and this one asks, because the artefacts
    /// differ in what a stale build costs. A stale <c>migrations list</c> is refreshed by pressing
    /// refresh; a stale bundle is a file that leaves the machine and applies the wrong migrations to
    /// a database somewhere the app cannot see. Only asked when the option is actually set — the
    /// normal path gets no extra dialog.
    /// </remarks>
    /// <returns>
    /// The target to run with — the original, or one with <c>NoBuild</c> cleared — or null if the
    /// user cancelled.
    /// </returns>
    private async Task<EfTarget?> ResolveNoBuildAsync(EfTarget target)
    {
        if (!target.NoBuild)
        {
            return target;
        }

        if (ConfirmAsync is null)
        {
            _session.StatusMessage = "Cannot confirm this action.";
            return null;
        }

        var request = new ConfirmRequest(
            "Create bundle without building?",
            "This workspace has \u201cDon\u2019t build\u201d set, so the bundle would be made from whatever "
                + "was last compiled. If that output is out of date, the bundle applies the wrong "
                + "migrations on whichever machine it is run against.",
            "Create bundle",
            Detail: "Building first costs a moment now. A stale bundle costs it on the target database.")
        {
            IsDestructive = false,
            OptionText = "Build the project first, ignoring this workspace\u2019s \u201cDon\u2019t build\u201d option",

            // Ticked by default: the safe answer should be the one a distracted Enter gives.
            OptionChecked = true,
        };

        if (!await ConfirmAsync(request))
        {
            return null;
        }

        // Only ever clears the flag for this one run. The workspace's own setting is untouched —
        // answering a question about one bundle must not quietly rewrite a saved preference.
        return request.OptionChecked ? target with { NoBuild = false } : target;
    }

    [RelayCommand(CanExecute = nameof(HasBundle))]
    private async Task RevealBundleAsync()
    {
        if (RevealFileAsync is not null && BundlePath is not null)
        {
            await RevealFileAsync(BundlePath);
        }
    }

    /// <summary>
    /// Called by the shell when the project or context selection changes, and whenever the
    /// migrations project or context changes underneath a stale result — a check against a
    /// different context is not worth keeping, and neither is a bundle built from a different one.
    /// </summary>
    public void NotifyTargetChanged()
    {
        ModelCheckState = ModelCheckState.Unknown;
        BundlePath = null;
        OnPropertyChanged(nameof(IsReady));
        NotifyCommandStates();
    }

    public void Clear()
    {
        ModelCheckState = ModelCheckState.Unknown;
        BundlePath = null;
    }

    private void NotifyCommandStates()
    {
        CheckPendingModelChangesCommand.NotifyCanExecuteChanged();
        CreateBundleCommand.NotifyCanExecuteChanged();
        RevealBundleCommand.NotifyCanExecuteChanged();
    }

    // ---- Property change plumbing ----

    partial void OnBundlePathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasBundle));
        RevealBundleCommand.NotifyCanExecuteChanged();
    }

    partial void OnBundleSelfContainedChanged(bool value) => OnPropertyChanged(nameof(BundleHint));

    partial void OnSelectedTargetRuntimeChanged(string value) => OnPropertyChanged(nameof(BundleHint));
}
