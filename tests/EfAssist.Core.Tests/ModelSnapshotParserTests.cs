using EfAssist.Core.Diagrams;

namespace EfAssist.Core.Tests;

/// <summary>
/// Parses the captured model snapshots in <c>Fixtures/</c>. <c>snapshot-rich</c> and
/// <c>snapshot-simple</c> are real EF 10.0.10 output from <c>samples/SampleRichModel</c> and
/// <c>samples/SampleEfApp</c>; the other two are hand-written to cover formatting EF only produces
/// in situations those samples cannot reach.
/// </summary>
public class ModelSnapshotParserTests
{
    private static DiagramModel Rich() => ModelSnapshotParser.Parse(Fixture.Text("snapshot-rich"));

    private static DiagramModel Simple() => ModelSnapshotParser.Parse(Fixture.Text("snapshot-simple"));

    // ---- Context and version ----

    [Fact]
    public void ReadsContextNameFromTheDbContextAttribute()
    {
        Assert.Equal("RichContext", Rich().ContextName);
        Assert.Equal("BlogContext", Simple().ContextName);
    }

    [Fact]
    public void ReadsTheProductVersionAnnotation() => Assert.Equal("10.0.10", Rich().EfVersion);

    [Fact]
    public void RecordsTheSourcePathAndAStableHash()
    {
        var text = Fixture.Text("snapshot-rich");
        var model = ModelSnapshotParser.Parse(text, @"C:\repo\Snapshot.cs");

        Assert.Equal(@"C:\repo\Snapshot.cs", model.SourcePath);
        Assert.Equal(ModelSnapshotParser.Hash(text), model.SourceHash);
        Assert.NotEqual(model.SourceHash, ModelSnapshotParser.Hash(text + " "));
    }

    // ---- Entities ----

    [Fact]
    public void FindsEveryEntityExactlyOnce()
    {
        // The load-bearing assertion of the whole parser. EF writes several
        // modelBuilder.Entity("X", ...) blocks for the same entity — properties in one, foreign keys
        // in a later one, navigations in a later one still. A parser that does not merge them
        // reports Author, Blog, Comment and Post twice each.
        var names = Rich().Entities.Select(e => e.Name).ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Contains("SampleRichModel.Blog", names);
        Assert.Contains("SampleRichModel.Post", names);
        Assert.Contains("PostTag", names);
    }

    [Fact]
    public void MergesPropertiesAndRelationshipsWrittenInSeparateBlocks()
    {
        var post = Rich().Entity("SampleRichModel.Post")!;

        // Properties come from the first Post block, navigations from the last.
        Assert.Contains("Title", post.Properties.Select(p => p.Name));
        Assert.Contains("Comments", post.Navigations);
        Assert.Contains("Blog", post.Navigations);
    }

    [Fact]
    public void ReadsTableNames()
    {
        Assert.Equal("Blogs", Rich().Entity("SampleRichModel.Blog")!.Table);
        Assert.Equal("PostStatistics", Rich().Entity("SampleRichModel.PostStatistics")!.Table);
    }

    [Fact]
    public void TreatsACastNullSchemaAsNoSchema()
    {
        // SampleEfApp's snapshot says ToTable("Blogs", (string)null). Without unwrapping the cast
        // the schema reads as the literal text "null" and the diagram shows "null.Blogs".
        var blog = Simple().Entity("SampleEfApp.Blog")!;

        Assert.Equal("Blogs", blog.Table);
        Assert.Null(blog.Schema);
        Assert.Equal("Blogs", blog.QualifiedTable);
    }

    [Fact]
    public void ReadsSchemaWhenThereIsOne()
    {
        var model = ModelSnapshotParser.Parse(Fixture.Text("snapshot-wrapped-args"));
        var vendor = model.Entity("Wrapped.Models.Vendor")!;

        Assert.Equal("Vendor", vendor.Table);
        Assert.Equal("hr", vendor.Schema);
        Assert.Equal("hr.Vendor", vendor.QualifiedTable);
    }

