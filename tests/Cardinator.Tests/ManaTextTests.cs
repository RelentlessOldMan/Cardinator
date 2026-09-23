using Cardinator.Services;

namespace Cardinator.Tests;

public class ManaTextTests
{
    [Theory]
    [InlineData("{{{{")]
    [InlineData("}}}}")]
    [InlineData("{}")]
    [InlineData("{R")]
    [InlineData("no braces at all")]
    [InlineData("")]
    [InlineData(null)]
    public void Tokenize_MalformedInput_DoesNotThrow(string? text)
    {
        var tokens = ManaText.Tokenize(text);
        Assert.NotNull(tokens);   // never throws, always returns a (possibly empty) list
    }

    [Theory]
    [InlineData("2RW", "{2}{R}{W}")]   // loose cost expands to brace tokens
    [InlineData("2 r w", "{2}{R}{W}")] // spaces + lowercase
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("{R}{W}", "{R}{W}")]   // already tokenized → unchanged
    public void NormalizeCost_HandlesEdgeCases(string input, string expected)
        => Assert.Equal(expected, ManaText.NormalizeCost(input));

    [Fact]
    public void NormalizeCost_MultiDigitStaysTogether()
        => Assert.Equal("{10}{G}", ManaText.NormalizeCost("10G"));

    [Fact]
    public void Tokenize_SplitsSymbolsAndText()
    {
        var tokens = ManaText.Tokenize("{T}, Sacrifice {R}: draw");
        Assert.Equal(4, tokens.Count);
        Assert.True(tokens[0].IsSymbol);
        Assert.Equal("{T}", tokens[0].Value);
        Assert.False(tokens[1].IsSymbol);
        Assert.Equal(", Sacrifice ", tokens[1].Value);
        Assert.True(tokens[2].IsSymbol);
        Assert.Equal("{R}", tokens[2].Value);
        Assert.Equal(": draw", tokens[3].Value);
    }

    [Fact]
    public void Tokenize_EmptyReturnsEmpty()
        => Assert.Empty(ManaText.Tokenize(""));

    [Theory]
    [InlineData("2RW", "{2}{R}{W}")]
    [InlineData("2 r w", "{2}{R}{W}")]
    [InlineData("10G", "{10}{G}")]
    [InlineData("wubrg", "{W}{U}{B}{R}{G}")]
    [InlineData("", "")]
    public void NormalizeCost_ConvertsLooseInput(string input, string expected)
        => Assert.Equal(expected, ManaText.NormalizeCost(input));

    [Fact]
    public void NormalizeCost_LeavesBracedInputUnchanged()
        => Assert.Equal("{2}{R}", ManaText.NormalizeCost("{2}{R}"));

    [Fact]
    public void SymbolTokens_ReturnsDistinctSymbols()
    {
        var tokens = ManaText.SymbolTokens("{R}{R}", "{T}: {R}").ToList();
        Assert.Equal(2, tokens.Count);
        Assert.Contains("{R}", tokens);
        Assert.Contains("{T}", tokens);
    }
}
