using System;
using System.Collections.Generic;
using EfAssist.Core;

namespace EfAssist.App.ViewModels;

/// <summary>How a recorded command ended.</summary>
public enum CommandOutcome
{
    /// <summary>Exit code zero, or local work that returned.</summary>
    Succeeded,

    /// <summary>A non-zero exit code, or work that threw.</summary>
    Failed,

    /// <summary>Cancelled from the Cancel button.</summary>
    Cancelled,
}

/// <summary>
/// One command that ran, for the Activity list.
/// </summary>
/// <remarks>
/// <para>
/// The console is a single stream, so a failure scrolls away as soon as the next command runs and
/// its guidance ends up a long way from the command that caused it. A run keeps the two together:
/// the command line, how it ended, how long it took, the diagnosis, and where in the console its
/// output starts — which is what "Show in raw output" jumps to.
/// </para>
/// <para>
/// In memory only, for the life of the process. Nothing here is written to disk: EF's output
/// carries server names and connection strings, and persisting it would mean deciding what to
/// redact. <c>ROADMAP.md</c> records the trigger for revisiting that.
/// </para>
/// </remarks>
public sealed record CommandRun
{
    /// <summary>What the app called it — "List migrations", "Apply migrations".</summary>
    public required string Label { get; init; }

    /// <summary>
    /// The arguments as passed to <c>dotnet</c>, so the run can be repeated verbatim. Empty for
    /// local work, which has no command line to repeat.
    /// </summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>The command as it would be typed, or null for local work.</summary>
    public string? CommandLine => Args.Count == 0 ? null : "dotnet " + string.Join(' ', Args);

    public required CommandOutcome Outcome { get; init; }

    /// <summary>Null for local work and for a run that never produced a result.</summary>
    public int? ExitCode { get; init; }

    public required TimeSpan Duration { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>The guidance for this failure, when it was one the app recognises.</summary>
    public EfDiagnosis? Diagnosis { get; init; }

    /// <summary>EF's own first error line, so a failure says something even without a diagnosis.</summary>
    public string? FailureLine { get; init; }

    /// <summary>
    /// Index of this run's first line in the console. The console is never truncated mid-session, so
    /// this stays valid until the console is cleared.
    /// </summary>
    public required int FirstOutputLine { get; init; }

    /// <summary>
    /// Set by the caller for commands that write to the database. Those went through a confirmation
    /// with the SQL in front of the user; re-running one from a history card would skip it, so
    /// destructive runs are shown but cannot be repeated from here.
    /// </summary>
    public bool Destructive { get; init; }

    public bool Failed => Outcome == CommandOutcome.Failed;

    public bool Succeeded => Outcome == CommandOutcome.Succeeded;

    public bool Cancelled => Outcome == CommandOutcome.Cancelled;

    /// <summary>Whether the "Run again" button applies. See <see cref="Destructive"/>.</summary>
    public bool CanRerun => !Destructive && Args.Count > 0;

    /// <summary>"succeeded in 2.1 s", "failed · exit 1", "cancelled after 31 s".</summary>
    public string OutcomeSummary => Outcome switch
    {
        CommandOutcome.Succeeded => $"succeeded in {Seconds}",
        CommandOutcome.Cancelled => $"cancelled after {Seconds}",
        _ => ExitCode is null ? $"failed after {Seconds}" : $"failed · exit {ExitCode} · {Seconds}",
    };

    /// <summary>The failure in one line: the diagnosis title when there is one, EF's own line if not.</summary>
    public string? Problem => Diagnosis?.Title ?? FailureLine;

    private string Seconds => Duration.TotalSeconds < 10
        ? $"{Duration.TotalSeconds:0.0} s"
        : $"{Duration.TotalSeconds:0} s";
}
