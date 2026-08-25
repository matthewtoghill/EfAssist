using Microsoft.EntityFrameworkCore;

namespace SampleRichModel;

/// <summary>
/// A deliberately feature-heavy model. Every construct here exists so that the generated model
/// snapshot contains a corresponding fluent call for the diagram parser to handle:
/// one-to-many, one-to-one, many-to-many via skip navigations, an explicit join entity with a
/// composite key and a payload, an owned reference, an owned collection, table-per-hierarchy with
/// an explicit discriminator, a self-referencing relationship, an alternate key, a named index,
/// a shadow foreign key, and every <c>DeleteBehavior</c> worth distinguishing.
/// </summary>
public class RichContext : DbContext
{
    public DbSet<Blog> Blogs => Set<Blog>();
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<Author> Authors => Set<Author>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<BlogEditor> BlogEditors => Set<BlogEditor>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options.UseSqlite("Data Source=rich.db");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Blog>(blog =>
        {
            blog.Property(b => b.Name).IsRequired().HasMaxLength(200);
            blog.Property(b => b.Slug).IsRequired().HasMaxLength(60);

            // Alternate key, so the snapshot carries a HasAlternateKey call.
            blog.HasAlternateKey(b => b.Slug);

            // No FK property declared for Owner, so EF invents a shadow "OwnerId". The diagram
            // needs to cope with a property that has no CLR member behind it.
            blog.HasOne(b => b.Owner)
                .WithMany()
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Post>(post =>
        {
            post.Property(p => p.Title).IsRequired().HasMaxLength(300);
            post.Property(p => p.PublishedUtc).HasDefaultValueSql("CURRENT_TIMESTAMP");

            // Named index, so the snapshot carries HasDatabaseName rather than only HasIndex.
            post.HasIndex(p => p.Title).HasDatabaseName("IX_Post_Title");

            // Unique index over two columns.
            post.HasIndex(p => new { p.BlogId, p.Slug }).IsUnique();

            post.HasOne(p => p.Blog)
                .WithMany(b => b.Posts)
                .HasForeignKey(p => p.BlogId)
                .OnDelete(DeleteBehavior.Cascade);

            // Optional relationship, so the delete behaviour differs from the one above.
            post.HasOne(p => p.Author)
                .WithMany(a => a.Posts)
                .HasForeignKey(p => p.AuthorId)
                .OnDelete(DeleteBehavior.SetNull);

            // One-to-one. The dependent's primary key is also its foreign key — "PostId" is not
            // "Id", so convention will not find it and it has to be stated.
            post.HasOne(p => p.Statistics)
                .WithOne(s => s.Post)
                .HasForeignKey<PostStatistics>(s => s.PostId)
                .OnDelete(DeleteBehavior.Cascade);

            // Skip navigations on both ends, so EF generates an implicit join entity with no CLR
            // type of its own. The diagram either draws it or collapses it to one many-to-many edge.
            post.HasMany(p => p.Tags)
                .WithMany(t => t.Posts);
        });

        modelBuilder.Entity<PostStatistics>().HasKey(s => s.PostId);

        // Owned types: one reference and one collection, both nested builders in the snapshot.
        modelBuilder.Entity<Author>(author =>
        {
            author.Property(a => a.DisplayName).IsRequired().HasMaxLength(120);
            author.OwnsOne(a => a.Address);
            author.OwnsMany(a => a.ContactMethods);
        });

        // Self-reference. The principal and the dependent are the same entity.
        modelBuilder.Entity<Comment>(comment =>
        {
            comment.HasOne(c => c.Post)
                .WithMany(p => p.Comments)
                .HasForeignKey(c => c.PostId)
                .OnDelete(DeleteBehavior.Cascade);

            comment.HasOne(c => c.Parent)
                .WithMany(c => c.Replies)
                .HasForeignKey(c => c.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Table-per-hierarchy with an explicit discriminator, so the snapshot carries HasBaseType,
        // HasDiscriminator and HasValue rather than leaving them implicit.
        modelBuilder.Entity<Person>()
            .HasDiscriminator<string>("PersonType")
            .HasValue<Employee>("employee")
            .HasValue<Customer>("customer");

        // An explicit join entity: a composite key over two foreign keys, plus a payload column.
        // Deliberately here alongside the implicit Post/Tag join, so the parser's implicit-join
        // detection has a near-miss to reject rather than only a positive case to find.
        modelBuilder.Entity<BlogEditor>(editor =>
        {
            editor.HasKey(e => new { e.BlogId, e.PersonId });

            editor.HasOne(e => e.Blog)
                .WithMany(b => b.Editors)
                .HasForeignKey(e => e.BlogId)
                .OnDelete(DeleteBehavior.Cascade);

            editor.HasOne(e => e.Person)
                .WithMany()
                .HasForeignKey(e => e.PersonId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public class Blog
{
    public int Id { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Url { get; set; }
    public DateTime CreatedUtc { get; set; }

    public Person? Owner { get; set; }
    public List<Post> Posts { get; } = [];
    public List<BlogEditor> Editors { get; } = [];
}

public class Post
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Body { get; set; }
    public DateTime PublishedUtc { get; set; }

    public int BlogId { get; set; }
    public Blog Blog { get; set; } = null!;

    public int? AuthorId { get; set; }
    public Author? Author { get; set; }

    public PostStatistics? Statistics { get; set; }
    public List<Tag> Tags { get; } = [];
    public List<Comment> Comments { get; } = [];
}

/// <summary>The dependent end of the one-to-one. Its key is its foreign key.</summary>
public class PostStatistics
{
    public int PostId { get; set; }
    public Post Post { get; set; } = null!;
    public long Views { get; set; }
    public long Likes { get; set; }
}

public class Tag
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<Post> Posts { get; } = [];
}

public class Author
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = "";

    /// <summary>Owned reference — its columns live in the Authors table.</summary>
    public Address? Address { get; set; }

    /// <summary>Owned collection — its own table, keyed by the owner.</summary>
    public List<ContactMethod> ContactMethods { get; } = [];

    public List<Post> Posts { get; } = [];
}

public class Address
{
    public string Line1 { get; set; } = "";
    public string? Line2 { get; set; }
    public string City { get; set; } = "";
    public string PostCode { get; set; } = "";
}

public class ContactMethod
{
    public string Kind { get; set; } = "";
    public string Value { get; set; } = "";
}

public class Comment
{
    public int Id { get; set; }
    public string Body { get; set; } = "";

    public int PostId { get; set; }
    public Post Post { get; set; } = null!;

    public int? ParentId { get; set; }
    public Comment? Parent { get; set; }
    public List<Comment> Replies { get; } = [];
}

public abstract class Person
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class Employee : Person
{
    public string EmployeeNumber { get; set; } = "";
    public DateTime HireDate { get; set; }
}

public class Customer : Person
{
    public string CustomerRef { get; set; } = "";
    public decimal CreditLimit { get; set; }
}

/// <summary>An explicit join entity with a payload, and a composite key over its two foreign keys.</summary>
public class BlogEditor
{
    public int BlogId { get; set; }
    public Blog Blog { get; set; } = null!;

    public int PersonId { get; set; }
    public Person Person { get; set; } = null!;

    public string Role { get; set; } = "";
}
