using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
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
/// The result of <c>migrations has-pending-model-changes</c>, shared by every screen that shows it —
/// Migrations runs it after a list, Tools and Diagrams on request — so a check from any of them
/// updates all three rather than leaving one saying Unknown while another says Pending.
/// </summary>
public partial class ModelCheck(CommandSession session) : ObservableObject
{
    [ObservableProperty]
    private ModelCheckState _state = ModelCheckState.Unknown;

    public bool IsPending => State == ModelCheckState.Pending;

    /// <summary>
    /// Runs the check and records its outcome. Returns the result, or null when cancelled, so the
    /// caller can word its own status line and decide whether a failure is worth reporting.
    /// </summary>
    public async Task<EfResult?> RunAsync(EfTarget target)
    {
        State = ModelCheckState.Unknown;

        var result = await session.RunAsync(
            EfArgs.MigrationsHasPendingModelChanges(target),
            "Checking for pending model changes");

        State = result switch
        {
            { Success: true } => ModelCheckState.UpToDate,
            not null when EfDiagnostics.IsPendingModelChanges(result) => ModelCheckState.Pending,
            _ => ModelCheckState.Unknown,
        };

        return result;
    }

    public void Reset() => State = ModelCheckState.Unknown;

    partial void OnStateChanged(ModelCheckState value) => OnPropertyChanged(nameof(IsPending));
}
