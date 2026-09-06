using System;
using System.Threading.Tasks;
using EfAssist.Core;

namespace EfAssist.App.ViewModels;

/// <summary>
/// The workspace's "Don't build" option is honoured silently on every command that only reads —
/// listing migrations, reading context details, checking for pending model changes. That is what the
/// option is for, and a stale read is fixed by pressing refresh.
/// </summary>
/// <remarks>
/// <para>
/// Commands that write are different. <c>database update</c> applies migrations from the last
/// compiled assembly to a live database, <c>migrations remove</c> deletes files and rewrites the
/// model snapshot, <c>database drop</c> resolves a connection string before destroying what is on the
/// end of it, and a bundle is a file that leaves the machine entirely. Stale output does not fail
/// loudly on any of them — it quietly does the wrong thing — so each one asks first.
/// </para>
/// <para>
/// Two shapes, because the actions differ in whether they already have a dialog. An action that is
/// confirmed anyway gains a tick box on the dialog it already shows
/// (<see cref="WithBuildOption"/>); there is no case for two modals in a row. An action with no
/// confirmation of its own, or one whose confirmation depends on a command that has to run first,
/// gets a question of its own (<see cref="AskAsync"/>).
/// </para>
/// </remarks>
public static class NoBuildPrompt
{
    private const string OptionLabel =
        "Build the project first, ignoring this workspace\u2019s \u201cDon\u2019t build\u201d option";

    private const string Warning =
        "This workspace has \u201cDon\u2019t build\u201d set, so it uses the last compiled version instead of the current code." +
        " Outdated output does not show obvious issues, but can lead to unexpected behavior.";

    /// <summary>
    /// Adds the tick box, and the explanation behind it, to a confirmation the action was going to
    /// show anyway. Returns the request untouched when the workspace builds normally, so the common
    /// path gains nothing at all.
    /// </summary>
    public static ConfirmRequest WithBuildOption(this ConfirmRequest request, EfTarget target) =>
        !target.NoBuild
            ? request
            : request with
            {
                // The action's own detail stays first: it is what the user came to the dialog to
                // read, and this is a footnote on how the action runs rather than what it does.
                Detail = request.HasDetail ? request.Detail + "\n\n" + Warning : Warning,
                OptionText = OptionLabel,

                // Ticked by default. Enter lands on Cancel, so the tick box only matters once the
                // user has chosen to go ahead — at which point the safe answer should be the one
                // already selected.
                OptionChecked = true,
            };

    /// <summary>
    /// The target to run with, once the dialog has been answered. Clears <c>NoBuild</c> for this one
    /// run when the tick box was left on; the workspace's saved option is never touched, because
    /// answering a question about one action must not quietly rewrite a preference.
    /// </summary>
    public static EfTarget ResolvedTarget(this ConfirmRequest request, EfTarget target) =>
        request.HasOption && request.OptionChecked ? target with { NoBuild = false } : target;

    /// <summary>
    /// Asks as a dialog of its own, for an action that has no confirmation to hang a tick box on.
    /// </summary>
    /// <param name="consequence">What a stale build costs for this particular action.</param>
    /// <returns>
    /// The target to run with, or null if the user cancelled. Returns the target unchanged, and asks
    /// nothing, when the workspace builds normally.
    /// </returns>
    public static async Task<EfTarget?> AskAsync(
        EfTarget target,
        Func<ConfirmRequest, Task<bool>> confirm,
        string title,
        string confirmText,
        string consequence)
    {
        if (!target.NoBuild)
        {
            return target;
        }

        // Not destructive in itself: this question is only about how the action runs. A red button
        // that does not mean danger teaches people to click red buttons.
        var request = new ConfirmRequest(title, consequence, confirmText) { IsDestructive = false }
            .WithBuildOption(target);

        return await confirm(request) ? request.ResolvedTarget(target) : null;
    }
}
