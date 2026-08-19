using System.Security.Cryptography;
using System.Text;

namespace EfMigrateHub.Core;

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
    /// Walks the tree explicitly rather than with <see cref="SearchOption.AllDirectories"/>, so an
    /// unreadable subdirectory skips itself instead of aborting the whole search, and so bin and obj
    /// are never descended into at all.
    /// </summary>
    private static string? Search(string directory, string fileName)
    {
        var match = Path.Combine(directory, fileName);
        if (File.Exists(match))
        {
            return match;
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (IgnoredDirectories.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string? found;
            try
            {
                found = Search(child, fileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (found is not null)
            {
                return found;
            }
        }

        return null;
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
    public static string ScriptCachePath(string migrationsProjectPath, string? context, string migrationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationId);

        var key = Key(Path.GetFullPath(migrationsProjectPath) + "|" + (context ?? ""));
        return Path.Combine(Path.GetTempPath(), "EfMigrateHub", "preview", key, migrationId + ".sql");
    }

    private static string Key(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..16];
    }
}
