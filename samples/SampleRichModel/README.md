# SampleRichModel

The source of the model-snapshot fixtures for the Diagrams tab, and a model worth looking at in the
GUI. Deliberately **not** part of `EfAssist.slnx`, for the same reasons as `SampleEfApp`.

`SampleEfApp` stays deliberately tiny — two entities, no relationships — and several tests assert on
its exact migration list. This project exists so the diagram parser has something with relationships
in it without disturbing any of that.

## Why each construct is here

Every one of these produces a distinct fluent call in `Migrations/RichContextModelSnapshot.cs`, which
is the file `ModelSnapshotParser` reads:

| Construct | Snapshot call it produces |
| --- | --- |
| `Blog` 1 → \* `Post`, navigations on both ends | `HasOne(...).WithMany("Posts").HasForeignKey("BlogId").OnDelete(Cascade)` |
| Optional `Post` → `Author` | the same, but `OnDelete(SetNull)` and no `.IsRequired()` |
| `Post` 1 → 0..1 `PostStatistics` | `HasOne(...).WithOne("Statistics").HasForeignKey("SampleRichModel.PostStatistics", "PostId")` — note the FK call names the dependent **type** first, unlike every other `HasForeignKey` |
| `Post` \* ↔ \* `Tag` via skip navigations | an implicit join entity `modelBuilder.Entity("PostTag", ...)` with no namespace, a composite key, and `HasOne("...", null)` — a literal `null` navigation name |
| `BlogEditor` with a composite key and a `Role` payload | an explicit join entity that must *not* be mistaken for the implicit kind |
| `Author` owns `Address` | `b.OwnsOne(..., b1 => { ... b1.WithOwner().HasForeignKey("AuthorId"); })` — a nested builder |
| `Author` owns many `ContactMethod` | `b.OwnsMany(...)`, own table, key `("AuthorId", "Id")` |
| `Person` → `Employee` / `Customer` | `HasDiscriminator<string>("PersonType").HasValue(...)`, `UseTphMappingStrategy()`, and `HasBaseType(...)` on the derived types, which have no `ToTable` of their own |
| `Comment` → `Comment` | a self-referencing `HasOne("...Comment", "Parent").WithMany("Replies")` |
| `Blog.Owner` with no declared FK property | a shadow `OwnerId` property, indistinguishable in the snapshot from a real one |
| `Blog.Slug` | `HasAlternateKey("Slug")` |
| `Post.Title` | `HasIndex("Title").HasDatabaseName("IX_Post_Title")` |
| `Post` (`BlogId`, `Slug`) | `HasIndex("BlogId", "Slug").IsUnique()` |
| `Post.PublishedUtc` | `ValueGeneratedOnAdd()` plus `HasDefaultValueSql(...)` |

The single most important thing this project revealed: **EF emits several separate
`modelBuilder.Entity("X", ...)` blocks for the same entity** — properties and keys in one, foreign
keys in a later one, navigations in a later one still. A parser that treats each block as a new
entity produces duplicates. `ModelSnapshotParser` merges them by name.

## No schema

SQLite has no schemas, so nothing here produces the two-argument `ToTable("X", "schema")` form.
`Fixtures/snapshot-wrapped-args.txt` covers that, along with EF's habit of wrapping long argument
lists across lines.

## Regenerating the fixture

```
cd samples/SampleRichModel
dotnet ef migrations add <Name>
cp Migrations/RichContextModelSnapshot.cs ../../tests/EfAssist.Core.Tests/Fixtures/snapshot-rich.txt
```

Then expect `ModelSnapshotParserTests` to need updating — that is the point of it being a captured
fixture rather than a hand-written one.

## No database

Nothing here needs `rich.db` to exist. `dotnet ef migrations add` never touches a database, and
these fixtures are generated from source. Do not run `database update`; there is nothing to gain and
it puts a file in the way.