    [Fact]
    public void SplitsAnEntityNameIntoNamespaceAndShortName()
    {
        var blog = Rich().Entity("SampleRichModel.Blog")!;

        Assert.Equal("Blog", blog.ShortName);
        Assert.Equal("SampleRichModel", blog.Namespace);
    }

    // ---- Properties ----

    [Fact]
    public void ReadsPropertyTypesColumnTypesAndLengths()
    {
        var name = Rich().Entity("SampleRichModel.Blog")!.Properties.Single(p => p.Name == "Name");

        Assert.Equal("string", name.ClrType);
        Assert.Equal("TEXT", name.ColumnType);
        Assert.Equal(200, name.MaxLength);
        Assert.True(name.IsRequired);
    }

    [Fact]
    public void KeepsNullabilityOnTheClrType()
    {
        var blog = Rich().Entity("SampleRichModel.Blog")!;

        Assert.Equal("int?", blog.Properties.Single(p => p.Name == "OwnerId").ClrType);
        Assert.Equal("string", blog.Properties.Single(p => p.Name == "Url").ClrType);
    }

    [Fact]
    public void WorksOutWhetherAColumnIsNullable()
    {
        var blog = Rich().Entity("SampleRichModel.Blog")!;

        // EF only writes IsRequired() where it is not already implied, so nullability needs both
        // that flag and the CLR type to come out right.
        Assert.True(blog.Properties.Single(p => p.Name == "Id").IsNotNull);          // key
        Assert.True(blog.Properties.Single(p => p.Name == "Name").IsNotNull);        // IsRequired()
        Assert.True(blog.Properties.Single(p => p.Name == "CreatedUtc").IsNotNull);  // value type
        Assert.False(blog.Properties.Single(p => p.Name == "OwnerId").IsNotNull);    // int?
        Assert.False(blog.Properties.Single(p => p.Name == "Url").IsNotNull);        // optional string
    }

    [Fact]
    public void ReadsGeneratedValuesAndDefaults()
    {
        var published = Rich().Entity("SampleRichModel.Post")!
            .Properties.Single(p => p.Name == "PublishedUtc");

        Assert.Equal("OnAdd", published.ValueGenerated);
        Assert.Equal("CURRENT_TIMESTAMP", published.DefaultValueSql);
    }

    // ---- Keys and indexes ----

    [Fact]
    public void ReadsSimpleAndCompositeKeys()
    {
        Assert.Equal(["Id"], Rich().Entity("SampleRichModel.Blog")!.Keys);
        Assert.Equal(["BlogId", "PersonId"], Rich().Entity("SampleRichModel.BlogEditor")!.Keys);
    }

    [Fact]
    public void ReadsAlternateKeys()
    {
        var blog = Rich().Entity("SampleRichModel.Blog")!;

        Assert.Equal([["Slug"]], blog.AlternateKeys);
        Assert.True(blog.Properties.Single(p => p.Name == "Slug").IsAlternateKey);
    }

    [Fact]
    public void MarksKeyProperties()
    {
        var editor = Rich().Entity("SampleRichModel.BlogEditor")!;

        Assert.True(editor.Properties.Single(p => p.Name == "BlogId").IsKey);
        Assert.True(editor.Properties.Single(p => p.Name == "PersonId").IsKey);
        Assert.False(editor.Properties.Single(p => p.Name == "Role").IsKey);
    }

    [Fact]
    public void ReadsNamedUniqueAndCompositeIndexes()
    {
        var indexes = Rich().Entity("SampleRichModel.Post")!.Indexes;

        var named = indexes.Single(i => i.DatabaseName == "IX_Post_Title");
        Assert.Equal(["Title"], named.Properties);
        Assert.False(named.IsUnique);

        var composite = indexes.Single(i => i.Properties.Count == 2);
        Assert.Equal(["BlogId", "Slug"], composite.Properties);
        Assert.True(composite.IsUnique);

        // An index with no explicit name falls back to its columns for display.
        Assert.Equal("AuthorId", indexes.Single(i => i.Properties is ["AuthorId"]).DisplayName);
    }

    // ---- Relationships ----

