using EfAssist.App.ViewModels;
using EfAssist.Core;

namespace EfAssist.Core.Tests;

/// <summary>
/// The rule the write commands share: honour "Don't build" silently on reads, ask on writes.
/// </summary>
public class NoBuildPromptTests
{
    private static readonly EfTarget Builds = new(@"C:\repo\Data.csproj");

    private static readonly EfTarget SkipsBuild = Builds with { NoBuild = true };

    private static ConfirmRequest Request(string? detail = null) =>
        new("Apply migrations", "Apply everything outstanding?", "Apply", detail);

    [Fact]
    public void A_workspace_that_builds_normally_gets_no_tick_box_and_no_extra_words()
    {
        var request = Request("2 migration(s) will be applied.");
        var decorated = request.WithBuildOption(Builds);

        Assert.Same(request, decorated);
        Assert.False(decorated.HasOption);
    }

    [Fact]
    public void Skipping_the_build_adds_the_tick_box_after_the_action_s_own_detail()
    {
        var decorated = Request("2 migration(s) will be applied.").WithBuildOption(SkipsBuild);

        Assert.True(decorated.HasOption);
        Assert.True(decorated.OptionChecked);

        // The action's own detail stays first; the build warning is a footnote on how it runs.
        Assert.StartsWith("2 migration(s) will be applied.", decorated.Detail);
        Assert.Contains("last compiled", decorated.Detail);
    }

    [Fact]
    public void A_request_with_no_detail_of_its_own_still_explains_the_tick_box()
    {
        var decorated = Request().WithBuildOption(SkipsBuild);

        Assert.True(decorated.HasDetail);
        Assert.Contains("last compiled", decorated.Detail);
    }

    [Fact]
    public void Leaving_the_tick_box_on_clears_no_build_for_the_run()
    {
        var decorated = Request().WithBuildOption(SkipsBuild);

        Assert.False(decorated.ResolvedTarget(SkipsBuild).NoBuild);

        // And the workspace's own target is untouched — only the copy handed to the command changes.
        Assert.True(SkipsBuild.NoBuild);
    }

    [Fact]
    public void Clearing_the_tick_box_keeps_no_build()
    {
        var decorated = Request().WithBuildOption(SkipsBuild);
        decorated.OptionChecked = false;

        Assert.True(decorated.ResolvedTarget(SkipsBuild).NoBuild);
    }

    [Fact]
    public void A_request_that_never_had_a_tick_box_cannot_clear_the_flag()
    {
        // OptionChecked defaults true on every request, so the guard has to be HasOption.
        Assert.True(Request().ResolvedTarget(SkipsBuild).NoBuild);
    }

    [Fact]
    public async Task The_standalone_question_is_skipped_entirely_when_the_workspace_builds()
    {
        var asked = 0;

        var resolved = await NoBuildPrompt.AskAsync(
            Builds,
            _ => { asked++; return Task.FromResult(true); },
            "T",
            "Go",
            "Consequence.");

        Assert.Equal(0, asked);
        Assert.Same(Builds, resolved);
    }

    [Fact]
    public async Task The_standalone_question_is_not_styled_as_destructive()
    {
        ConfirmRequest? seen = null;

        await NoBuildPrompt.AskAsync(
            SkipsBuild,
            r => { seen = r; return Task.FromResult(true); },
            "Create bundle without building?",
            "Create bundle",
            "The bundle would be made from whatever was last compiled.");

        Assert.False(seen!.IsDestructive);
        Assert.True(seen.HasOption);
        Assert.Equal("Create bundle", seen.ConfirmText);
    }

    [Fact]
    public async Task Cancelling_the_standalone_question_returns_nothing_to_run()
    {
        var resolved = await NoBuildPrompt.AskAsync(
            SkipsBuild, _ => Task.FromResult(false), "T", "Go", "Consequence.");

        Assert.Null(resolved);
    }
}
