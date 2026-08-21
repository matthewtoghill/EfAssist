using EfAssist.Core;

namespace EfAssist.Core.Tests;

public class MigrationNameTests
{
    [Theory]
    [InlineData("InitialCreate")]
    [InlineData("Add_Blog_Url")]
    [InlineData("_Internal")]
    [InlineData("Migration2")]
    public void Accepts_names_that_are_valid_class_names(string name) =>
        Assert.Null(MigrationName.Validate(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_an_empty_name(string? name) =>
        Assert.NotNull(MigrationName.Validate(name));

    [Fact]
    public void Rejects_a_name_starting_with_a_digit()
    {
        // EF turns the name into a class name, so this would fail after a full build.
        Assert.Contains("must start with", MigrationName.Validate("2ndMigration"));
    }

    [Theory]
    [InlineData("Add Blog Url")]
    [InlineData("Add-Blog")]
    [InlineData("Add.Blog")]
    [InlineData("Add/Blog")]
    public void Rejects_characters_that_are_not_valid_in_an_identifier(string name) =>
        Assert.Contains("not allowed", MigrationName.Validate(name));

    [Fact]
    public void Rejects_a_csharp_keyword() =>
        Assert.Contains("keyword", MigrationName.Validate("class"));

    [Fact]
    public void Rejects_a_name_already_in_use_regardless_of_case()
    {
        string[] existing = ["InitialCreate", "AddBlogUrl"];

        Assert.Contains("already exists", MigrationName.Validate("AddBlogUrl", existing));
        Assert.Contains("already exists", MigrationName.Validate("addblogurl", existing));
        Assert.Null(MigrationName.Validate("AddPostTitle", existing));
    }

    [Fact]
    public void Rejects_surrounding_whitespace_rather_than_silently_trimming_it() =>
        Assert.Contains("space", MigrationName.Validate(" InitialCreate "));
}
