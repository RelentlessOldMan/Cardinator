using System.IO;
using Cardinator.Services;

namespace Cardinator.Tests;

public class ImageIntakeTests
{
    [Theory]
    [InlineData("https://example.com/art.png", true)]
    [InlineData("http://example.com/a", true)]
    [InlineData("  https://example.com/a  ", true)]
    [InlineData("ftp://example.com/a", false)]
    [InlineData("C:/art/foo.png", false)]
    [InlineData("just some text", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsHttpUrl_RecognizesWebUrls(string? text, bool expected)
        => Assert.Equal(expected, ImageIntake.IsHttpUrl(text));

    [Theory]
    [InlineData("foo.png", true)]
    [InlineData("foo.JPG", true)]
    [InlineData("foo.jpeg", true)]
    [InlineData("foo.webp", true)]
    [InlineData("https://x.com/pic.gif?w=200", true)]
    [InlineData("foo.txt", false)]
    [InlineData("foo", false)]
    [InlineData(null, false)]
    public void LooksLikeImagePath_ChecksExtension(string? path, bool expected)
        => Assert.Equal(expected, ImageIntake.LooksLikeImagePath(path));

    [Theory]
    [InlineData("image/png", "http://x/y", ".png")]
    [InlineData("image/jpeg", "http://x/y", ".jpg")]
    [InlineData("image/gif", "http://x/y", ".gif")]
    [InlineData("image/webp", "http://x/y", ".webp")]
    [InlineData("", "http://x/pic.jpg", ".jpg")]                 // fall back to URL extension
    [InlineData("application/octet-stream", "http://x/pic.png?q=1", ".png")]
    [InlineData("", "http://x/noext", ".png")]                   // final fallback
    public void ExtensionFor_PrefersContentTypeThenUrl(string ct, string url, string expected)
        => Assert.Equal(expected, ImageIntake.ExtensionFor(ct, url));

    [Fact]
    public void UniqueFileName_IsSafeUniqueAndKeepsExtension()
    {
        var a = ImageIntake.UniqueFileName("my art", ".png");
        var b = ImageIntake.UniqueFileName("my art", ".png");

        Assert.EndsWith(".png", a);
        Assert.NotEqual(a, b);                                    // short id makes it collision-resistant
        Assert.DoesNotContain(Path.GetInvalidFileNameChars(), a.Contains);
    }

    [Fact]
    public void UniqueFileName_EmptyBaseStillProducesName()
    {
        var name = ImageIntake.UniqueFileName("", "png");         // extension without leading dot
        Assert.EndsWith(".png", name);
        Assert.True(name.Length > ".png".Length);
    }
}
