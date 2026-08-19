using Microsoft.EntityFrameworkCore;

namespace SampleEfApp;

public class BlogContext : DbContext
{
    public DbSet<Blog> Blogs => Set<Blog>();
    public DbSet<Post> Posts => Set<Post>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options.UseSqlite("Data Source=blog.db");
}

// Second context with no migrations of its own, so `dbcontext list` returns more than one entry.
public class AuditContext : DbContext
{
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options.UseSqlite("Data Source=audit.db");
}

public class Blog
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Url { get; set; }
}

public class Post
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public int BlogId { get; set; }
}

public class AuditEntry
{
    public int Id { get; set; }
    public string Message { get; set; } = "";
}
