using EfAssist.App.ViewModels;
using EfAssist.Core;

namespace EfAssist.Core.Tests;

/// <summary>
/// The shared command runner: what it says after a command, and that a non-zero exit or a throwing
/// runner never leaves the shell claiming something is still going.
/// </summary>
public class CommandSessionTests
{
    private sealed class ScriptedRunner(Func<EfResult> next) : IEfRunner
    {
        public Task<EfResult> RunAsync(
            IReadOnlyList<string> args,
            string workingDirectory,
            IProgress<OutputLine>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(next());
    }

    private sealed class ThrowingRunner : IEfRunner
    {
        public Task<EfResult> RunAsync(
            IReadOnlyList<string> args,
            string workingDirectory,
            IProgress<OutputLine>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new IOException("the pipe is broken");
    }

    private static EfResult Failure(string message) => new(
        1, [new OutputLine(OutputChannel.Error, message)], "dotnet ef migrations list", @"C:\repo");

    private static EfResult Success() => new(0, [], "dotnet ef migrations list", @"C:\repo");

    private static CommandSession Build(IEfRunner runner) =>
        new(runner) { PostToUiThread = action => action() };

    [Fact]
    public async Task Explains_a_recognised_failure()
    {
        var session = Build(new ScriptedRunner(() => Failure("Build failed. Use dotnet build to see the errors.")));

        await session.RunAsync(["ef", "migrations", "list"], "Listing migrations");

        Assert.True(session.HasDiagnosis);
        Assert.Contains("did not compile", session.Diagnosis!.Title);
    }

    [Fact]
    public async Task The_next_successful_command_takes_the_explanation_away()
    {
        var fail = true;
        var session = Build(new ScriptedRunner(() => fail ? Failure("Build failed.") : Success()));

        await session.RunAsync(["ef", "migrations", "list"], "Listing migrations");
        Assert.True(session.HasDiagnosis);

        fail = false;
        await session.RunAsync(["ef", "migrations", "list"], "Listing migrations");

        Assert.False(session.HasDiagnosis);
        Assert.Null(session.Diagnosis);
    }

    [Fact]
    public async Task Copy_diagnostics_produces_one_pasteable_block()
    {
        var copied = "";
        var session = Build(new ScriptedRunner(() => Failure("Build failed.")));
        session.CopyToClipboardAsync = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };
        session.DiagnosticsHeader = () => "dotnet-ef 10.0.10 · SDK 10.0.400";

        await session.RunAsync(["ef", "migrations", "list"], "Listing migrations");
        await session.CopyDiagnosticsCommand.ExecuteAsync(null);

        // Everything needed to reproduce it elsewhere, in one paste.
        Assert.Contains("dotnet-ef 10.0.10", copied);
        Assert.Contains("Diagnosis: The project did not compile", copied);
        Assert.Contains("Command:   dotnet ef migrations list", copied);
        Assert.Contains(@"Directory: C:\repo", copied);
        Assert.Contains("Exit code: 1", copied);
        Assert.Contains("Build failed.", copied);
    }

    [Fact]
    public async Task Copy_diagnostics_before_anything_has_run_says_so_rather_than_copying_nothing()
    {
        var copied = "";
        var session = Build(new ScriptedRunner(Success));
        session.CopyToClipboardAsync = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };

        await session.CopyDiagnosticsCommand.ExecuteAsync(null);

        Assert.Contains("No command has been run yet.", copied);
    }

    [Fact]
    public async Task A_runner_that_throws_still_stops_the_spinner_and_says_what_happened()
    {
        var session = Build(new ThrowingRunner());

        var result = await session.RunAsync(["ef", "migrations", "list"], "Listing migrations");

        Assert.Null(result);
        Assert.False(session.IsRunning);
        Assert.Contains("the pipe is broken", session.StatusMessage);
        Assert.Contains(session.Output, l => l.Channel == OutputChannel.Error);
    }

    [Fact]
    public async Task A_failed_command_leaves_the_status_bar_reporting_the_failure()
    {
        var session = Build(new ScriptedRunner(() => Failure("No project was found.")));

        var result = await session.RunAsync(["ef", "migrations", "list"], "Listing migrations");

        Assert.NotNull(result);
        Assert.False(session.IsRunning);
        session.ReportFailure(result, "Listing migrations failed.");
        Assert.Equal("No project was found.", session.StatusMessage);
    }