    [Fact]
    public void ReadsAOneToManyWithNavigationsOnBothEnds()
    {
        var relationship = Rich().Relationships.Single(r =>
            r.DependentEntity == "SampleRichModel.Post"
            && r.PrincipalEntity == "SampleRichModel.Blog");

        Assert.Equal(Cardinality.OneToMany, relationship.Cardinality);
        Assert.Equal(["BlogId"], relationship.ForeignKeyProperties);
        Assert.Equal("Blog", relationship.DependentNavigation);
        Assert.Equal("Posts", relationship.PrincipalNavigation);
        Assert.Equal("Cascade", relationship.DeleteBehavior);
        Assert.True(relationship.IsRequired);
    }

    [Fact]
    public void DistinguishesAnOptionalRelationshipFromARequiredOne()
    {
        var optional = Rich().Relationships.Single(r =>
            r.DependentEntity == "SampleRichModel.Post"
            && r.PrincipalEntity == "SampleRichModel.Author");

        Assert.Equal("SetNull", optional.DeleteBehavior);
        Assert.False(optional.IsRequired);
    }

    [Fact]
    public void ReadsAOneToOneWhoseForeignKeyCallNamesTheDependentTypeFirst()
    {
        // HasForeignKey("SampleRichModel.PostStatistics", "PostId") — the leading argument is a type,
        // not a column. Taking it literally puts a phantom "SampleRichModel.PostStatistics" column
        // on the diagram.
        var relationship = Rich().Relationships.Single(r =>
            r.DependentEntity == "SampleRichModel.PostStatistics");

        Assert.Equal(Cardinality.OneToOne, relationship.Cardinality);
        Assert.Equal(["PostId"], relationship.ForeignKeyProperties);
        Assert.Equal("Statistics", relationship.PrincipalNavigation);
        Assert.Equal("Post", relationship.DependentNavigation);
    }

    [Fact]
    public void ReadsASelfReference()
    {
        var relationship = Rich().Relationships.Single(r =>
            r.DependentEntity == "SampleRichModel.Comment"
            && r.PrincipalEntity == "SampleRichModel.Comment");

        Assert.True(relationship.IsSelfReference);
        Assert.Equal(["ParentId"], relationship.ForeignKeyProperties);
        Assert.Equal("Replies", relationship.PrincipalNavigation);
        Assert.Equal("Restrict", relationship.DeleteBehavior);
    }

    [Fact]
    public void MarksForeignKeyProperties()
    {
        var post = Rich().Entity("SampleRichModel.Post")!;

        Assert.True(post.Properties.Single(p => p.Name == "BlogId").IsForeignKey);
        Assert.True(post.Properties.Single(p => p.Name == "AuthorId").IsForeignKey);
        Assert.False(post.Properties.Single(p => p.Name == "Title").IsForeignKey);
    }

    [Fact]
    public void HandlesANullNavigationNameOnARelationship()
    {
        // The implicit join entity's relationships are written HasOne("...Post", null), and a literal
        // null argument must not be read as the string "null".
        var relationships = Rich().Relationships.Where(r => r.DependentEntity == "PostTag").ToList();

        Assert.Equal(2, relationships.Count);
        Assert.All(relationships, r => Assert.Null(r.DependentNavigation));
        Assert.All(relationships, r => Assert.Null(r.PrincipalNavigation));
    }

    // ---- Many-to-many ----

    [Fact]
    public void DetectsAnEfGeneratedManyToManyJoinEntity()
    {
        var join = Rich().Entity("PostTag")!;

        Assert.True(join.IsImplicitJoin);
        Assert.Equal(["PostsId", "TagsId"], join.Keys);
    }

    [Fact]
    public void DoesNotMistakeAHandWrittenJoinEntityForAGeneratedOne()
    {
        // BlogEditor has a composite key over two foreign keys, exactly like the generated kind. What
        // separates them is the payload column and the CLR namespace.
        var editor = Rich().Entity("SampleRichModel.BlogEditor")!;

        Assert.False(editor.IsImplicitJoin);
        Assert.Contains("Role", editor.Properties.Select(p => p.Name));
    }

    // ---- Owned types ----

