namespace EfMigrateHub.Core;

/// <summary>
/// A known EF condition translated into plain language. The raw output is never replaced by this —
/// it stays in the console underneath, and "Copy diagnostics" still copies everything.
/// </summary>
/// <param name="Title">One line naming the problem, in the user's terms rather than EF's.</param>
/// <param name="Guidance">What to change, concretely.</param>
/// <param name="IsWarning">
/// True for a condition EF carried on past. The command may well have produced a result; it just
/// cannot be fully trusted.
/// </param>
public sealed record EfDiagnosis(string Title, string Guidance, bool IsWarning = false);

/// <summary>
/// Maps the handful of EF failures that are common and cryptic onto guidance.
///
/// Matching is on substrings of EF's English message, which is why <see cref="EfRunner"/> pins
/// <c>DOTNET_CLI_UI_LANGUAGE</c>. This is text scraping, and it is only tolerable because nothing
/// depends on it: an unmatched failure still shows EF's own message, the console and the
/// diagnostics block. A wrong match costs a misleading paragraph, not a broken feature.
///
/// Every rule here was reproduced against a real project — see <c>tests/.../Fixtures/error-*.txt</c>,
/// which are the captured output of those runs.
/// </summary>
public static class EfDiagnostics
{
    /// <summary>
    /// EF reports a tool/runtime version gap as a warning and carries on, so this is checked
    /// separately from the failure rules and regardless of the exit code. It is worth saying because
    /// an old tool can report the wrong answer rather than refusing: an EF 8 tool listing an EF 10
    /// project returns the migrations but gets applied state wrong.
    /// </summary>
    private static readonly EfDiagnosis VersionMismatch = new(
        "The dotnet ef tool is a different version from the project's EF Core.",
        "Results can be wrong rather than merely refused, so treat applied state with suspicion. "
        + "Update the tool with `dotnet tool update --global dotnet-ef`, or, if this solution pins "
        + "one in .config/dotnet-tools.json, update it there and run `dotnet tool restore`.",
        IsWarning: true);

    /// <summary>
    /// Ordered most-specific first, and where two could match the more actionable one wins. A
    /// connection failure usually arrives wrapped in "Unable to create a 'DbContext'", so it is
    /// tested first — otherwise every unreachable database reads as a design-time factory problem.
    /// </summary>
    private static readonly (string[] Needles, EfDiagnosis Diagnosis)[] Rules =
    [
        (
            [
                "no executable found matching command \"dotnet-ef\"",
                "could not execute because the specified command or file was not found",
            ],
            new EfDiagnosis(
                "The dotnet ef tool is not available here.",
                $"Install it with `{ToolStatus.InstallCommand}`, then reopen the workspace. If this "
                + "solution uses a local tool manifest, run `dotnet tool restore` in the solution "
                + "folder instead.")
        ),
        (
            [
                "doesn't reference microsoft.entityframeworkcore.design",
                "does not reference microsoft.entityframeworkcore.design",
            ],
            new EfDiagnosis(
                "The startup project cannot host the EF tools.",
                "EF loads its design-time services from the startup project, and that project does "
                + "not reference Microsoft.EntityFrameworkCore.Design. Either pick a different "
                + "Startup project on the left — usually the runnable app — or add the package to "
                + "it with `dotnet add package Microsoft.EntityFrameworkCore.Design`.")
        ),
        (
            [
                "doesn't match your migrations assembly",
                "does not match your migrations assembly",
            ],
            new EfDiagnosis(
                "The migrations project and the assembly EF expects do not agree.",
                "The context is configured to keep its migrations in a different assembly from the "
                + "project EF was pointed at. Either change Migrations project on the left to the "
                + "assembly named in the message, or change MigrationsAssembly(...) in the "
                + "context's UseSqlServer/UseNpgsql/UseSqlite call to match the project you want.")
        ),
        (
            [
                "login failed for user",
                "password authentication failed",
                "a network-related or instance-specific error",
                "the server was not found or was not accessible",
                "no such host is known",
                "connection refused",
                "cannot open database",
                "unable to open database file",
                "failed to connect",
                "connection timeout expired",
            ],
            new EfDiagnosis(
                "The database could not be reached.",
                "The build and the model were fine — the connection was not. Check that the server "
                + "is running and reachable, and that the connection string in the startup "
                + "project's configuration or user-secrets points where you expect. To work on the "
                + "migration files without a database, tick Offline on the left.")
        ),
        (
            [
                "no project was found",
                "unable to retrieve project metadata",
                "msb1003",
                "msb1009",
            ],
            new EfDiagnosis(
                "EF could not find or read the project it was pointed at.",
                "Check the Migrations project and Startup project selections on the left. Startup "
                + "project is the runnable app that holds the configuration and connection string; "
                + "Migrations project is the one containing the Migrations folder. They are often "
                + "the same project, but when they differ both must be set.")
        ),
        (
            ["build failed"],
            new EfDiagnosis(
                "The project did not compile, so EF never ran.",
                "Fix the build errors — `dotnet build` reports the same ones with detail. Nothing EF "
                + "does can work until the project compiles, because it loads the built assembly.")
        ),
        (
            [
                "unable to create a 'dbcontext'",
                "unable to create an object of type",
                "no dbcontext was found in assembly",
                "more than one dbcontext was found",
            ],
            new EfDiagnosis(
                "EF could not construct the DbContext at design time.",
                "EF builds the startup project and runs its host builder to get a context. Usual "
                + "causes: the wrong Startup or Migrations project is selected; the app's startup "
                + "code throws before the host is built; or the context has no parameterless "
                + "constructor and no design-time factory. Adding an IDesignTimeDbContextFactory<T> "
                + "to the project settles it in every case.")
        ),
    ];

    /// <summary>
    /// <c>migrations has-pending-model-changes</c> reports pending changes as a thrown
    /// <c>OperationException</c> — a non-zero exit that is an expected outcome, not a failure to
    /// diagnose. Checked against <see cref="EfResult.ErrorMessage"/> before routing a failed result
    /// through <see cref="Diagnose"/>.
    /// </summary>
    public const string PendingModelChangesNeedle =
        "changes have been made to the model since the last migration";

    public static bool IsPendingModelChanges(EfResult result) =>
        !result.Success &&
        result.ErrorMessage.Contains(PendingModelChangesNeedle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns null for a clean success, and for any failure this does not recognise.
    /// </summary>
    public static EfDiagnosis? Diagnose(EfResult result)
    {
        if (!result.Success)
        {
            // Matched against the failure message only. EF repeats a lot on the info lines — stack
            // traces, the whole build log — and matching those turns any command that mentions a
            // phrase in passing into a confident wrong answer.
            var message = result.ErrorMessage;

            foreach (var (needles, diagnosis) in Rules)
            {
                if (needles.Any(n => message.Contains(n, StringComparison.OrdinalIgnoreCase)))
                {
                    return diagnosis;
                }
            }
        }

        // Also reached by an unrecognised failure: a version gap is a plausible cause of one, and
        // saying so beats saying nothing.
        return HasVersionWarning(result) ? VersionMismatch : null;
    }

    private static bool HasVersionWarning(EfResult result) => result.Lines.Any(l =>
        l.Channel is OutputChannel.Warn or OutputChannel.Error &&
        l.Text.Contains("Entity Framework tools version", StringComparison.OrdinalIgnoreCase));
}
