namespace EfAssist.Core.Tests;

public class DataLossTests
{
    private const string Migration = """
        using Microsoft.EntityFrameworkCore.Migrations;

        public partial class Reshape : Migration
        {
            protected override void Up(MigrationBuilder migrationBuilder)
            {
                migrationBuilder.DropTable(name: "Tags");

                migrationBuilder.DropColumn(name: "Url", table: "Blogs", schema: "blog");

                migrationBuilder.RenameColumn(name: "Title", table: "Posts", newName: "Heading");

                migrationBuilder.AlterColumn<string>(
                    name: "Name",
                    table: "Blogs",
                    type: "nvarchar(100)",
                    maxLength: 100,
                    nullable: false,
                    oldClrType: typeof(string),
                    oldType: "nvarchar(max)",
                    oldNullable: true);

                migrationBuilder.AlterColumn<int>(
                    name: "Rating",
                    table: "Posts",
                    type: "int",
                    nullable: true,
                    oldClrType: typeof(int),
                    oldType: "int");

                migrationBuilder.AlterColumn<string>(
                    name: "Body",
                    table: "Posts",
                    maxLength: 50,
                    oldClrType: typeof(string),
                    oldMaxLength: 200);

                migrationBuilder.AddColumn<string>(name: "Slug", table: "Posts", nullable: true);
            }

            protected override void Down(MigrationBuilder migrationBuilder)
            {
                migrationBuilder.DropColumn(name: "Slug", table: "Posts");
            }
        }
        """;

    [Fact]
    public void Up_names_each_operation_that_may_lose_data_in_source_order()
    {
        Assert.Equal(
            [
                "drops table Tags",
                "drops column blog.Blogs.Url",
                "renames column Posts.Title to Heading",
                "alters column Blogs.Name (nvarchar(max) to nvarchar(100), maxLength set to 100, made required)",
                "alters column Posts.Body (maxLength 200 to 50)",
            ],
            DataLoss.Scan(Migration, "Up"));
    }

    [Fact]
    public void Down_is_scanned_separately_because_a_rollback_runs_it()
    {
        Assert.Equal(["drops column Posts.Slug"], DataLoss.Scan(Migration, "Down"));
    }

    [Fact]
    public void A_clr_type_change_without_store_types_still_counts()
    {
        var source = """
            class M { void Up(MigrationBuilder b) {
                b.AlterColumn<int>(name: "Age", table: "People", nullable: false, oldClrType: typeof(string));
            } }
            """;

        Assert.Equal(["alters column People.Age (string to int)"], DataLoss.Scan(source, "Up"));
    }

    [Fact]
    public void A_file_without_the_method_finds_nothing()
    {
        Assert.Empty(DataLoss.Scan("class Nothing {}", "Up"));
    }
}
