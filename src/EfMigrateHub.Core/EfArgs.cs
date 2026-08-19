namespace EfMigrateHub.Core;

/// <summary>
/// Which projects and context a command applies to. Passed explicitly on every invocation —
/// never inferred from the working directory.
/// </summary>
/// <param name="Project">The migrations project (<c>--project</c>): where migration files live.</param>
/// <param name="StartupProject">
/// The project EF builds and loads to find the context (<c>--startup-project</c>). Defaults to
/// <paramref name="Project"/> when null, matching the CLI's own behaviour.
/// </param>
public sealed record EfTarget(
    string Project,
    string? StartupProject = null,
    string? Context = null,
    bool NoBuild = false,
    string? Configuration = null,
    string? Framework = null);

/// <summary>
/// Builds <c>dotnet ef</c> argument lists. Returns a list rather than a string so
/// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> handles quoting — paths with
/// spaces are the norm, not the exception.
/// </summary>
public static class EfArgs
{
    public static List<string> MigrationsList(EfTarget target, bool noConnect = false)
    {
        var args = Build("migrations", "list", [], target, json: true);
        if (noConnect)
        {
            // Offline: EF returns "applied": null for every migration, not false.
            args.Add("--no-connect");
        }

        return args;
    }

    public static List<string> MigrationsAdd(EfTarget target, string name, string? outputDir = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var args = Build("migrations", "add", [name], target, json: false);
        if (!string.IsNullOrWhiteSpace(outputDir))
        {
            args.Add("--output-dir");
            args.Add(outputDir);
        }

        return args;
    }

    public static List<string> MigrationsRemove(EfTarget target, bool force = false)
    {
        var args = Build("migrations", "remove", [], target, json: false);
        if (force)
        {
            args.Add("--force");
        }

        return args;
    }

    /// <summary>
    /// Always writes to a file. Capturing script output from stdout means stripping the
    /// <c>--prefix-output</c> field off SQL that carries its own indentation; writing to disk
    /// gives byte-exact SQL and is where the script needs to end up anyway.
    /// </summary>
    public static List<string> MigrationsScript(
        EfTarget target,
        string outputPath,
        string? from = null,
        string? to = null,
        bool idempotent = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        // FROM and TO are positional. TO cannot be supplied without FROM, so a caller asking for
        // "everything up to X" has to spell out the start explicitly.
        List<string> positional = [];
        if (!string.IsNullOrWhiteSpace(from))
        {
            positional.Add(from);
            if (!string.IsNullOrWhiteSpace(to))
            {
                positional.Add(to);
            }
        }
        else if (!string.IsNullOrWhiteSpace(to))
        {
            throw new ArgumentException(
                "A 'to' migration cannot be supplied without a 'from' migration; pass \"0\" for the start.",
                nameof(to));
        }

        var args = Build("migrations", "script", positional, target, json: false);
        args.Add("--output");
        args.Add(outputPath);
        if (idempotent)
        {
            // Not supported by every provider — SQLite throws. Gate on the provider name from
            // DbContextDetails before offering this.
            args.Add("--idempotent");
        }

        return args;
    }

    public static List<string> DatabaseUpdate(EfTarget target, string? targetMigration = null)
    {
        List<string> positional = string.IsNullOrWhiteSpace(targetMigration) ? [] : [targetMigration];
        return Build("database", "update", positional, target, json: false);
    }

    /// <summary>
    /// Always passes <c>--force</c>. Without it the CLI prompts for confirmation on stdin, which
    /// would hang a GUI-launched process forever. The confirmation is the app's job — see the
    /// type-the-database-name dialog.
    /// </summary>
    public static List<string> DatabaseDrop(EfTarget target, bool dryRun = false)
    {
        var args = Build("database", "drop", [], target, json: false);
        args.Add("--force");
        if (dryRun)
        {
            args.Add("--dry-run");
        }

        return args;
    }

    /// <summary>
    /// No <c>--json</c>: the command does not support it. A clean model exits 0; pending changes
    /// throw and exit non-zero, so callers distinguish the two by matching
    /// <see cref="EfDiagnostics.PendingModelChangesNeedle"/> against the failure message rather than
    /// treating every non-zero exit as an error.
    /// </summary>
    public static List<string> MigrationsHasPendingModelChanges(EfTarget target) =>
        Build("migrations", "has-pending-model-changes", [], target, json: false);

    public static List<string> DbContextList(EfTarget target) =>
        Build("dbcontext", "list", [], target, json: true);

    public static List<string> DbContextInfo(EfTarget target) =>
        Build("dbcontext", "info", [], target, json: true);

    private static List<string> Build(
        string group,
        string verb,
        IEnumerable<string> positional,
        EfTarget target,
        bool json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target.Project);

        var args = new List<string> { "ef", group, verb };
        args.AddRange(positional);

        if (json)
        {
            args.Add("--json");
        }

        // Machine-readable output and no ANSI escapes, on every command.
        args.Add("--prefix-output");
        args.Add("--no-color");

        args.Add("--project");
        args.Add(target.Project);

        if (!string.IsNullOrWhiteSpace(target.StartupProject))
        {
            args.Add("--startup-project");
            args.Add(target.StartupProject);
        }

        if (!string.IsNullOrWhiteSpace(target.Context))
        {
            args.Add("--context");
            args.Add(target.Context);
        }

        if (!string.IsNullOrWhiteSpace(target.Configuration))
        {
            args.Add("--configuration");
            args.Add(target.Configuration);
        }

        if (!string.IsNullOrWhiteSpace(target.Framework))
        {
            args.Add("--framework");
            args.Add(target.Framework);
        }

        if (target.NoBuild)
        {
            args.Add("--no-build");
        }

        return args;
    }
}
