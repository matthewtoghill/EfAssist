using EfMigrateHub.Core;

namespace EfMigrateHub.Core.Tests;

public class PreflightTests
{
    /// <summary>Answers each call in order, so the SDK and ef probes can differ.</summary>
    private sealed class ScriptedRunner(params EfResult[] responses) : IEfRunner
    {
        private int _call;

        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<EfResult> RunAsync(
            IReadOnlyList<string> args,
            string workingDirectory,
            IProgress<OutputLine>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(args);
            return Task.FromResult(responses[Math.Min(_call++, responses.Length - 1)]);
        }
    }

    private static EfResult Ok(params string[] lines) =>
        new(0, lines.Select(l => new OutputLine(OutputChannel.Raw, l)).ToArray(), "fake", ".");

    private static EfResult Failed(string error) =>
        new(1, [new OutputLine(OutputChannel.Error, error)], "fake", ".");

    [Fact]
    public async Task Reports_both_versions_when_the_tool_is_installed()
    {
        var runner = new ScriptedRunner(
            Ok("10.0.400"),
            Ok("Entity Framework Core .NET Command-line Tools", "10.0.10"));

        var status = await Preflight.CheckAsync(runner, ".");

        Assert.True(status.EfToolAvailable);
        Assert.Null(status.Problem);
        Assert.Equal("10.0.400", status.SdkVersion);
        Assert.Equal("10.0.10", status.EfToolVersion);
    }

    [Fact]
    public async Task Probes_dotnet_ef_rather_than_the_dotnet_ef_executable()
    {
        // Going through `dotnet ef` is what makes a local tool manifest work.
        var runner = new ScriptedRunner(Ok("10.0.400"), Ok("tools", "10.0.10"));

        await Preflight.CheckAsync(runner, ".");

        Assert.Equal(["--version"], runner.Calls[0]);
        Assert.Equal(["ef", "--version"], runner.Calls[1]);
    }

    [Fact]
    public async Task A_missing_tool_is_a_problem_with_the_sdk_version_still_reported()
    {
        var runner = new ScriptedRunner(
            Ok("10.0.400"),
            Failed("Could not execute because the specified command or file was not found."));

        var status = await Preflight.CheckAsync(runner, ".");

        Assert.False(status.EfToolAvailable);
        Assert.Contains("not found", status.Problem);
        Assert.Equal("10.0.400", status.SdkVersion);
        Assert.Null(status.EfToolVersion);
    }

    [Fact]
    public async Task Falls_back_to_a_usable_message_when_the_failure_says_nothing()
    {
        var runner = new ScriptedRunner(Ok("10.0.400"), new EfResult(1, [], "fake", "."));

        var status = await Preflight.CheckAsync(runner, ".");

        Assert.False(status.EfToolAvailable);
        Assert.False(string.IsNullOrWhiteSpace(status.Problem));
    }

    [Fact]
    public async Task Survives_the_sdk_probe_failing()
    {
        var runner = new ScriptedRunner(Failed("no dotnet"), Failed("no dotnet"));

        var status = await Preflight.CheckAsync(runner, ".");

        Assert.False(status.EfToolAvailable);
        Assert.Null(status.SdkVersion);
    }

    [Fact]
    public async Task Real_environment_has_the_tooling_this_app_needs()
    {
        var status = await Preflight.CheckAsync(new EfRunner(), AppContext.BaseDirectory);

        Assert.True(status.EfToolAvailable, status.Problem);
        Assert.NotNull(status.EfToolVersion);
        Assert.NotNull(status.SdkVersion);
    }
}