    [Fact]
    public async Task Records_a_run_per_command_with_its_outcome_and_where_its_output_starts()
    {
        var fail = false;
        var session = Build(new ScriptedRunner(() => fail ? Failure("Login failed for user 'app'.") : Success()));

        await session.RunAsync(["ef", "migrations", "list"], "Listing migrations");

        var listed = Assert.Single(session.Runs);
        Assert.Equal("Listing migrations", listed.Label);
        Assert.Equal("dotnet ef migrations list", listed.CommandLine);
        Assert.Equal(CommandOutcome.Succeeded, listed.Outcome);
        Assert.Equal(0, listed.ExitCode);

        // The echoed command line is this run's first console line.
        Assert.Equal(0, listed.FirstOutputLine);
        Assert.True(listed.CanRerun);
        Assert.False(session.HasUnreadFailure);

        var linesSoFar = session.Output.Count;
        fail = true;
        await session.RunAsync(["ef", "database", "update"], "Applying migrations", destructive: true);

        // Newest first, so the failure is at the head.
        Assert.Equal(2, session.Runs.Count);
        var applied = session.Runs[0];
        Assert.Equal(CommandOutcome.Failed, applied.Outcome);
        Assert.Equal(1, applied.ExitCode);
        Assert.Equal(linesSoFar, applied.FirstOutputLine);
        // A recognised failure reads as its guidance title, with EF's own line kept behind it.
        Assert.Equal(applied.Diagnosis!.Title, applied.Problem);
        Assert.Contains("Login failed", applied.FailureLine);
        Assert.True(session.HasUnreadFailure);

        // A database write went through a confirmation the card cannot reproduce.
        Assert.True(applied.Destructive);
        Assert.False(applied.CanRerun);

        Assert.Same(applied, session.LastRun);

        session.MarkActivityRead();
        Assert.False(session.HasUnreadFailure);
    }

    [Fact]
    public async Task A_run_that_throws_is_recorded_as_failed_with_the_message()
    {
        var session = Build(new ThrowingRunner());

        await session.RunAsync(["ef", "migrations", "list"], "Listing migrations");

        var run = Assert.Single(session.Runs);
        Assert.Equal(CommandOutcome.Failed, run.Outcome);
        Assert.Null(run.ExitCode);
        Assert.Equal("the pipe is broken", run.Problem);
    }

    [Fact]
    public async Task Local_work_is_recorded_with_no_command_line_to_repeat()
    {
        var session = Build(new ScriptedRunner(Success));

        await session.RunLocalAsync("Generating diagram", _ => Task.FromResult(7));

        var run = Assert.Single(session.Runs);
        Assert.Equal("Generating diagram", run.Label);
        Assert.Null(run.CommandLine);
        Assert.Equal(CommandOutcome.Succeeded, run.Outcome);

        // Nothing to re-run: there is no command line behind local work.
        Assert.False(run.CanRerun);
    }

    [Fact]
    public async Task The_history_is_capped_and_drops_the_oldest()
    {
        var session = Build(new ScriptedRunner(Success));

        for (var i = 0; i < CommandSession.MaxRuns + 5; i++)
        {
            await session.RunAsync(["ef", "migrations", "list"], $"Run {i}");
        }

        Assert.Equal(CommandSession.MaxRuns, session.Runs.Count);
        Assert.Equal($"Run {CommandSession.MaxRuns + 4}", session.Runs[0].Label);
        Assert.Equal("Run 5", session.Runs[^1].Label);
    }

    [Fact]
    public async Task Clearing_the_console_clears_the_history_that_points_into_it()
    {
        var session = Build(new ScriptedRunner(() => Failure("Build failed.")));

        await session.RunAsync(["ef", "migrations", "list"], "Listing migrations");
        Assert.NotEmpty(session.Runs);
        Assert.True(session.HasUnreadFailure);

        session.Reset();

        Assert.Empty(session.Runs);
        Assert.Null(session.LastRun);
        Assert.False(session.HasUnreadFailure);
    }

    [Fact]
    public async Task A_failure_arrives_expanded_and_everything_else_collapsed()
    {
        var fail = false;
        var session = Build(new ScriptedRunner(() => fail ? Failure("Build failed.") : Success()));

        await session.RunAsync(["ef", "migrations", "list"], "Listing migrations");
        Assert.False(session.Runs[0].IsExpanded);

        fail = true;
        await session.RunAsync(["ef", "database", "update"], "Applying migrations");

        // The pane opens itself on a failure, so the card it opened for is already showing its
        // guidance; the successful run behind it stays a single line.
        Assert.True(session.Runs[0].IsExpanded);
        Assert.False(session.Runs[1].IsExpanded);

        session.Runs[1].ToggleExpandedCommand.Execute(null);
        Assert.True(session.Runs[1].IsExpanded);

        // Opening one card does not close another.
        Assert.True(session.Runs[0].IsExpanded);
    }
}
