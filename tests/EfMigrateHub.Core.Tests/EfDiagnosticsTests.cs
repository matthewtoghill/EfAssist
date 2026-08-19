using EfMigrateHub.Core;

namespace EfMigrateHub.Core.Tests;

/// <summary>
/// Every mapped failure was reproduced deliberately against a real project and its output captured
/// as a fixture — these tests replay those captures, so they check the mapping against EF's actual
/// wording rather than against wording invented here. The reproduction recipes are in
/// PROGRESS.md.
/// </summary>
public class EfDiagnosticsTests
{
    [Theory]
    [InlineData("error-no-project", "could not find or read the project")]
    [InlineData("error-no-project-found", "could not find or read the project")]
    [InlineData("error-no-dbcontext", "cannot host the EF tools")]
    [InlineData("error-migrations-assembly", "do not agree")]
    [InlineData("error-connection", "could not be reached")]
    [InlineData("error-build-failed", "did not compile")]
    [InlineData("error-dbcontext-create", "could not construct the DbContext")]
    public void Recognises_a_reproduced_failure(string fixture, string expectedInTitle)
    {
        var diagnosis = EfDiagnostics.Diagnose(Fixture.Load(fixture));

        Assert.NotNull(diagnosis);
        Assert.Contains(expectedInTitle, diagnosis.Title, StringComparison.OrdinalIgnoreCase);
        Assert.False(diagnosis.IsWarning);
        Assert.NotEmpty(diagnosis.Guidance);
    }

    [Fact]
    public void Reports_a_tool_version_gap_even_though_the_command_succeeded()
    {
        // Captured from an EF 8 tool listing an EF 10 project: EF warns and carries on, and the
        // applied state it returns is wrong. A failure-only check would say nothing at all.
        var result = Fixture.Load("warn-tools-version");
        Assert.True(result.Success);

        var diagnosis = EfDiagnostics.Diagnose(result);

        Assert.NotNull(diagnosis);
        Assert.True(diagnosis.IsWarning);
        Assert.Contains("different version", diagnosis.Title);
    }

    [Fact]
    public void Says_nothing_about_a_clean_success()
    {
        Assert.Null(EfDiagnostics.Diagnose(Fixture.Load("migrations-list-mixed")));
    }

    [Fact]
    public void Says_nothing_about_a_failure_it_does_not_recognise()
    {
        Assert.Null(EfDiagnostics.Diagnose(Fixture.Load("error-unknown-context")));
    }

    [Fact]
    public void Does_not_match_on_a_phrase_buried_in_the_build_log()
    {
        // The info lines carry stack traces and the whole build log. Matching those would turn any
        // command whose output mentions a phrase in passing into a confident wrong answer.
        var result = new EfResult(
            1,
            [
                new OutputLine(OutputChannel.Info, "Restoring Contoso.BuildFailed.Tests..."),
                new OutputLine(OutputChannel.Error, "No DbContext named 'Nope' was found."),
            ],
            "dotnet ef migrations list",
            "/tmp");

        Assert.Null(EfDiagnostics.Diagnose(result));
    }

    [Fact]
    public void Prefers_the_connection_failure_over_the_wrapper_it_arrives_in()
    {
        // A database that cannot be reached surfaces as "Unable to create a 'DbContext'" with the
        // real cause inside it. Reporting the wrapper would send the user to look at design-time
        // factories for what is a connection string problem.
        var result = new EfResult(
            1,
            [
                new OutputLine(
                    OutputChannel.Error,
                    "Unable to create a 'DbContext' of type ''. The exception 'Login failed for "
                    + "user 'sa'.' was thrown while attempting to create an instance."),
            ],
            "dotnet ef migrations list",
            "/tmp");

        Assert.Equal("The database could not be reached.", EfDiagnostics.Diagnose(result)?.Title);
    }

    [Fact]
    public void Falls_back_to_unprefixed_output_so_msbuild_failures_are_still_matched()
    {
        // MSBuild writes without EF's prefixes, so these arrive on the Raw channel.
        var result = new EfResult(
            1,
            [new OutputLine(OutputChannel.Raw, "MSBUILD : error MSB1003: Specify a project or solution file.")],
            "dotnet ef migrations list",
            "/tmp");

        Assert.NotNull(EfDiagnostics.Diagnose(result));
    }

    [Fact]
    public void Recognises_pending_model_changes_as_an_expected_outcome_not_a_failure()
    {
        var result = new EfResult(
            1,
            [
                new OutputLine(
                    OutputChannel.Error,
                    "Changes have been made to the model since the last migration. Add a new migration."),
            ],
            "dotnet ef migrations has-pending-model-changes",
            "/tmp");

        Assert.True(EfDiagnostics.IsPendingModelChanges(result));

        // Not a recognised failure rule, so Diagnose must not report it as one.
        Assert.Null(EfDiagnostics.Diagnose(result));
    }

    [Fact]
    public void Does_not_treat_a_clean_success_or_an_unrelated_failure_as_pending_changes()
    {
        Assert.False(EfDiagnostics.IsPendingModelChanges(Fixture.Load("migrations-list-mixed")));
        Assert.False(EfDiagnostics.IsPendingModelChanges(Fixture.Load("error-unknown-context")));
    }
}
