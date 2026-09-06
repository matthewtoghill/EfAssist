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

        /// <summary>Every command's arguments, so a test can assert what was actually asked for.</summary>
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<EfResult> RunAsync(
            IReadOnlyList<string> args,
            string workingDirectory,
            IProgress<OutputLine>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(args);
            return Task.FromResult(NextResult);
        }
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

    // ---- Migrations bundle ----

    /// <summary>Wires the Save As dialog to answer with a fixed path, and records what it was offered.</summary>
    private static ToolsViewModel WithSaveDialog(ToolsViewModel tools, string? answer, List<string> suggested)
    {
        tools.PickSaveFileAsync = (name, _) =>
        {
            suggested.Add(name);
            return Task.FromResult(answer);
        };

        return tools;
    }

    [Fact]
    public async Task A_bundle_runs_the_bundle_command_against_the_chosen_path()
    {
        var runner = new FakeEf();
        var (tools, session) = Build(runner);
        List<string> suggested = [];
        WithSaveDialog(tools, @"C:\deploy\BlogContext-efbundle.exe", suggested);

        await tools.CreateBundleCommand.ExecuteAsync(null);

        var args = Assert.Single(runner.Calls);
        Assert.Equal(["ef", "migrations", "bundle"], args.Take(3));
        Assert.Contains(@"C:\deploy\BlogContext-efbundle.exe", args);
        Assert.Equal(@"C:\deploy\BlogContext-efbundle.exe", tools.BundlePath);
        Assert.True(tools.HasBundle);
        Assert.Contains("Bundle written", session.StatusMessage);
    }

    [Fact]
    public async Task Cancelling_the_save_dialog_runs_nothing()
    {
        var runner = new FakeEf();
        var (tools, _) = Build(runner);
        WithSaveDialog(tools, null, []);

        await tools.CreateBundleCommand.ExecuteAsync(null);

        Assert.Empty(runner.Calls);
        Assert.False(tools.HasBundle);
    }

    [Fact]
    public async Task The_suggested_name_and_the_arguments_both_follow_the_chosen_runtime()
    {
        var runner = new FakeEf();
        var (tools, _) = Build(runner);
        List<string> suggested = [];
        WithSaveDialog(tools, "/deploy/BlogContext-efbundle", suggested);

        tools.SelectedTargetRuntime = "linux-x64";
        tools.BundleSelfContained = true;

        await tools.CreateBundleCommand.ExecuteAsync(null);

        // No .exe on a Linux bundle, whichever machine produced it.
        Assert.Equal("BlogContext-efbundle", Assert.Single(suggested));

        var args = Assert.Single(runner.Calls);
        Assert.Contains("--self-contained", args);
        Assert.Contains("linux-x64", args);
    }

    [Fact]
    public async Task The_default_runtime_sentinel_is_not_passed_to_the_cli()
    {
        var runner = new FakeEf();
        var (tools, _) = Build(runner);
        WithSaveDialog(tools, "bundle.exe", []);

        await tools.CreateBundleCommand.ExecuteAsync(null);

        var args = Assert.Single(runner.Calls);
        Assert.DoesNotContain("--target-runtime", args);
        Assert.DoesNotContain(ToolsViewModel.RuntimeThisMachine, args);
    }

    [Fact]
    public async Task A_failed_bundle_is_reported_and_offers_nothing_to_open()
    {
        var runner = new FakeEf
        {
            NextResult = new EfResult(
                1,
                [new OutputLine(OutputChannel.Error, "MSBUILD : error MSB1011: more than one project.")],
                "fake",
                "."),
        };
        var (tools, session) = Build(runner);
        WithSaveDialog(tools, "bundle.exe", []);

        await tools.CreateBundleCommand.ExecuteAsync(null);

        Assert.False(tools.HasBundle);
        Assert.Null(tools.BundlePath);
        Assert.NotEqual("Ready.", session.StatusMessage);
    }

    [Fact]
    public async Task A_bundle_built_for_one_context_is_dropped_when_the_target_changes()
    {
        var (tools, _) = Build(new FakeEf());
        WithSaveDialog(tools, "bundle.exe", []);

        await tools.CreateBundleCommand.ExecuteAsync(null);
        Assert.True(tools.HasBundle);

        tools.NotifyTargetChanged();

        Assert.False(tools.HasBundle);
    }

    [Fact]
    public void The_hint_says_what_the_target_machine_still_needs()
    {
        var (tools, _) = Build(new FakeEf());

        Assert.Contains("needs a matching .NET runtime", tools.BundleHint);

        tools.BundleSelfContained = true;
        Assert.Contains("needs nothing installed", tools.BundleHint);
    }

    // ---- The no-build question ----

    private static readonly EfTarget NoBuildTarget = new(
        Project: @"C:\repo\src\Data\Data.csproj",
        StartupProject: @"C:\repo\src\Api\Api.csproj",
        Context: "BlogContext",
        NoBuild: true);

    /// <summary>Records what the dialog was asked, and answers it the way the test wants.</summary>
    private sealed class FakeConfirm
    {
        public List<ConfirmRequest> Asked { get; } = [];

        public bool Answer { get; set; } = true;

        /// <summary>What the user leaves the tick box on. Null means leave it as the caller seeded it.</summary>
        public bool? TickBox { get; set; }

        public Task<bool> HandleAsync(ConfirmRequest request)
        {
            Asked.Add(request);
            if (TickBox is not null)
            {
                request.OptionChecked = TickBox.Value;
            }

            return Task.FromResult(Answer);
        }
    }

    [Fact]
    public async Task A_workspace_that_builds_normally_is_not_asked_anything()
    {
        var runner = new FakeEf();
        var (tools, _) = Build(runner);
        var confirm = new FakeConfirm();
        tools.ConfirmAsync = confirm.HandleAsync;
        WithSaveDialog(tools, "bundle.exe", []);

        await tools.CreateBundleCommand.ExecuteAsync(null);

        Assert.Empty(confirm.Asked);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Leaving_the_tick_box_on_drops_no_build_for_this_run()
    {
        var runner = new FakeEf();
        var (tools, _) = Build(runner, NoBuildTarget);
        var confirm = new FakeConfirm();
        tools.ConfirmAsync = confirm.HandleAsync;
        WithSaveDialog(tools, "bundle.exe", []);

        await tools.CreateBundleCommand.ExecuteAsync(null);

        var request = Assert.Single(confirm.Asked);
        Assert.True(request.HasOption);
        Assert.True(request.OptionChecked);
        Assert.False(request.IsDestructive);

        Assert.DoesNotContain("--no-build", Assert.Single(runner.Calls));
    }

    [Fact]
    public async Task Clearing_the_tick_box_keeps_no_build()
    {
        var runner = new FakeEf();
        var (tools, _) = Build(runner, NoBuildTarget);
        var confirm = new FakeConfirm { TickBox = false };
        tools.ConfirmAsync = confirm.HandleAsync;
        WithSaveDialog(tools, "bundle.exe", []);

        await tools.CreateBundleCommand.ExecuteAsync(null);

        Assert.Contains("--no-build", Assert.Single(runner.Calls));
    }

    [Fact]
    public async Task Cancelling_the_question_runs_nothing_and_never_asks_for_a_filename()
    {
        var runner = new FakeEf();
        var (tools, _) = Build(runner, NoBuildTarget);
        tools.ConfirmAsync = new FakeConfirm { Answer = false }.HandleAsync;
        List<string> suggested = [];
        WithSaveDialog(tools, "bundle.exe", suggested);

        await tools.CreateBundleCommand.ExecuteAsync(null);

        Assert.Empty(runner.Calls);
        Assert.Empty(suggested);
        Assert.False(tools.HasBundle);
    }

    [Fact]
    public async Task Answering_once_does_not_rewrite_the_workspace_setting()
    {
        var runner = new FakeEf();
        var (tools, _) = Build(runner, NoBuildTarget);
        var confirm = new FakeConfirm();
        tools.ConfirmAsync = confirm.HandleAsync;
        WithSaveDialog(tools, "bundle.exe", []);

        await tools.CreateBundleCommand.ExecuteAsync(null);
        await tools.CreateBundleCommand.ExecuteAsync(null);

        // Asked both times: clearing the flag applies to the one run, not to the workspace.
        Assert.Equal(2, confirm.Asked.Count);
    }

    [Fact]
    public async Task Bundling_without_a_way_to_ask_refuses_rather_than_guessing()
    {
        var runner = new FakeEf();
        var (tools, session) = Build(runner, NoBuildTarget);
        WithSaveDialog(tools, "bundle.exe", []);

        await tools.CreateBundleCommand.ExecuteAsync(null);

        Assert.Empty(runner.Calls);
        Assert.NotEqual("Ready.", session.StatusMessage);
    }
}
