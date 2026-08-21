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
    public async Task Dismissing_the_explanation_leaves_the_output_alone()
    {
        var session = Build(new ScriptedRunner(() => Failure("Build failed.")));

        await session.RunAsync(["ef", "migrations", "list"], "Listing migrations");
        var lines = session.Output.Count;

        session.DismissDiagnosisCommand.Execute(null);

        Assert.False(session.HasDiagnosis);
        Assert.Equal(lines, session.Output.Count);
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
}
