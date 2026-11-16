using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Shell;

/// <summary>
/// The favicon and the app icon come from <c>assets/brand/</c>, the single source of every icon (assets/brand/README.md),
/// and are bundled with the UI. The files in <c>wwwroot</c> are copies; these tests fail when the brand files change and
/// the copies were not refreshed.
/// </summary>
[Trait("Category", "Ui")]
public sealed class BrandAssetTests
{
    [Theory]
    [InlineData("icon.svg", "icon.svg")]
    [InlineData("icon.png", "icon.png")]
    [InlineData("icon@2x.png", "icon@2x.png")]
    [InlineData("favicon.ico", "app.ico")]
    public void The_bundled_icons_are_copies_of_the_brand_assets(string bundled, string brand)
    {
        byte[] expected = File.ReadAllBytes(RepositoryPaths.Combine("assets", "brand", brand));
        byte[] actual = File.ReadAllBytes(RepositoryPaths.Combine("src", "QoffeeAutomat.Ui", "wwwroot", bundled));

        Assert.True(expected.AsSpan().SequenceEqual(actual), bundled + " differs from assets/brand/" + brand + "; copy the brand file again.");
    }

    [Fact]
    public void The_index_page_uses_the_favicon_and_the_app_icon_and_the_manifest_lists_them()
    {
        string html = File.ReadAllText(RepositoryPaths.Combine("src", "QoffeeAutomat.Ui", "wwwroot", "index.html"));
        string manifest = File.ReadAllText(RepositoryPaths.Combine("src", "QoffeeAutomat.Ui", "wwwroot", "manifest.webmanifest"));

        Assert.Contains("href=\"favicon.ico\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"icon.svg\"", html, StringComparison.Ordinal);
        Assert.Contains("rel=\"apple-touch-icon\" href=\"icon@2x.png\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"manifest.webmanifest\"", html, StringComparison.Ordinal);
        Assert.Contains("\"src\": \"icon.png\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"src\": \"icon@2x.png\"", manifest, StringComparison.Ordinal);
    }
}