    [Fact]
    public void ReadsAnOwnedReference()
    {
        var model = Rich();
        var address = model.Entities.Single(e => e.IsOwned && e.ShortName == "Address");

        Assert.Equal("SampleRichModel.Author", address.OwnerName);
        Assert.Contains("City", address.Properties.Select(p => p.Name));

        var ownership = model.Relationships.Single(r => r.DependentEntity == address.Name);
        Assert.True(ownership.IsOwnership);
        Assert.Equal(Cardinality.OneToOne, ownership.Cardinality);
        Assert.Equal("Address", ownership.PrincipalNavigation);
        Assert.Equal(["AuthorId"], ownership.ForeignKeyProperties);
    }

    [Fact]
    public void ReadsAnOwnedCollection()
    {
        var model = Rich();
        var contact = model.Entities.Single(e => e.IsOwned && e.ShortName == "ContactMethod");

        Assert.Equal("ContactMethod", contact.Table);
        Assert.Equal(["AuthorId", "Id"], contact.Keys);

        var ownership = model.Relationships.Single(r => r.DependentEntity == contact.Name);
        Assert.Equal(Cardinality.OneToMany, ownership.Cardinality);
        Assert.True(ownership.IsOwnership);
    }

    [Fact]
    public void KeepsTwoOwnersOfTheSameOwnedTypeApart()
    {
        // Nothing in samples/SampleRichModel does this, but a Contractor and an Employee both owning
        // an Address is the textbook shape, and keying owned types on the bare type name silently
        // merges the two into one node.
        const string source = """
            [DbContext(typeof(TwoOwnersContext))]
            partial class Snapshot : ModelSnapshot
            {
                protected override void BuildModel(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity("Ns.Contractor", b =>
                        {
                            b.OwnsOne("Ns.Address", "Address", b1 =>
                                {
                                    b1.Property<string>("City").HasColumnType("nvarchar(max)");
                                    b1.WithOwner().HasForeignKey("ContractorId");
                                });
                        });

                    modelBuilder.Entity("Ns.Employee", b =>
                        {
                            b.OwnsOne("Ns.Address", "Address", b1 =>
                                {
                                    b1.Property<string>("Town").HasColumnType("nvarchar(max)");
                                    b1.WithOwner().HasForeignKey("EmployeeId");
                                });
                        });
                }
            }
            """;

        var owned = ModelSnapshotParser.Parse(source).Entities.Where(e => e.IsOwned).ToList();

        Assert.Equal(2, owned.Count);
        Assert.Equal(["Ns.Contractor", "Ns.Employee"], owned.Select(e => e.OwnerName).Order());
        Assert.All(owned, e => Assert.Equal("Address", e.ShortName));
        Assert.Single(owned, e => e.Properties.Any(p => p.Name == "City"));
        Assert.Single(owned, e => e.Properties.Any(p => p.Name == "Town"));
    }

    // ---- Inheritance ----

    [Fact]
    public void ReadsATablePerHierarchyDiscriminator()
    {
        var model = Rich();
        var person = model.Entity("SampleRichModel.Person")!;

        Assert.Equal("PersonType", person.DiscriminatorProperty);
        Assert.Equal("Person", person.DiscriminatorValue);
        Assert.True(person.Properties.Single(p => p.Name == "PersonType").IsDiscriminator);
    }

    [Fact]
    public void ReadsDerivedTypesAndTheirDiscriminatorValues()
    {
        var model = Rich();
        var employee = model.Entity("SampleRichModel.Employee")!;

        Assert.Equal("SampleRichModel.Person", employee.BaseType);
        Assert.Equal("employee", employee.DiscriminatorValue);

        // A derived type in a TPH hierarchy declares no table of its own; it inherits the base's.
        Assert.Null(employee.Table);
        Assert.Equal("People", model.Entity(employee.BaseType!)!.Table);
    }

    // ---- Formatting robustness ----

