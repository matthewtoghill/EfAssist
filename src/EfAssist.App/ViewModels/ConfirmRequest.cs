using System;
using System.Threading.Tasks;

namespace EfAssist.App.ViewModels;

/// <summary>
/// A destructive action awaiting confirmation.
/// </summary>
/// <param name="Title">Window title, e.g. "Drop database".</param>
/// <param name="Message">What is about to happen, naming the exact target.</param>
/// <param name="ConfirmText">Label for the confirming button. Should be a verb, not "OK".</param>
/// <param name="Detail">Optional extra consequence, shown in a warning style.</param>
/// <param name="RequiredTypedValue">
/// When set, the user must type this exactly before the confirm button enables. Used for dropping a
/// database, where a misplaced click is unrecoverable.
/// </param>
public sealed record ConfirmRequest(
    string Title,
    string Message,
    string ConfirmText,
    string? Detail = null,
    string? RequiredTypedValue = null)
{
    /// <summary>
    /// Generates and shows the SQL this action would run, on demand. Null when there is nothing to
    /// preview — dropping a database runs no migration SQL, so it has none — and the button is hidden
    /// in that case. An init-only property rather than a constructor parameter because it is a
    /// behaviour the caller attaches, not part of what the dialog says; every caller builds its
    /// request first and adds this with a <c>with</c> expression.
    /// </summary>
    /// <remarks>
    /// Generating costs a <c>dotnet ef migrations script</c> run, which builds. That is the whole
    /// reason this is a button the user presses rather than something the dialog does as it opens.
    /// </remarks>
    public Func<Task>? PreviewAsync { get; init; }

    /// <summary>
    /// Label for an optional tick box in the dialog, or null for no tick box. It exists because some
    /// questions have three answers — do it this way, do it that way, or do not do it — and the
    /// dialog answers with a bool. The two ways ride on <see cref="OptionChecked"/> and Cancel stays
    /// the third, rather than every caller of <c>ConfirmAsync</c> changing shape for one of them.
    /// </summary>
    public string? OptionText { get; init; }

    /// <summary>
    /// The tick box's state: seeded by the caller as the recommended answer, written by the dialog,
    /// and read back by the caller once it closes. Deliberately mutable on an otherwise immutable
    /// record — it is the return channel for the tick box, and nothing else reads it, so there is no
    /// change notification to keep in step.
    /// </summary>
    public bool OptionChecked { get; set; } = true;

    /// <summary>
    /// Whether the confirming button is styled as destructive. True for everything that was here
    /// first — dropping a database, reverting every migration. A question that only asks how to do
    /// something harmless sets this false, because a red button that does not mean danger teaches
    /// people to click red buttons.
    /// </summary>
    public bool IsDestructive { get; init; } = true;

    public bool HasOption => !string.IsNullOrEmpty(OptionText);

    public bool RequiresTyping => !string.IsNullOrEmpty(RequiredTypedValue);

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    public bool HasPreview => PreviewAsync is not null;

    /// <summary>
    /// Whether what the user typed unlocks the action. Case-sensitive and exact, because the point of
    /// the gate is that it cannot be satisfied by accident. A request with no required value is not
    /// gated at all — callers must refuse to build one rather than pass an empty string, which is why
    /// the drop path bails out when it cannot determine the database name.
    /// </summary>
    public bool IsSatisfiedBy(string? typed) =>
        !RequiresTyping || string.Equals(typed?.Trim(), RequiredTypedValue, StringComparison.Ordinal);
}
