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

    [Theory]
    [InlineData("https://example.com/graph#code=secret&state=x", "https://example.com/graph（已省略參數）")]
    [InlineData("https://login.example.com/authorize?client_id=a&code_challenge=b", "https://login.example.com/authorize（已省略參數）")]
    [InlineData("https://example.com/a/b", "https://example.com/a/b")]
    [InlineData("about:blank", "about:blank")]
    [InlineData(null, "")]
    public void ForLog_StripsQueryAndFragment(string? input, string expected) => Assert.Equal(expected, UrlHelper.ForLog(input));
}
