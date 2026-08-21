using EfAssist.Core;

namespace EfAssist.Core.Tests;

public class ScriptFileNameTests
{
    [Fact]
    public void Names_a_full_script_from_zero_to_latest() =>
        Assert.Equal(
            "BlogContext_0-to-latest.sql",
            ScriptFileName.Suggest("BlogContext", null, null, idempotent: false));

    [Fact]
    public void Names_a_range_by_its_endpoints() =>
        Assert.Equal(
            "BlogContext_InitialCreate-to-AddBlogUrl.sql",
            ScriptFileName.Suggest("BlogContext", "InitialCreate", "AddBlogUrl", idempotent: false));

    [Fact]
    public void Marks_an_idempotent_script_so_the_two_do_not_collide() =>
        Assert.Equal(
            "BlogContext_0-to-latest_idempotent.sql",
            ScriptFileName.Suggest("BlogContext", null, null, idempotent: true));

    [Fact]
    public void The_same_choices_always_suggest_the_same_name()
    {
        // Stable, so regenerating a range reuses the file rather than littering the folder.
        var first = ScriptFileName.Suggest("BlogContext", "A", "B", idempotent: false);
        var second = ScriptFileName.Suggest("BlogContext", "A", "B", idempotent: false);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Falls_back_to_a_usable_name_without_a_context() =>
        Assert.Equal("script_0-to-latest.sql", ScriptFileName.Suggest(null, null, null, false));

    [Theory]
    [InlineData("../../etc")]
    [InlineData(@"a\b")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    public void Path_separators_cannot_escape_the_target_folder(string context)
    {
        // A hand-typed value reaching Path.Combine unescaped would write the script somewhere else.
        var name = ScriptFileName.Suggest(context, null, null, false);

        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
        Assert.Equal(name, Path.GetFileName(name));
    }

    [Fact]
    public void Very_long_names_are_trimmed_but_keep_their_extension()
    {
        var name = ScriptFileName.Suggest(new string('x', 400), new string('y', 400), null, false);

        Assert.True(name.Length <= 124, $"was {name.Length}");
        Assert.EndsWith(".sql", name);
    }
}
