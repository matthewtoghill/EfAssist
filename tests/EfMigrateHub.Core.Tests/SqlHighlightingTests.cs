using Avalonia.Styling;
using AvaloniaEdit.Highlighting;
using EfMigrateHub.App;

namespace EfMigrateHub.Core.Tests;

/// <summary>
/// The syntax definitions are embedded resources loaded by name and parsed at runtime, so a typo in
/// either the file or the resource path is a crash on first sight of the Script tab rather than a
/// build error. These tests are what turns that back into a build-time failure.
/// </summary>
public class SqlHighlightingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Loads_a_definition_for_every_variant(string? variant)
    {
        var definition = SqlHighlighting.For(Variant(variant));

        Assert.NotNull(definition);
        Assert.NotEmpty(definition.MainRuleSet.Spans);
    }

    [Fact]
    public void Light_and_dark_are_different_definitions()
    {
        // The whole reason there are two files: one shared definition could not repaint on a theme
        // switch, because a HighlightingColor holds a literal colour.
        Assert.NotSame(
            SqlHighlighting.For(ThemeVariant.Light),
            SqlHighlighting.For(ThemeVariant.Dark));
    }

    [Fact]
    public void Anything_other_than_light_gets_the_dark_definition()
    {
        // ActualThemeVariant resolves Default to Light or Dark before we see it, so this is the
        // safety net for a null or an unexpected variant rather than the normal path.
        Assert.Same(
            SqlHighlighting.For(ThemeVariant.Dark),
            SqlHighlighting.For(null));
    }

    [Fact]
    public void Repeated_calls_reuse_the_parsed_definition()
    {
        Assert.Same(
            SqlHighlighting.For(ThemeVariant.Light),
            SqlHighlighting.For(ThemeVariant.Light));
    }

    [Theory]
    [InlineData("Comment")]
    [InlineData("Char")]
    [InlineData("Identifier")]
    [InlineData("Digits")]
    [InlineData("Keywords")]
    public void Both_variants_colour_the_same_named_roles(string role)
    {
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var colour = SqlHighlighting.For(variant).GetNamedColor(role);

            Assert.NotNull(colour);
            Assert.NotNull(colour!.Foreground);
        }
    }

    [Fact]
    public void The_two_variants_do_not_share_a_colour()
    {
        // A value copied across from the other file by accident would read badly on one background
        // and there is nothing on screen to catch it, since both variants still highlight.
        foreach (var role in new[] { "Comment", "Char", "Identifier", "Digits", "Keywords" })
        {
            Assert.NotEqual(
                Foreground(ThemeVariant.Light, role),
                Foreground(ThemeVariant.Dark, role));
        }
    }

    [Theory]
    [InlineData("SELECT", "Keywords")]
    [InlineData("select", "Keywords")]
    [InlineData("-- a comment", "Comment")]
    [InlineData("'a literal'", "Char")]
    [InlineData("[dbo]", "Identifier")]
    [InlineData("\"Blogs\"", "Identifier")]
    [InlineData("42", "Digits")]
    [InlineData("3.14", "Digits")]
    public void Colours_the_constructs_that_appear_in_generated_sql(string sql, string role)
    {
        // Provider coverage in one place: [dbo] is SQL Server's quoting, "Blogs" is PostgreSQL's and
        // EF's SQLite output, and both must read as identifiers rather than as string literals.
        var definition = SqlHighlighting.For(ThemeVariant.Dark);
        var highlighter = new DocumentHighlighter(
            new AvaloniaEdit.Document.TextDocument(sql), definition);

        var sections = highlighter.HighlightLine(1).Sections;

        Assert.Contains(sections, s => s.Color.Name == role);
    }

    [Fact]
    public void A_go_batch_separator_is_a_keyword()
    {
        // EF's SQL Server scripts are split by GO, so it is the one keyword worth pinning.
        var definition = SqlHighlighting.For(ThemeVariant.Dark);
        var highlighter = new DocumentHighlighter(
            new AvaloniaEdit.Document.TextDocument("GO"), definition);

        Assert.Contains(
            highlighter.HighlightLine(1).Sections,
            s => s.Color.Name == "Keywords");
    }

    private static string Foreground(ThemeVariant variant, string role) =>
        SqlHighlighting.For(variant).GetNamedColor(role)!.Foreground!.ToString()!;

    private static ThemeVariant? Variant(string? name) => name switch
    {
        "Light" => ThemeVariant.Light,
        "Dark" => ThemeVariant.Dark,
        _ => null,
    };
}
