namespace SreAgent.Host.Tests;

/// <summary>Sandbox service rules that can be tested without a database.</summary>
public class OrderRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_or_blank_items_are_a_client_error(string? item) => Assert.NotNull(OrderRules.Validate(item));

    [Fact]
    public void An_item_over_the_limit_is_a_client_error() =>
        Assert.NotNull(OrderRules.Validate(new string('x', OrderRules.MaxItemLength + 1)));

    [Theory]
    [InlineData("coffee")]
    [InlineData("кофе с молоком")]
    public void Normal_items_pass(string item) => Assert.Null(OrderRules.Validate(item));

    [Fact]
    public void The_size_limit_is_inclusive() => Assert.Null(OrderRules.Validate(new string('x', OrderRules.MaxItemLength)));
}
