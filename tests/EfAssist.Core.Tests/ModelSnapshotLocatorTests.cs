using EfAssist.Core.Diagrams;

namespace EfAssist.Core.Tests;

public class ModelSnapshotLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "EfAssistTests", Guid.NewGuid().ToString("N")[..8]);

    public ModelSnapshotLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [Fact]
    public void FindsTheSnapshotForTheNamedContext()
    {
        Snapshot("Migrations/BlogContextModelSnapshot.cs", "BlogContext");
        Snapshot("Migrations/AuditContextModelSnapshot.cs", "AuditContext");

        var found = ModelSnapshotLocator.Find(_root, "AuditContext");

        Assert.NotNull(found);
        Assert.EndsWith("AuditContextModelSnapshot.cs", found);
    }

    [Fact]
    public void MatchesOnTheAttributeRatherThanTheFileName()
    {
        // A renamed context leaves the generated file named after the old one, so the file name is a
        // convention and the attribute is the fact.
        Snapshot("Migrations/OldNameModelSnapshot.cs", "RenamedContext");

        Assert.NotNull(ModelSnapshotLocator.Find(_root, "RenamedContext"));
        Assert.Null(ModelSnapshotLocator.Find(_root, "OldName"));
    }

    [Fact]
    public void AcceptsAFullyQualifiedContextName()
    {
        // dbcontext list --json returns the full name; the attribute carries the short one.
        Snapshot("Migrations/BlogContextModelSnapshot.cs", "BlogContext");

        Assert.NotNull(ModelSnapshotLocator.Find(_root, "SampleEfApp.Data.BlogContext"));
    }

    [Fact]
    public void AcceptsAProjectFileRatherThanAFolder()
    {
        Snapshot("Migrations/BlogContextModelSnapshot.cs", "BlogContext");
        File.WriteAllText(Path.Combine(_root, "Sample.csproj"), "<Project />");

        Assert.NotNull(ModelSnapshotLocator.Find(
            Path.Combine(_root, "Sample.csproj"), "BlogContext"));
    }

    [Fact]
    public void ReturnsNullWhenTheContextHasNoMigrations()
    {
        Snapshot("Migrations/BlogContextModelSnapshot.cs", "BlogContext");

        // The empty state, not an error: a context with no migrations has no snapshot to draw.
        Assert.Null(ModelSnapshotLocator.Find(_root, "AuditContext"));
    }

    [Fact]
    public void ReturnsNullWhenThereAreNoSnapshotsAtAll() =>
        Assert.Null(ModelSnapshotLocator.Find(_root, "BlogContext"));

    [Fact]
    public void ReturnsNullForAPathThatDoesNotExist() =>
        Assert.Null(ModelSnapshotLocator.Find(Path.Combine(_root, "nope"), "BlogContext"));

    [Fact]
    public void ReturnsNullForAnEmptyPath() => Assert.Null(ModelSnapshotLocator.Find("", "Blog"));

    [Fact]
    public void FallsBackToTheOnlySnapshotWhenNoContextIsGiven()
    {
        // The context dropdown costs a build to populate, so the tab has to work before it has.
        Snapshot("Migrations/BlogContextModelSnapshot.cs", "BlogContext");

        Assert.NotNull(ModelSnapshotLocator.Find(_root, null));
    }

    [Fact]
    public void RefusesToGuessBetweenTwoSnapshotsWhenNoContextIsGiven()
    {
        Snapshot("Migrations/BlogContextModelSnapshot.cs", "BlogContext");
        Snapshot("Migrations/AuditContextModelSnapshot.cs", "AuditContext");

        Assert.Null(ModelSnapshotLocator.Find(_root, null));
    }

    [Fact]
    public void IgnoresBuildOutput()
    {
        // A stale copy under obj/ would otherwise win on a project whose Migrations folder was moved.
        Snapshot("obj/Debug/net10.0/BlogContextModelSnapshot.cs", "BlogContext");
        Snapshot("bin/Debug/BlogContextModelSnapshot.cs", "BlogContext");

        Assert.Null(ModelSnapshotLocator.Find(_root, "BlogContext"));
    }

    [Fact]
    public void SearchesNestedFolders()
    {
        Snapshot("Data/Migrations/Sql/BlogContextModelSnapshot.cs", "BlogContext");

        Assert.NotNull(ModelSnapshotLocator.Find(_root, "BlogContext"));
    }

    [Fact]
    public void FindsTheDesignerSnapshotForAMigration()
    {
        Snapshot("Migrations/20260101000000_Init.Designer.cs", "BlogContext");
        File.WriteAllText(
            Path.Combine(_root, "Migrations", "20260101000000_Init.cs"), "// migration");

        var found = ModelSnapshotLocator.FindForMigration(_root, "20260101000000_Init");

        Assert.NotNull(found);
        Assert.EndsWith(".Designer.cs", found);
    }

    [Fact]
    public void ReturnsNullForAMigrationWithNoDesignerFile()
    {
        File.WriteAllText(Path.Combine(_root, "Handwritten.cs"), "// not a migration");

        Assert.Null(ModelSnapshotLocator.FindForMigration(_root, "Handwritten"));
    }

    private void Snapshot(string relativePath, string contextName)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        File.WriteAllText(path, $$"""
            // <auto-generated />
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Infrastructure;

            namespace Sample.Migrations
            {
                [DbContext(typeof({{contextName}}))]
                partial class Snapshot : ModelSnapshot
                {
                    protected override void BuildModel(ModelBuilder modelBuilder)
                    {
                        modelBuilder.Entity("Sample.Thing", b =>
                            {
                                b.Property<int>("Id").HasColumnType("int");
                                b.HasKey("Id");
                                b.ToTable("Things");
                            });
                    }
                }
            }
            """);
    }
}
