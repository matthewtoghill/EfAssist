using EfAssist.Core;

namespace EfAssist.Core.Tests;

public class EfJsonTests
{
    [Fact]
    public void Reads_applied_and_pending_migrations()
    {
        var migrations = EfJson.Migrations(Fixture.Load("migrations-list-mixed"));

        Assert.NotNull(migrations);
        Assert.Equal(2, migrations.Count);

        Assert.Equal("20260818140933_InitialCreate", migrations[0].Id);
        Assert.Equal("InitialCreate", migrations[0].Name);
        Assert.Equal(MigrationState.Applied, migrations[0].State);

        Assert.Equal("AddBlogUrl", migrations[1].Name);
        Assert.Equal(MigrationState.Pending, migrations[1].State);
    }

    [Fact]
    public void Offline_applied_null_maps_to_Unknown_never_Pending()
    {
        // --no-connect returns "applied": null. Rendering that as "pending" would tell the user
        // a migration has not been applied when we simply have not looked.
        var migrations = EfJson.Migrations(Fixture.Load("migrations-list-noconnect"));

        Assert.NotNull(migrations);
        Assert.NotEmpty(migrations);
        Assert.All(migrations, m => Assert.Equal(MigrationState.Unknown, m.State));
        Assert.All(migrations, m => Assert.Null(m.Applied));
    }

    [Fact]
    public void Empty_migration_list_is_an_empty_result_not_a_failure()
    {
        var result = Fixture.Load("migrations-list-empty");
        var migrations = EfJson.Migrations(result);

        Assert.True(result.Success);
        Assert.NotNull(migrations);
        Assert.Empty(migrations);
    }

    [Fact]
    public void Reads_every_context_from_dbcontext_list()
    {
        var contexts = EfJson.Contexts(Fixture.Load("dbcontext-list"));

        Assert.NotNull(contexts);
        Assert.Equal(
            ["BlogContext", "AuditContext"],
            contexts.Select(c => c.Name));
        Assert.Equal("SampleEfApp.BlogContext", contexts[0].FullName);
        Assert.Contains("SampleEfApp, Version=", contexts[0].AssemblyQualifiedName);
    }

    [Fact]
    public void Reads_provider_and_data_source_from_dbcontext_info()
    {
        var details = EfJson.ContextDetails(Fixture.Load("dbcontext-info"));

        Assert.NotNull(details);
        Assert.Equal("SampleEfApp.BlogContext", details.Type);
        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", details.ProviderName);
        Assert.Equal("main", details.DatabaseName);
        Assert.Equal("blog.db", details.DataSource);
    }

    [Fact]
    public void Confirmation_name_falls_back_to_data_source_when_database_name_is_generic()
    {
        // SQLite always reports "main", which would make the drop confirmation meaningless.
        var sqlite = EfJson.ContextDetails(Fixture.Load("dbcontext-info"))!;
        Assert.Equal("blog.db", sqlite.ConfirmationName);

        var sqlServer = new DbContextDetails(
            "X", "Microsoft.EntityFrameworkCore.SqlServer", "AppDb", "localhost", "None");
        Assert.Equal("AppDb", sqlServer.ConfirmationName);
    }

    [Fact]
    public void Sqlite_is_known_not_to_support_idempotent_scripts()
    {
        var sqlite = EfJson.ContextDetails(Fixture.Load("dbcontext-info"))!;
        Assert.False(sqlite.SupportsIdempotentScripts);

        var sqlServer = new DbContextDetails(
            "X", "Microsoft.EntityFrameworkCore.SqlServer", "AppDb", "localhost", "None");
        Assert.True(sqlServer.SupportsIdempotentScripts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ \"unclosed\": ")]
    public void Malformed_or_empty_payloads_return_null_instead_of_throwing(string payload)
    {
        Assert.Null(EfJson.Deserialize<List<MigrationInfo>>(payload));
    }

    [Fact]
    public void Unknown_fields_do_not_break_parsing()
    {
        // EF adding a field in a future release must not take the app down.
        const string json = """
            [{ "id": "1_A", "name": "A", "safeName": "A", "applied": true, "somethingNew": 42 }]
            """;

        var migrations = EfJson.Deserialize<List<MigrationInfo>>(json);

        Assert.NotNull(migrations);
        Assert.Equal(MigrationState.Applied, migrations[0].State);
    }
}
