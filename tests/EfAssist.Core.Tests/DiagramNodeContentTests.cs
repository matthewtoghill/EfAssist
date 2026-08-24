using EfAssist.Core.Diagrams;

namespace EfAssist.Core.Tests;

/// <summary>
/// The one place the entity-relationship and class views differ, so both are exercised against the
/// same parsed model.
/// </summary>
public class DiagramNodeContentTests
{
    private static readonly DiagramModel Model =
        ModelSnapshotParser.Parse(Fixture.Text("snapshot-rich"));

    private static DiagramNodeContent.Content Build(DiagramViewOptions? options = null) =>
        DiagramNodeContent.Build(Model, options ?? new DiagramViewOptions());

    private static DiagramNode Node(DiagramNodeContent.Content content, string shortName) =>
        content.Nodes.Single(n => n.EntityName.EndsWith("." + shortName, StringComparison.Ordinal));

    // ---- Entity-relationship view ----

    [Fact]
    public void ErViewTitlesNodesWithTheirTable()
    {
        var post = Node(Build(), "Post");

        Assert.Equal("Posts", post.Title);
        Assert.Equal("Post", post.Subtitle);
    }

    [Fact]
    public void ErViewTitlesADerivedTypeWithTheTableItInherits()
    {
        // A type-per-hierarchy derived type declares no table of its own. Using the raw value would
        // leave the node untitled.
        var employee = Node(Build(), "Employee");

        Assert.Equal("People", employee.Title);
    }

    [Fact]
    public void ErViewShowsColumnTypesAndKeyBadges()
    {
        var post = Node(Build(), "Post");

        var id = post.Rows.Single(r => r.Name == "Id");
        Assert.Equal("PK", id.Badge);
        Assert.Equal("INTEGER", id.Type);

        Assert.Equal("FK", post.Rows.Single(r => r.Name == "BlogId").Badge);
    }

    [Fact]
    public void ErViewShowsBothRolesOnAKeyThatIsAlsoAForeignKey()
    {
        // The normal shape of a join table, and the reason the badge is two words rather than one.
        var content = Build(new DiagramViewOptions { CollapseJoinEntities = false });
        var join = content.Nodes.Single(n => n.EntityName == "PostTag");

        Assert.All(join.Rows, r => Assert.Equal("PK FK", r.Badge));
    }

    [Fact]
    public void ErViewOmitsNavigations()
    {
        // A navigation is not a column, so ShowNavigations has no meaning here whatever it says.
        var content = Build(new DiagramViewOptions { ShowNavigations = true });

        Assert.DoesNotContain(
            Node(content, "Post").Rows, r => r.Kind == RowKind.Navigation);
    }

    // ---- Class view ----

    [Fact]
    public void ClassViewTitlesNodesWithTheClrTypeAndSubtitlesWithTheTable()
    {
        var post = Node(Build(new DiagramViewOptions { Kind = DiagramKind.Class }), "Post");

        Assert.Equal("Post", post.Title);
        Assert.Equal("Posts", post.Subtitle);
    }

    [Fact]
    public void ClassViewShowsClrTypesRatherThanColumnTypes()
    {
        var post = Node(Build(new DiagramViewOptions { Kind = DiagramKind.Class }), "Post");

        Assert.Equal("int", post.Rows.Single(r => r.Name == "Id").Type);
        Assert.Equal("string", post.Rows.Single(r => r.Name == "Title").Type);
    }

    [Fact]
    public void ClassViewTypesNavigationsFromTheRelationshipsTheyBelongTo()
    {
        // The snapshot records a navigation's name but never its type, so a collection-or-not has to
        // be worked out from which end of which relationship the name appears on.
        var content = Build(new DiagramViewOptions { Kind = DiagramKind.Class });

        var blog = Node(content, "Blog");
        Assert.Equal(
            "ICollection<Post>",
            blog.Rows.Single(r => r.Name == "Posts" && r.Kind == RowKind.Navigation).Type);

        var post = Node(content, "Post");
        Assert.Equal(
            "Blog",
            post.Rows.Single(r => r.Name == "Blog" && r.Kind == RowKind.Navigation).Type);
        Assert.Equal(
            "PostStatistics",
            post.Rows.Single(r => r.Name == "Statistics" && r.Kind == RowKind.Navigation).Type);
    }

