using EfMigrateHub.Core;

namespace EfMigrateHub.Core.Tests;

/// <summary>
/// Exercises the real process plumbing. Uses `dotnet --version`, which does not build anything, so
/// these stay fast enough to run on every change.
/// </summary>
public class EfRunnerTests
{
    private readonly EfRunner _runner = new();

    [Fact]
    public async Task Captures_output_and_exit_code_from_a_real_process()
    {
        var result = await _runner.RunAsync(["--version"], AppContext.BaseDirectory);

        Assert.True(result.Success, result.Diagnostics);
        Assert.NotEmpty(result.Lines);

        // `dotnet --version` writes an unprefixed line, so it lands on the Raw channel.
        Assert.Contains(result.Lines, l => l.Channel == OutputChannel.Raw && l.Text.Contains('.'));
    }

    [Fact]
    public async Task Streams_lines_as_they_arrive()
    {
        var streamed = new List<OutputLine>();
        var progress = new Progress<OutputLine>(streamed.Add);

        var result = await _runner.RunAsync(["--version"], AppContext.BaseDirectory, progress);

        // Progress<T> posts asynchronously, so wait for it to drain rather than racing it.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (streamed.Count < result.Lines.Count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(result.Lines.Count, streamed.Count);
    }

    [Fact]
    public async Task A_failing_command_is_a_result_not_an_exception()
    {
        // A missing project fails before any build starts, so this stays fast. Note that an unknown
        // flag is not usable here: dotnet ef prints its help banner and exits 0.
        var args = EfArgs.MigrationsList(new EfTarget("does-not-exist.csproj"));

        var result = await _runner.RunAsync(args, AppContext.BaseDirectory);

        Assert.False(result.Success);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unable to retrieve project metadata", result.ErrorMessage);
    }

    [Fact]
    public async Task A_process_that_cannot_start_is_reported_as_a_failed_result()
    {
        // Covers the same path as "dotnet is not on PATH": Process.Start throws, and the caller
        // still gets an EfResult so its error handling and diagnostics work unchanged.
        var result = await _runner.RunAsync(
            ["--version"],
            Path.Combine(Path.GetTempPath(), "EfMigrateHubTests", Guid.NewGuid().ToString("N")));

        Assert.False(result.Success);
        Assert.NotEmpty(result.ErrorMessage);
    }

    [Fact]
    public void Design_time_commands_run_as_Development_so_user_secrets_are_read()
    {
        // WebApplicationBuilder and HostApplicationBuilder only layer user secrets over
        // appsettings.json when the environment is Development. Nothing else sets this — the EF
        // tools never read launchSettings.json — so a Production default would silently connect to
        // whatever appsettings says.
        Assert.Equal(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Development",
            EfRunner.HostEnvironment);
    }

    [Fact]
    public async Task Cancellation_kills_the_process_and_reports_cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _runner.RunAsync(["--version"], AppContext.BaseDirectory, null, cancelled.Token));
    }
}
