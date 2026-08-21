using System;

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
    public bool RequiresTyping => !string.IsNullOrEmpty(RequiredTypedValue);

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    /// <summary>
    /// Whether what the user typed unlocks the action. Case-sensitive and exact, because the point of
    /// the gate is that it cannot be satisfied by accident. A request with no required value is not
    /// gated at all — callers must refuse to build one rather than pass an empty string, which is why
    /// the drop path bails out when it cannot determine the database name.
    /// </summary>
    public bool IsSatisfiedBy(string? typed) =>
        !RequiresTyping || string.Equals(typed?.Trim(), RequiredTypedValue, StringComparison.Ordinal);
}
