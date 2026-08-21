using System;
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
/// The Tools tab: cross-cutting checks that do not belong to one context's migration list. Starts
/// with the pending-model-changes check; more checks can join it here later.
/// </summary>
public partial class ToolsViewModel : ObservableObject
{
    private readonly CommandSession _session;
    private readonly Func<EfTarget?> _target;

    public ToolsViewModel(CommandSession session, Func<EfTarget?> target)
    {
        _session = session;
        _target = target;

        _session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CommandSession.IsRunning))
            {
                CheckPendingModelChangesCommand.NotifyCanExecuteChanged();
            }
        };
    }

    [ObservableProperty]
    private ModelCheckState _modelCheckState = ModelCheckState.Unknown;

    public bool IsReady => !_session.IsRunning && _target() is not null;

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
    /// Called by the shell when the project or context selection changes, and whenever the
    /// migrations project or context changes underneath a stale result — a check against a
    /// different context is not worth keeping.
    /// </summary>
    public void NotifyTargetChanged()
    {
        ModelCheckState = ModelCheckState.Unknown;
        OnPropertyChanged(nameof(IsReady));
        CheckPendingModelChangesCommand.NotifyCanExecuteChanged();
    }

    public void Clear() => ModelCheckState = ModelCheckState.Unknown;
}
