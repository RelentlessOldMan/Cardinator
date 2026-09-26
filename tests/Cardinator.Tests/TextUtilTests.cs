using Cardinator.Services;

namespace Cardinator.Tests;

public class TextUtilTests
{
    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul", "_nul")]
    [InlineData("COM1", "_COM1")]
    [InlineData("CON.png", "_CON.png")]        // reserved even with an extension
    [InlineData("Lightning Bolt", "Lightning Bolt")]
    [InlineData("Foo.", "Foo")]                // trailing dot silently dropped by Windows -> remove it
    [InlineData("Foo ", "Foo")]               // trailing space likewise
    public void SafeFileName_HandlesReservedAndTrailing(string input, string expected)
        => Assert.Equal(expected, TextUtil.SafeFileName(input));

    [Fact]
    public void SafeFileName_ReplacesInvalidChars()
    {
        var result = TextUtil.SafeFileName("a/b:c*d?");
        Assert.DoesNotContain('/', result);
        Assert.DoesNotContain(':', result);
        Assert.DoesNotContain('*', result);
        Assert.DoesNotContain('?', result);
    }

    [Fact]
    public void SafeFileName_EmptyFallsBack()
        => Assert.Equal("card", TextUtil.SafeFileName("   "));

    [Fact]
    public void Slug_CollapsesSeparators()
        => Assert.Equal("gold-multicolor", TextUtil.Slug("  Gold   Multicolor!!  "));

    [Fact]
    public void Slug_EmptyFallsBack()
        => Assert.Equal("card", TextUtil.Slug("＠＠＠"));   // no ASCII letters/digits
}
