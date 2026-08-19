using EfMigrateHub.App.ViewModels;

namespace EfMigrateHub.Core.Tests;

public class ConfirmRequestTests
{
    private static ConfirmRequest Gated(string required) =>
        new("Drop database", "Really?", "Drop", RequiredTypedValue: required);

    [Fact]
    public void An_ungated_request_is_satisfied_by_anything()
    {
        var request = new ConfirmRequest("Remove", "Remove the last migration?", "Remove");

        Assert.False(request.RequiresTyping);
        Assert.True(request.IsSatisfiedBy(null));
    }

    [Fact]
    public void A_gated_request_needs_the_exact_value()
    {
        var request = Gated("OrdersDb");

        Assert.True(request.RequiresTyping);
        Assert.True(request.IsSatisfiedBy("OrdersDb"));
        Assert.True(request.IsSatisfiedBy("  OrdersDb  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ordersdb")]
    [InlineData("OrdersDB")]
    [InlineData("OrdersDb2")]
    [InlineData("Orders")]
    public void Anything_else_leaves_the_gate_shut(string? typed)
    {
        // Case-insensitivity would make a near-miss enough to destroy a database.
        Assert.False(Gated("OrdersDb").IsSatisfiedBy(typed));
    }
}
