namespace EfAssist.Core.Diagrams;

/// <summary>
/// Finds the model snapshot file for a <c>DbContext</c> inside the migrations project.
/// </summary>
/// <remarks>
/// Matching is on the <c>[DbContext(typeof(X))]</c> attribute inside the file, not on the file name.
/// EF names the file after the context, but renaming a context does not rename the file it already
/// generated, and a solution can hold several snapshots in one folder. The attribute is the
/// authoritative link; the file name is a convention.
/// </remarks>
public static class ModelSnapshotLocator
{
    /// <summary>Same exclusions as <see cref="MigrationFiles"/>: a build output holds stale copies.</summary>
    private static readonly string[] IgnoredDirectories = ["bin", "obj", ".git", "node_modules"];

    /// <param name="migrationsProjectPath">The project file or folder passed to <c>--project</c>.</param>
    /// <param name="contextName">
    /// The context to find, as short or fully-qualified name — <c>dotnet ef dbcontext list</c>
    /// returns the full name and the attribute carries the short one, so both have to work.
    /// </param>
    /// <returns>
    /// The snapshot path, or null when the project has no migrations for this context. Null is the
    /// empty state, not an error: a context with no migrations legitimately has no snapshot.
    /// </returns>
    public static string? Find(
        string migrationsProjectPath,
        string? contextName,
        CancellationToken cancellationToken = default)
    {
        var root = ProjectDirectory(migrationsProjectPath);
        if (root is null)
        {
            return null;
        }

        var wanted = ShortName(contextName);

        // A single snapshot with no context to match against is unambiguous, so it is returned rather
        // than refused — a workspace whose context dropdown has not been populated yet still draws.
        string? onlyCandidate = null;
        var candidates = 0;

        foreach (var file in Snapshots(root, cancellationToken))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var declared = ModelSnapshotParser.ContextNameFromSource(text);
            if (declared is null)
            {
                continue;
            }

            if (wanted is not null && declared == wanted)
            {
                return file;
            }

            candidates++;
            onlyCandidate = file;
        }

        return wanted is null && candidates == 1 ? onlyCandidate : null;
    }

    /// <summary>
    /// The snapshot as of one specific migration, from its <c>.Designer.cs</c>. Not used by the
    /// Diagrams tab yet — it is what a "model at migration X" or a diagram diff would read, and it
    /// costs nothing to expose while the file-finding code is here.
    /// </summary>
    public static string? FindForMigration(string migrationsProjectPath, string migrationId)
    {
        var source = MigrationFiles.FindSource(migrationsProjectPath, migrationId);
        if (source is null)
        {
            return null;
        }

        var designer = Path.ChangeExtension(source, null) + ".Designer.cs";
        return File.Exists(designer) ? designer : null;
    }

    /// <summary>Every <c>*ModelSnapshot.cs</c> under the project, build output excluded.</summary>
    private static IEnumerable<string> Snapshots(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<string> files;
        IEnumerable<string> children;
        try
        {
            files = Directory.EnumerateFiles(directory, "*ModelSnapshot.cs").ToList();
            children = Directory.EnumerateDirectories(directory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable directory skips itself rather than aborting the whole search, matching
            // MigrationFiles.Search.
            yield break;
        }

        foreach (var file in files)
        {
            yield return file;
        }

        foreach (var child in children)
        {
            if (IgnoredDirectories.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var found in Snapshots(child, cancellationToken))
            {
                yield return found;
            }
        }
    }

    private static string? ProjectDirectory(string migrationsProjectPath)
    {
        if (string.IsNullOrWhiteSpace(migrationsProjectPath))
        {
            return null;
        }

        var root = Directory.Exists(migrationsProjectPath)
            ? migrationsProjectPath
            : Path.GetDirectoryName(Path.GetFullPath(migrationsProjectPath));

        return root is not null && Directory.Exists(root) ? root : null;
    }

    private static string? ShortName(string? contextName)
    {
        if (string.IsNullOrWhiteSpace(contextName))
        {
            return null;
        }

        var dot = contextName.LastIndexOf('.');
        return dot < 0 ? contextName : contextName[(dot + 1)..];
    }
}
