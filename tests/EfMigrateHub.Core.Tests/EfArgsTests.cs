using EfMigrateHub.Core;

namespace EfMigrateHub.Core.Tests;

public class EfArgsTests
{
    private static readonly EfTarget Target = new(
        Project: @"C:\my repo\src\Data\Data.csproj",
        StartupProject: @"C:\my repo\src\Api\Api.csproj",
        Context: "BlogContext");

    [Fact]
    public void Every_command_asks_for_prefixed_uncoloured_output()
    {
        List<List<string>> commands =
        [
            EfArgs.MigrationsList(Target),
            EfArgs.MigrationsAdd(Target, "AddThing"),
            EfArgs.MigrationsRemove(Target),
            EfArgs.MigrationsScript(Target, "out.sql"),
            EfArgs.DatabaseUpdate(Target),
            EfArgs.DatabaseDrop(Target),
            EfArgs.DbContextList(Target),
            EfArgs.DbContextInfo(Target),
        ];

        Assert.All(commands, args =>
        {
            Assert.Equal("ef", args[0]);
            Assert.Contains("--prefix-output", args);
            Assert.Contains("--no-color", args);
            Assert.Equal(@"C:\my repo\src\Data\Data.csproj", ArgAfter(args, "--project"));
            Assert.Equal(@"C:\my repo\src\Api\Api.csproj", ArgAfter(args, "--startup-project"));
            Assert.Equal("BlogContext", ArgAfter(args, "--context"));
        });
    }

    [Fact]
    public void Json_is_requested_only_where_the_cli_supports_it()
    {
        Assert.Contains("--json", EfArgs.MigrationsList(Target));
        Assert.Contains("--json", EfArgs.DbContextList(Target));
        Assert.Contains("--json", EfArgs.DbContextInfo(Target));

        Assert.DoesNotContain("--json", EfArgs.MigrationsAdd(Target, "X"));
        Assert.DoesNotContain("--json", EfArgs.DatabaseUpdate(Target));
        Assert.DoesNotContain("--json", EfArgs.MigrationsScript(Target, "out.sql"));
    }

    [Fact]
    public void Positional_arguments_come_immediately_after_the_verb()
    {
        var add = EfArgs.MigrationsAdd(Target, "AddBlogUrl");
        Assert.Equal(["ef", "migrations", "add", "AddBlogUrl"], add.Take(4));

        var update = EfArgs.DatabaseUpdate(Target, "InitialCreate");
        Assert.Equal(["ef", "database", "update", "InitialCreate"], update.Take(4));

        var script = EfArgs.MigrationsScript(Target, "out.sql", from: "0", to: "AddBlogUrl");
        Assert.Equal(["ef", "migrations", "script", "0", "AddBlogUrl"], script.Take(5));
    }

    [Fact]
    public void Database_update_to_latest_passes_no_target()
    {
        Assert.Equal(["ef", "database", "update"], EfArgs.DatabaseUpdate(Target).Take(3));
        Assert.Equal(["ef", "database", "update"], EfArgs.DatabaseUpdate(Target, "  ").Take(3));
    }

    [Fact]
    public void Database_drop_always_forces_so_it_cannot_block_on_a_stdin_prompt()
    {
        // Without --force the CLI asks for confirmation on stdin and a GUI-launched process hangs
        // forever. The user-facing confirmation is the app's job.
        Assert.Contains("--force", EfArgs.DatabaseDrop(Target));
        Assert.DoesNotContain("--dry-run", EfArgs.DatabaseDrop(Target));
        Assert.Contains("--dry-run", EfArgs.DatabaseDrop(Target, dryRun: true));
    }

    [Fact]
    public void Script_always_writes_to_a_file()
    {
        var args = EfArgs.MigrationsScript(Target, @"C:\out\up.sql", idempotent: true);

        Assert.Equal(@"C:\out\up.sql", ArgAfter(args, "--output"));
        Assert.Contains("--idempotent", args);
        Assert.DoesNotContain("--idempotent", EfArgs.MigrationsScript(Target, "out.sql"));
    }

    [Fact]
    public void Script_rejects_a_to_migration_without_a_from()
    {
        // The CLI takes FROM and TO positionally, so a lone TO would silently be read as FROM and
        // generate the wrong script.
        Assert.Throws<ArgumentException>(() =>
            EfArgs.MigrationsScript(Target, "out.sql", from: null, to: "AddBlogUrl"));
    }

    [Fact]
    public void No_connect_is_opt_in()
    {
        Assert.DoesNotContain("--no-connect", EfArgs.MigrationsList(Target));
        Assert.Contains("--no-connect", EfArgs.MigrationsList(Target, noConnect: true));
    }

    [Fact]
    public void No_build_is_opt_in_and_optional_target_fields_are_omitted_when_unset()
    {
        var minimal = EfArgs.MigrationsList(new EfTarget("Data.csproj"));

        Assert.DoesNotContain("--no-build", minimal);
        Assert.DoesNotContain("--startup-project", minimal);
        Assert.DoesNotContain("--context", minimal);
        Assert.DoesNotContain("--configuration", minimal);
        Assert.DoesNotContain("--framework", minimal);

        var full = EfArgs.MigrationsList(new EfTarget(
            "Data.csproj", NoBuild: true, Configuration: "Release", Framework: "net10.0"));

        Assert.Contains("--no-build", full);
        Assert.Equal("Release", ArgAfter(full, "--configuration"));
        Assert.Equal("net10.0", ArgAfter(full, "--framework"));
    }

    [Fact]
    public void Add_requires_a_migration_name()
    {
        Assert.Throws<ArgumentException>(() => EfArgs.MigrationsAdd(Target, "   "));
    }

    [Fact]
    public void Output_dir_is_passed_through_when_supplied()
    {
        Assert.Equal(
            "Persistence/Migrations",
            ArgAfter(EfArgs.MigrationsAdd(Target, "X", "Persistence/Migrations"), "--output-dir"));
    }

    private static string? ArgAfter(List<string> args, string flag)
    {
        var index = args.IndexOf(flag);
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }
}
