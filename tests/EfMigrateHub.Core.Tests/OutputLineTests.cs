using EfMigrateHub.Core;

namespace EfMigrateHub.Core.Tests;

public class OutputLineTests
{
    [Theory]
    [InlineData("info:    Build started...", OutputChannel.Info, "Build started...")]
    [InlineData("data:    [", OutputChannel.Data, "[")]
    [InlineData("warn:    something", OutputChannel.Warn, "something")]
    [InlineData("error:   No DbContext named 'X' was found.", OutputChannel.Error, "No DbContext named 'X' was found.")]
    public void Splits_the_fixed_width_prefix_field(string line, OutputChannel channel, string text)
    {
        var parsed = OutputLine.Parse(line);

        Assert.Equal(channel, parsed.Channel);
        Assert.Equal(text, parsed.Text);
    }

    [Fact]
    public void Preserves_payload_indentation()
    {
        // Generated SQL carries its own indentation immediately after the 9-character prefix field.
        // Trimming here would silently reformat every script the app shows.
        var parsed = OutputLine.Parse("data:        \"MigrationId\" TEXT NOT NULL");

        Assert.Equal(OutputChannel.Data, parsed.Channel);
        Assert.Equal("    \"MigrationId\" TEXT NOT NULL", parsed.Text);
    }

    [Fact]
    public void Blank_payload_line_is_an_empty_string_not_a_dropped_line()
    {
        var parsed = OutputLine.Parse("data:    ");

        Assert.Equal(OutputChannel.Data, parsed.Channel);
        Assert.Equal("", parsed.Text);
    }

    [Fact]
    public void Unprefixed_msbuild_output_is_raw_not_a_failure()
    {
        const string msbuild =
            @"C:\Repos\EfMigrateHub\samples\SampleEfApp\SampleEfApp.csproj : warning NU1903: Package 'x' has a known high severity vulnerability";

        var parsed = OutputLine.Parse(msbuild);

        Assert.Equal(OutputChannel.Raw, parsed.Channel);
        Assert.Equal(msbuild, parsed.Text);
    }

    [Fact]
    public void Text_that_merely_starts_with_a_token_is_not_treated_as_prefixed()
    {
        var parsed = OutputLine.Parse("error: not padded to the prefix width");

        Assert.Equal(OutputChannel.Raw, parsed.Channel);
    }
}
