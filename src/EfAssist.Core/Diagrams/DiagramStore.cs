namespace EfAssist.Core.Diagrams;

/// <summary>
/// A diagram as it survives between sessions: the extracted model, plus everything the user did to
/// it that a re-extraction would otherwise throw away.
/// </summary>
/// <remarks>
/// Mutable with settable properties rather than a record, because it is round-tripped through
/// <c>System.Text.Json</c> alongside the settings files and follows their conventions.
/// </remarks>
public sealed class SavedDiagram
{
    public DiagramModel? Model { get; set; }

    /// <summary>
    /// Hand-dragged node positions, keyed by <see cref="DiagramKind"/> name and then by entity name.
    /// </summary>
    /// <remarks>
    /// Per view, not shared. The two views put different rows in a node, so the same entity is a
    /// different height in each; one arrangement replayed in the other view overlaps its neighbours,
    /// and with the diagram locked there is no way to pull them apart again.
    /// </remarks>
    public Dictionary<string, Dictionary<string, DiagramPoint>> Positions { get; set; } = [];

    /// <summary>Locked by default: pan and zoom cannot lose work, dragging can.</summary>
    public bool Locked { get; set; } = true;

    public DiagramKind Kind { get; set; }

    public DiagramViewOptions? Options { get; set; }

    /// <summary>The positions for one view, or an empty map when that view has never been arranged.</summary>
    public Dictionary<string, DiagramPoint> PositionsFor(DiagramKind kind) =>
        Positions.TryGetValue(kind.ToString(), out var positions) ? positions : [];

    public void SetPositions(DiagramKind kind, IReadOnlyDictionary<string, DiagramPoint> positions) =>
        Positions[kind.ToString()] = new Dictionary<string, DiagramPoint>(
            positions, StringComparer.Ordinal);
}

/// <summary>
/// Reads and writes the saved diagram for one workspace and context.
/// </summary>
/// <remarks>
/// Under the app data folder beside the settings files, not the temp folder. A generated SQL script
/// is a throwaway and <see cref="MigrationFiles.ScriptCachePath"/> treats it as one; a diagram with a
/// hand-arranged layout in it is work, and the OS clearing it out would lose that work.
/// </remarks>
public static class DiagramStore
{
    /// <param name="root">
    /// The folder holding <c>settings.json</c> — <see cref="AppSettings.Root"/>. Null means nothing
    /// has a location yet, which is a first run rather than an error.
    /// </param>
    public static string? Path(string? root, string workspacePath, string? contextName)
    {
        if (root is null || string.IsNullOrWhiteSpace(workspacePath))
        {
            return null;
        }

        return System.IO.Path.Combine(
            root,
            "diagrams",
            SettingsStore.WorkspaceKey(workspacePath),
            Safe(contextName) + ".json");
    }

    /// <summary>
    /// The saved diagram, or null when there is none. Never throws — an unreadable or corrupt file
    /// means "regenerate", which is exactly what the empty state already offers.
    /// </summary>
    public static SavedDiagram? Load(string? root, string workspacePath, string? contextName)
    {
        var path = Path(root, workspacePath, contextName);
        return path is null ? null : SettingsStore.ReadOrDefault<SavedDiagram>(path);
    }

    /// <summary>
    /// Writes the diagram, or does nothing when there is nowhere to write it. Returns whether it was
    /// written, so a caller can say so rather than claiming a save that never happened.
    /// </summary>
    public static bool Save(
        string? root, string workspacePath, string? contextName, SavedDiagram diagram)
    {
        ArgumentNullException.ThrowIfNull(diagram);

        var path = Path(root, workspacePath, contextName);
        if (path is null)
        {
            return false;
        }

        try
        {
            SettingsStore.WriteAtomic(path, diagram);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A diagram that could not be cached is a lost convenience, not a failed operation. The
            // one on screen is still perfectly usable.
            return false;
        }
    }

    public static void Delete(string? root, string workspacePath, string? contextName)
    {
        var path = Path(root, workspacePath, contextName);
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing useful to do. A leftover file is only ever read after being overwritten.
        }
    }

    /// <summary>
    /// Whether the snapshot a diagram was built from has changed since. Re-hashing the file is exact,
    /// where a timestamp comparison would call a touched-but-identical file stale.
    /// </summary>
    /// <returns>
    /// True when the source has changed or has gone. A model with no recorded source is not stale —
    /// there is nothing to compare it against, and badging it would be a guess.
    /// </returns>
    public static bool IsStale(DiagramModel? model)
    {
        if (model is null || string.IsNullOrEmpty(model.SourcePath))
        {
            return false;
        }

        try
        {
            return !File.Exists(model.SourcePath)
                || ModelSnapshotParser.Hash(File.ReadAllText(model.SourcePath)) != model.SourceHash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable is not the same as changed, and claiming a diagram is out of date because a
            // file was briefly locked would cry wolf.
            return false;
        }
    }

    /// <summary>
    /// A context name as a file name. Contexts are C# type names so this rarely does anything, but a
    /// fully-qualified name with a namespace is what <c>dbcontext list</c> returns and a path
    /// separator in it would write outside the folder.
    /// </summary>
    private static string Safe(string? contextName)
    {
        var name = string.IsNullOrWhiteSpace(contextName) ? "default" : contextName;
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
        return safe.Length > 80 ? safe[..80] : safe;
    }
}
