using ExtHost.Services;
using Xunit;

namespace ExtHost.Tests;

public class UrlHelperTests
{
    [Theory]
    [InlineData("google.com", "https://google.com")]
    [InlineData("localhost:3000", "http://localhost:3000")]
    [InlineData("https://example.com/a", "https://example.com/a")]
    [InlineData("hello world", null)]
    public void FixupUrl(string input, string? expected) => Assert.Equal(expected, UrlHelper.FixupUrl(input));

    [Fact]
    public void NonUrl_BecomesSearch() =>
        Assert.Equal("https://s/?q=hello%20world", UrlHelper.ToNavigableUrl("hello world", "https://s/?q={0}"));
}