    [Fact]
    public void ClassViewCanHideNavigations()
    {
        var content = Build(new DiagramViewOptions
        {
            Kind = DiagramKind.Class,
            ShowNavigations = false,
        });

        Assert.DoesNotContain(Node(content, "Post").Rows, r => r.Kind == RowKind.Navigation);
    }

    [Fact]
    public void BothViewsCoverTheSameEntities()
    {
        var er = Build();
        var @class = Build(new DiagramViewOptions { Kind = DiagramKind.Class });

        Assert.Equal(
            er.Nodes.Select(n => n.EntityName).Order(),
            @class.Nodes.Select(n => n.EntityName).Order());
    }

    // ---- Property detail ----

    [Fact]
    public void KeysOnlyShowsKeysAndAlternateKeys()
    {
        var blog = Node(Build(new DiagramViewOptions { Properties = PropertyDetail.KeysOnly }), "Blog");

        Assert.Equal(["Id", "Slug"], blog.Rows.Select(r => r.Name));
    }

    [Fact]
    public void KeysAndForeignKeysAddsTheForeignKeyColumns()
    {
        var post = Node(
            Build(new DiagramViewOptions { Properties = PropertyDetail.KeysAndForeignKeys }), "Post");

        Assert.Equal(["Id", "AuthorId", "BlogId"], post.Rows.Select(r => r.Name));
    }

    [Fact]
    public void TypesCanBeHidden()
    {
        var post = Node(Build(new DiagramViewOptions { ShowTypes = false }), "Post");

        Assert.All(post.Rows, r => Assert.Null(r.Type));
    }

    [Fact]
    public void IndexesAreOffByDefaultAndCanBeShown()
    {
        Assert.DoesNotContain(Node(Build(), "Post").Rows, r => r.Kind == RowKind.Index);

        var withIndexes = Node(Build(new DiagramViewOptions { ShowIndexes = true }), "Post");
        var index = withIndexes.Rows.Single(r => r.Name == "IX_Post_Title");

        Assert.Equal("IX", index.Badge);
        Assert.Equal(RowKind.Index, index.Kind);
    }

    [Fact]
    public void NullableColumnsAreMarked()
    {
        var blog = Node(Build(), "Blog");

        Assert.True(blog.Rows.Single(r => r.Name == "Url").IsNullable);
        Assert.False(blog.Rows.Single(r => r.Name == "Name").IsNullable);
    }

    // ---- Many-to-many ----

    [Fact]
    public void CollapsesAJoinEntityIntoOneEdgeByDefault()
    {
        var content = Build();

        Assert.DoesNotContain(content.Nodes, n => n.EntityName == "PostTag");

        var edge = content.Edges.Single(e => e.Kind == EdgeKind.ManyToMany);
        Assert.Equal(["SampleRichModel.Post", "SampleRichModel.Tag"], new[] { edge.From, edge.To }.Order());
        Assert.Equal("*", edge.FromLabel);
        Assert.Equal("*", edge.ToLabel);
    }

    [Fact]
    public void ShowsTheJoinEntityWhenCollapsingIsOff()
    {
        var content = Build(new DiagramViewOptions { CollapseJoinEntities = false });

        var join = content.Nodes.Single(n => n.EntityName == "PostTag");
        Assert.True(join.IsJoin);
        Assert.Equal("join table", join.Subtitle);

        // Its two foreign keys are now drawn as themselves, so a collapsed edge as well would double
        // the relationship up.
        Assert.DoesNotContain(content.Edges, e => e.Kind == EdgeKind.ManyToMany);
        Assert.Equal(2, content.Edges.Count(e => e.From == "PostTag"));
    }

    [Fact]
    public void KeepsAHandWrittenJoinEntityWhateverTheOption()
    {
        var content = Build();

        Assert.Contains(content.Nodes, n => n.EntityName == "SampleRichModel.BlogEditor");
    }

    // ---- Owned types ----

    [Fact]
    public void InlinesAnOwnedReferenceIntoItsOwner()
    {
        var content = Build();

        Assert.DoesNotContain(content.Nodes, n => n.IsOwned && n.Title.Contains("Address"));

        var author = Node(content, "Author");
        Assert.Contains(author.Rows, r => r.Name == "Address.City");
        Assert.Contains(author.Rows, r => r.Name == "Address.Line1");

        // The owner's own key comes back as the owned type's foreign key; showing it twice on one
        // node is noise.
        Assert.DoesNotContain(author.Rows, r => r.Name == "Address.AuthorId");
    }

