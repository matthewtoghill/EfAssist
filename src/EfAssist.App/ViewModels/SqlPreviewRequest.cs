namespace EfAssist.App.ViewModels;

/// <summary>
/// SQL to show read-only in its own window, generated on demand from a confirmation dialog.
/// </summary>
/// <param name="Title">Window title, naming the action the SQL belongs to.</param>
/// <param name="Sql">The script itself, exactly as <c>dotnet ef</c> wrote it.</param>
/// <param name="Path">Where it was written, so it can be opened in an external editor.</param>
/// <param name="Caveat">
/// Set when the script may not be exactly what the run will execute — the applied state was unknown,
/// so the starting point had to be assumed. Null when the range is certain.
/// </param>
/// <param name="Wrap">The app-wide SQL wrap preference, so the preview matches the Script tab.</param>
/// <param name="ShowLineNumbers">The app-wide line-number preference, for the same reason.</param>
public sealed record SqlPreviewRequest(
    string Title,
    string Sql,
    string Path,
    string? Caveat = null,
    bool Wrap = false,
    bool ShowLineNumbers = true)
{
    public bool HasCaveat => !string.IsNullOrEmpty(Caveat);

    /// <summary>
    /// A generated script with no statements in it. EF writes a header even for an empty range, so
    /// this is about there being nothing to read rather than about the file being zero bytes.
    /// </summary>
    public bool IsEmpty => Sql.Trim().Length == 0;
}
