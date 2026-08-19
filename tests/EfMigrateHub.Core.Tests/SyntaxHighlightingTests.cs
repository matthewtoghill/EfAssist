using Avalonia.Styling;
using AvaloniaEdit.Highlighting;
using EfMigrateHub.App;

namespace EfMigrateHub.Core.Tests;

/// <summary>
/// The syntax definitions are embedded resources loaded by name and parsed at runtime, so a typo in
/// either the file or the resource path is a crash on first sight of the Script tab rather than a
/// build error. These tests are what turns that back into a build-time failure.
/// </summary>
public class SyntaxHighlightingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Loads_a_definition_for_every_variant(string? variant)
    {
        var definition = SyntaxHighlighting.Sql(Variant(variant));

        Assert.NotNull(definition);
        Assert.NotEmpty(definition.MainRuleSet.Spans);
    }

    [Fact]
    public void Light_and_dark_are_different_definitions()
    {
        // The whole reason there are two files: one shared definition could not repaint on a theme
        // switch, because a HighlightingColor holds a literal colour.
        Assert.NotSame(
            SyntaxHighlighting.Sql(ThemeVariant.Light),
            SyntaxHighlighting.Sql(ThemeVariant.Dark));
    }

    [Fact]
    public void Anything_other_than_light_gets_the_dark_definition()
    {
        // ActualThemeVariant resolves Default to Light or Dark before we see it, so this is the
        // safety net for a null or an unexpected variant rather than the normal path.
        Assert.Same(
            SyntaxHighlighting.Sql(ThemeVariant.Dark),
            SyntaxHighlighting.Sql(null));
    }

    [Fact]
    public void Repeated_calls_reuse_the_parsed_definition()
    {
        Assert.Same(
            SyntaxHighlighting.Sql(ThemeVariant.Light),
            SyntaxHighlighting.Sql(ThemeVariant.Light));
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
            var colour = SyntaxHighlighting.Sql(variant).GetNamedColor(role);

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
        var definition = SyntaxHighlighting.Sql(ThemeVariant.Dark);
        var highlighter = new DocumentHighlighter(
            new AvaloniaEdit.Document.TextDocument(sql), definition);

        var sections = highlighter.HighlightLine(1).Sections;

        Assert.Contains(sections, s => s.Color.Name == role);
    }

    [Fact]
    public void A_go_batch_separator_is_a_keyword()
    {
        // EF's SQL Server scripts are split by GO, so it is the one keyword worth pinning.
        var definition = SyntaxHighlighting.Sql(ThemeVariant.Dark);
        var highlighter = new DocumentHighlighter(
            new AvaloniaEdit.Document.TextDocument("GO"), definition);

        Assert.Contains(
            highlighter.HighlightLine(1).Sections,
            s => s.Color.Name == "Keywords");
    }

    private static string Foreground(ThemeVariant variant, string role) =>
        SyntaxHighlighting.Sql(variant).GetNamedColor(role)!.Foreground!.ToString()!;

    [Theory]
    [InlineData("Comment")]
    [InlineData("String")]
    [InlineData("Types")]
    [InlineData("Digits")]
    [InlineData("Keywords")]
    public void The_csharp_definition_colours_its_roles_in_both_variants(string role)
    {
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var colour = SyntaxHighlighting.CSharp(variant).GetNamedColor(role);

            Assert.NotNull(colour);
            Assert.NotNull(colour!.Foreground);
        }
    }

    [Theory]
    [InlineData("public class Foo", "Keywords")]
    [InlineData("string name;", "Types")]
    [InlineData("// a comment", "Comment")]
    [InlineData("/// <inheritdoc />", "Comment")]
    [InlineData("\"Blogs\"", "String")]
    [InlineData("@\"C:\temp\"", "String")]
    [InlineData("42", "Digits")]
    public void Colours_the_constructs_that_appear_in_a_migration_file(string source, string role)
    {
        var highlighter = new DocumentHighlighter(
            new AvaloniaEdit.Document.TextDocument(source),
            SyntaxHighlighting.CSharp(ThemeVariant.Dark));

        Assert.Contains(highlighter.HighlightLine(1).Sections, s => s.Color.Name == role);
    }

    [Fact]
    public void A_doubled_quote_does_not_end_a_verbatim_string()
    {
        // EF writes table and column names into migrations as verbatim strings, and a doubled quote
        // inside one would otherwise be read as the end of the string and colour the rest of the
        // line as code.
        var source = "name: @\"He said \"\"hi\"\" here\", nullable: true";
        var highlighter = new DocumentHighlighter(
            new AvaloniaEdit.Document.TextDocument(source),
            SyntaxHighlighting.CSharp(ThemeVariant.Dark));

        var stringSection = Assert.Single(
            highlighter.HighlightLine(1).Sections,
            s => s.Color.Name == "String");

        Assert.Equal(source.IndexOf("@\"", StringComparison.Ordinal), stringSection.Offset);
        Assert.Equal(source.IndexOf("\", nullable", StringComparison.Ordinal) + 1,
            stringSection.Offset + stringSection.Length);
    }

    [Fact]
    public void Sql_and_csharp_are_different_definitions()
    {
        Assert.NotSame(
            SyntaxHighlighting.Sql(ThemeVariant.Dark),
            SyntaxHighlighting.CSharp(ThemeVariant.Dark));
    }

    private static ThemeVariant? Variant(string? name) => name switch
    {
        "Light" => ThemeVariant.Light,
        "Dark" => ThemeVariant.Dark,
        _ => null,
    };
}
