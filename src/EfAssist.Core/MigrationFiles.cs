using System.Security.Cryptography;
using System.Text;

namespace EfAssist.Core;

/// <summary>
/// Locates the files behind a migration on disk, and decides where a throwaway per-migration script
/// is written.
/// </summary>
/// <remarks>
/// <c>migrations list --json</c> reports an id and a name but no path, and the CLI has no command
/// that will tell us where the file went — <c>--output-dir</c> is an input, never an output. So the
/// file is found by searching the migrations project for the one named after the migration id,
/// which is the convention EF has always generated and the only thing there is to go on.
/// </remarks>
public static class MigrationFiles
{
    /// <summary>Never worth searching, and a build output can contain a stale copy of the source.</summary>
    private static readonly string[] IgnoredDirectories = ["bin", "obj", ".git", "node_modules"];

    /// <summary>
    /// The migration's own <c>.cs</c> file — not the <c>.Designer.cs</c> sibling, which holds the
    /// model snapshot rather than the Up and Down methods.
    /// </summary>
    /// <param name="migrationsProjectPath">The project file passed to <c>--project</c>.</param>
    /// <param name="migrationId">EF's id, for example <c>20260818140933_InitialCreate</c>.</param>
    /// <returns>The full path, or null when nothing matches.</returns>
    public static string? FindSource(string migrationsProjectPath, string migrationId)
    {
        if (string.IsNullOrWhiteSpace(migrationsProjectPath) || string.IsNullOrWhiteSpace(migrationId))
        {
            return null;
        }

        var root = Directory.Exists(migrationsProjectPath)
            ? migrationsProjectPath
            : Path.GetDirectoryName(Path.GetFullPath(migrationsProjectPath));

        if (root is null || !Directory.Exists(root))
        {
            return null;
        }

        // ponytail: walks the project on every selection with no cache. Add one if a large project
        // makes the scan noticeable — against a warm OS cache it is not.
        var fileName = migrationId + ".cs";

        try
        {
            return Search(root, fileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The migration id a source file is named after: a 14-digit timestamp, an underscore and a name,
    /// for example <c>20260818140933_InitialCreate.cs</c>.
    /// </summary>
    /// <returns>
    /// The id, or null for anything else — the <c>.Designer.cs</c> sibling, the model snapshot, or
    /// any other file that happens to sit in the folder.
    /// </returns>
    public static string? IdFromPath(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var id = name[..^3];
        return id.Length > 15 && id[14] == '_' && id[..14].All(char.IsAsciiDigit) ? id : null;
    }

    /// <summary>
    /// Whether a path under <paramref name="root"/> passes through a directory the search never
    /// looks in — a build output, for instance, which writes files in bursts and holds stale copies.
    /// </summary>
    public static bool IsIgnored(string root, string path) =>
        Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .SkipLast(1)
            .Any(segment => IgnoredDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));

    /// <summary>The first file with this name, in <see cref="Walk"/> order.</summary>
    private static string? Search(string directory, string fileName) =>
        Walk(directory).FirstOrDefault(f => string.Equals(
            Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every migration source file in the project, keyed by migration id, from one walk rather than
    /// one <see cref="FindSource"/> per migration. Where two files share a name, the one
    /// <see cref="FindSource"/> would pick wins.
    /// </summary>
    /// <remarks>
    /// Keyed by file name, so it also holds non-migration files; look up by id and the rest are
    /// never seen.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> FindSources(string migrationsProjectPath)
    {
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var root = Directory.Exists(migrationsProjectPath)
            ? migrationsProjectPath
            : Path.GetDirectoryName(Path.GetFullPath(migrationsProjectPath));

        if (root is null || !Directory.Exists(root))
        {
            return sources;
        }

        foreach (var file in Walk(root))
        {
            if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            {
                sources.TryAdd(Path.GetFileNameWithoutExtension(file), file);
            }
        }

        return sources;
    }

    /// <summary>
    /// A directory's own files first, then each child in turn. Explicit rather than
    /// <see cref="SearchOption.AllDirectories"/>, so an unreadable subdirectory skips itself instead
    /// of aborting the whole walk, and so bin and obj are never descended into at all.
    /// </summary>
    private static IEnumerable<string> Walk(string directory)
    {
        string[] files, children;
        try
        {
            files = Directory.GetFiles(directory);
            children = Directory.GetDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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

            foreach (var file in Walk(child))
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// Where to generate the SQL for a single migration. Under the temp folder because the file is a
    /// means of reading the script, not a deliverable — <c>migrations script</c> can only write to a
    /// file, so viewing one requires producing one.
    /// </summary>
    /// <remarks>
    /// Keyed by a hash of the project and context so two workspaces with identically named
    /// migrations cannot read each other's SQL. The files are left behind deliberately: the OS
    /// clears its own temp folder, and a leftover is only ever re-read after being rewritten.
    /// </remarks>
    /// <param name="idempotent">
    /// Whether the script was (or would be) generated with <c>--idempotent</c>. Part of the path
    /// rather than just the in-memory cache key, so the two variants never share a file — without
    /// this, generating one after the other would silently overwrite the file that "Open file"
    /// still thinks holds the other one.
    /// </param>
    /// <param name="down">
    /// Whether the script rolls the migration back rather than applying it. Part of the path for the
    /// same reason as <paramref name="idempotent"/>: the two directions are different scripts for
    /// the same migration, and must not share a file.
    /// </param>
    public static string ScriptCachePath(
        string migrationsProjectPath,
        string? context,
        string migrationId,
        bool idempotent = false,
        bool down = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationId);

        var key = Key(Path.GetFullPath(migrationsProjectPath) + "|" + (context ?? ""));
        var fileName = migrationId
            + (down ? "_down" : "")
            + (idempotent ? "_idempotent" : "")
            + ".sql";
        return Path.Combine(Path.GetTempPath(), "EfAssist", "preview", key, fileName);
    }

    /// <summary>
    /// Where to generate the SQL a <c>database update</c> would run, for the preview offered on its
    /// confirmation. Shares <see cref="ScriptCachePath"/>'s folder and its per-workspace keying; only
    /// the file name differs, because a range is not a migration.
    /// </summary>
    /// <remarks>
    /// The <c>update_</c> prefix cannot collide with a migration's own file: a migration id starts
    /// with its timestamp. Never <c>--idempotent</c>, so there is no variant to keep apart —
    /// <c>database update</c> does not run idempotent SQL, and previewing SQL the run would not
    /// execute would defeat the point of the preview.
    /// </remarks>
    /// <param name="from">The migration the database is at, or <c>"0"</c> for an empty database.</param>
    /// <param name="to">The migration being moved to; null for the latest.</param>
    public static string UpdatePreviewPath(
        string migrationsProjectPath, string? context, string? from, string? to) =>
        ScriptCachePath(
            migrationsProjectPath,
            context,
            $"update_{Safe(from) ?? "0"}_to_{Safe(to) ?? "latest"}");

    /// <summary>
    /// Strips anything a file name cannot hold. Migration names are C# identifiers, so in practice
    /// this changes nothing — but these values reach a path, and a path is not the place to find out
    /// that an assumption was wrong.
    /// </summary>
    private static string? Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. value.Select(c => invalid.Contains(c) ? '_' : c)]);
        return cleaned.Length > 64 ? cleaned[..64] : cleaned;
    }

    private static string Key(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..16];
    }
}
