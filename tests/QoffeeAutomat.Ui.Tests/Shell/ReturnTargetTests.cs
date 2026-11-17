using QoffeeAutomat.Ui.Components.Pages;

namespace QoffeeAutomat.Ui.Tests.Shell;

[Trait("Category", "Ui")]
public sealed class ReturnTargetTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("/commands", "/commands")]
    [InlineData("/commands/abc?x=1#top", "/commands/abc?x=1#top")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("commands", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("/ok\r\nLocation: https://evil.example", "/")]
    public void Only_a_path_on_this_site_is_followed(string? returnUrl, string expected)
    {
        Assert.Equal(expected, ReturnTarget.Resolve(returnUrl));
    }
}
