using EfMigrateHub.Core;

namespace EfMigrateHub.Core.Tests;

public class EfResultTests
{
    [Fact]
    public void Error_message_is_the_error_line_not_the_stack_trace()
    {
        // The failed idempotent-script capture has the full exception dumped across info: lines,
        // then the human-readable message on the error: line. Only the latter belongs in the UI.
        var result = Fixture.Load("script-idempotent");

        Assert.False(result.Success);
        Assert.Equal(
            "Generating idempotent scripts for migrations is not currently supported for SQLite. "
            + "See https://go.microsoft.com/fwlink/?LinkId=723262 for more information and examples.",
            result.ErrorMessage);
        Assert.DoesNotContain("at Microsoft.EntityFrameworkCore", result.ErrorMessage);
    }

    [Fact]
    public void Error_message_reads_cleanly_for_a_wrong_startup_project()
    {
        var result = Fixture.Load("error-no-dbcontext");

        Assert.False(result.Success);
        Assert.StartsWith("Your startup project 'EfMigrateHub.Core' doesn't reference", result.ErrorMessage);
    }

    [Fact]
    public void Error_message_reads_cleanly_for_an_unknown_context()
    {
        var result = Fixture.Load("error-unknown-context");

        Assert.False(result.Success);
        Assert.Equal("No DbContext named 'NoSuchContext' was found.", result.ErrorMessage);
    }

    [Fact]
    public void Data_reassembles_generated_sql_with_its_indentation()
    {
        var sql = Fixture.Load("script-plain").Data;

        Assert.Contains("CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" (", sql);
        Assert.Contains("    \"ProductVersion\" TEXT NOT NULL", sql);
        Assert.Contains("BEGIN TRANSACTION;", sql);
    }

    [Fact]
    public void Diagnostics_includes_the_command_directory_and_exit_code()
    {
        var diagnostics = Fixture.Load("error-unknown-context").Diagnostics;

        Assert.Contains("Command:", diagnostics);
        Assert.Contains("Directory:", diagnostics);
        Assert.Contains("Exit code: 1", diagnostics);
        Assert.Contains("No DbContext named 'NoSuchContext' was found.", diagnostics);
    }

    [Fact]
    public void Falls_back_to_unprefixed_output_when_there_is_no_error_line()
    {
        // An MSBuild failure never reaches EF, so nothing is prefixed. Showing nothing at all
        // would leave the user staring at a failed command with no explanation.
        var result = new EfResult(
            1,
            [new OutputLine(OutputChannel.Raw, "Foo.csproj(3,5): error CS1002: ; expected")],
            "dotnet ef migrations list",
            ".");

        Assert.Contains("error CS1002", result.ErrorMessage);
    }
}