    [Fact]
    public void NeverInlinesAnOwnedCollection()
    {
        // An owned collection has its own table, so folding it into the owner would claim columns
        // live somewhere they do not.
        var content = Build();

        Assert.Contains(content.Nodes, n => n.IsOwned && n.EntityName.EndsWith("ContactMethod"));
    }

    [Fact]
    public void CanShowAnOwnedReferenceAsItsOwnNode()
    {
        var content = Build(new DiagramViewOptions { InlineOwnedTypes = false });

        var address = content.Nodes.Single(n => n.IsOwned && n.EntityName.EndsWith("#SampleRichModel.Address"));
        Assert.Contains(address.Rows, r => r.Name == "City");

        var ownership = content.Edges.Single(e =>
            e.Kind == EdgeKind.Ownership && e.From == address.EntityName);
        Assert.Equal("SampleRichModel.Author", ownership.To);
    }

    [Fact]
    public void ClassViewLabelsAnOwnedNodeWithItsOwner()
    {
        var content = Build(new DiagramViewOptions
        {
            Kind = DiagramKind.Class,
            InlineOwnedTypes = false,
        });

        var address = content.Nodes.Single(n => n.EntityName.EndsWith("#SampleRichModel.Address"));
        Assert.Equal("owned by Author", address.Subtitle);
    }

    // ---- Edges ----

    [Fact]
    public void DrawsForeignKeyEdgesFromDependentToPrincipal()
    {
        var edge = Build().Edges.Single(e =>
            e.From == "SampleRichModel.Post" && e.To == "SampleRichModel.Blog");

        Assert.Equal(EdgeKind.ForeignKey, edge.Kind);
        Assert.Equal("*", edge.FromLabel);
        Assert.Equal("1", edge.ToLabel);
    }

    [Fact]
    public void MarksAnOptionalRelationshipAsOptionalOnThePrincipalEnd()
    {
        var edge = Build().Edges.Single(e =>
            e.From == "SampleRichModel.Post" && e.To == "SampleRichModel.Author");

        Assert.Equal("0..1", edge.ToLabel);
    }

    [Fact]
    public void DeleteBehaviourIsOffByDefaultAndCanBeLabelled()
    {
        Assert.All(
            Build().Edges.Where(e => e.Kind == EdgeKind.ForeignKey),
            e => Assert.Null(e.Label));

        var labelled = Build(new DiagramViewOptions { ShowDeleteBehavior = true }).Edges
            .Single(e => e.From == "SampleRichModel.Post" && e.To == "SampleRichModel.Blog");

        Assert.Equal("Cascade", labelled.Label);
    }

    [Fact]
    public void DrawsInheritanceEdgesFromDerivedToBase()
    {
        var edges = Build().Edges.Where(e => e.Kind == EdgeKind.Inheritance).ToList();

        Assert.Equal(2, edges.Count);
        Assert.All(edges, e => Assert.Equal("SampleRichModel.Person", e.To));
    }

    [Fact]
    public void InheritanceEdgesCanBeHidden() =>
        Assert.DoesNotContain(
            Build(new DiagramViewOptions { ShowInheritance = false }).Edges,
            e => e.Kind == EdgeKind.Inheritance);

    [Fact]
    public void KeepsASelfReferenceAsAnEdge()
    {
        var edge = Build().Edges.Single(e =>
            e.From == "SampleRichModel.Comment" && e.To == "SampleRichModel.Comment");

        Assert.Equal(EdgeKind.ForeignKey, edge.Kind);
    }

    [Fact]
    public void NeverEmitsAnEdgeToAHiddenNode()
    {
        // A dangling edge would draw a route to nowhere and crash the router looking for the node.
        var content = Build();
        var visible = content.Nodes.Select(n => n.EntityName).ToHashSet();

        Assert.All(content.Edges, e =>
        {
            Assert.Contains(e.From, visible);
            Assert.Contains(e.To, visible);
        });
    }

    [Fact]
    public void HandlesAModelWithNoEntities()
    {
        var content = DiagramNodeContent.Build(DiagramModel.Empty, new DiagramViewOptions());

        Assert.Empty(content.Nodes);
        Assert.Empty(content.Edges);
    }
}
