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
