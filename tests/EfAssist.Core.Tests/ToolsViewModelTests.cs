using EfAssist.App.ViewModels;
using EfAssist.Core;

namespace EfAssist.Core.Tests;

/// <summary>The Tools tab, driven with a fake runner that returns canned pending-model-changes outcomes.</summary>
public class ToolsViewModelTests
{
    private static readonly EfTarget Target = new(
        Project: @"C:\repo\src\Data\Data.csproj",
        StartupProject: @"C:\repo\src\Api\Api.csproj",
        Context: "BlogContext");

    private sealed class FakeEf : IEfRunner
    {
        public EfResult NextResult { get; set; } = new(0, [], "fake", ".");

        public Task<EfResult> RunAsync(
            IReadOnlyList<string> args,
            string workingDirectory,
            IProgress<OutputLine>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(NextResult);
    }

    private static (ToolsViewModel Tools, CommandSession Session) Build(FakeEf runner, EfTarget? target = null)
    {
        var session = new CommandSession(runner) { PostToUiThread = action => action() };
        var tools = new ToolsViewModel(session, () => target ?? Target);
        return (tools, session);
    }

    [Fact]
    public async Task A_clean_model_reports_up_to_date()
    {
        var runner = new FakeEf { NextResult = new EfResult(0, [], "fake", ".") };
        var (tools, session) = Build(runner);

        await tools.CheckPendingModelChangesCommand.ExecuteAsync(null);

        Assert.Equal(ModelCheckState.UpToDate, tools.ModelCheckState);
        Assert.Null(session.Diagnosis);
    }

    [Fact]
    public async Task Pending_changes_are_reported_without_being_treated_as_a_failure()
    {
        var runner = new FakeEf
        {
            NextResult = new EfResult(
                1,
                [
                    new OutputLine(
                        OutputChannel.Error,
                        "Changes have been made to the model since the last migration. Add a new migration."),
                ],
                "fake",
                "."),
        };
        var (tools, session) = Build(runner);

        await tools.CheckPendingModelChangesCommand.ExecuteAsync(null);

        Assert.Equal(ModelCheckState.Pending, tools.ModelCheckState);
        Assert.Null(session.Diagnosis);
    }

    [Fact]
    public async Task An_unrelated_failure_is_reported_as_a_failure_and_leaves_the_state_unknown()
    {
        var runner = new FakeEf
        {
            NextResult = new EfResult(
                1,
                [new OutputLine(OutputChannel.Error, "Unable to create a 'DbContext' of type ''.")],
                "fake",
                "."),
        };
        var (tools, session) = Build(runner);

        await tools.CheckPendingModelChangesCommand.ExecuteAsync(null);

        Assert.Equal(ModelCheckState.Unknown, tools.ModelCheckState);
        Assert.NotEqual("Ready.", session.StatusMessage);
    }

    [Fact]
    public void Changing_the_target_resets_a_stale_result()
    {
        var (tools, _) = Build(new FakeEf());

        tools.NotifyTargetChanged();

        Assert.Equal(ModelCheckState.Unknown, tools.ModelCheckState);
    }
}