    [Fact]
    public void ParsesCallsWhoseArgumentsWrapAcrossLines()
    {
        // The case that rules out a line-based parser. EF wraps long argument lists, and every call
        // in this fixture is the multi-line form of one snapshot-rich has on a single line.
        var model = ModelSnapshotParser.Parse(Fixture.Text("snapshot-wrapped-args"));
        var contractor = model.Entity("Wrapped.Models.Contractor")!;

        Assert.Equal("VendorId", contractor.Properties.Single(p => p.Name == "VendorId").Name);
        Assert.Equal(["VendorId"], contractor.Indexes.Single().Properties);
        Assert.Equal(200, model.Entity("Wrapped.Models.Vendor")!
            .Properties.Single(p => p.Name == "Name").MaxLength);

        var relationship = model.Relationships.Single(r =>
            r.DependentEntity == "Wrapped.Models.Contractor");

        Assert.Equal("Wrapped.Models.Vendor", relationship.PrincipalEntity);
        Assert.Equal(["VendorId"], relationship.ForeignKeyProperties);
        Assert.Equal("Vendor", relationship.DependentNavigation);
        Assert.Equal("Contractors", relationship.PrincipalNavigation);
        Assert.Equal("Restrict", relationship.DeleteBehavior);
    }

    [Fact]
    public void ReadsAnOwnedTypeConfiguredWithHasOneRatherThanWithOwner()
    {
        // Older EF versions wrote the ownership foreign key as
        // HasOne(owner).WithOne(nav).HasForeignKey(ownedType, column) instead of WithOwner(), and
        // those snapshots are still committed in real repositories.
        var model = ModelSnapshotParser.Parse(Fixture.Text("snapshot-wrapped-args"));
        var address = model.Entities.Single(e => e.IsOwned);

        Assert.Equal("Wrapped.Models.Contractor", address.OwnerName);
        Assert.Contains("City", address.Properties.Select(p => p.Name));
    }

    [Fact]
    public void SkipsFluentCallsItDoesNotRecognise()
    {
        // A future EF version adding a fluent method should cost one detail on the diagram, not the
        // whole tab.
        var model = ModelSnapshotParser.Parse(Fixture.Text("snapshot-future"));

        Assert.Equal("99.0.0", model.EfVersion);
        Assert.Equal(["Future.Gadget", "Future.Widget"], model.Entities.Select(e => e.Name).Order());

        var widget = model.Entity("Future.Widget")!;
        Assert.Equal(["Id"], widget.Keys);
        Assert.Equal("Widgets", widget.Table);
        Assert.True(widget.Properties.Single(p => p.Name == "Name").IsRequired);

        // HasLatencyBudget sits in the middle of the relationship chain, between HasForeignKey and
        // OnDelete, so an unknown call must not stop the rest of the chain being read.
        var relationship = model.Relationships.Single();
        Assert.Equal(["GadgetId"], relationship.ForeignKeyProperties);
        Assert.Equal("Cascade", relationship.DeleteBehavior);
        Assert.True(relationship.IsRequired);
    }

    [Fact]
    public void ReturnsAnEmptyModelForSourceThatIsNotASnapshot()
    {
        var model = ModelSnapshotParser.Parse("public class NotASnapshot { }");

        Assert.Empty(model.Entities);
        Assert.Empty(model.Relationships);
        Assert.Equal("", model.ContextName);
    }

    [Fact]
    public void DoesNotThrowOnSourceThatDoesNotCompile()
    {
        // Roslyn parses a broken file into a tree with error nodes rather than refusing, which is why
        // the diagram works on a solution that is mid-edit.
        var model = ModelSnapshotParser.Parse("modelBuilder.Entity(\"Ns.A\", b => { b.HasKey(");

        Assert.NotNull(model);
    }

    [Fact]
    public void ParsesAModelWithNoRelationshipsAtAll()
    {
        var model = Simple();

        Assert.Equal(2, model.Entities.Count);
        Assert.Empty(model.Relationships);
        Assert.All(model.Entities, e => Assert.False(e.IsImplicitJoin));
    }

    [Fact]
    public void ObservesCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            ModelSnapshotParser.Parse(Fixture.Text("snapshot-rich"), cancellationToken: cancelled.Token));
    }
}
