using System.IO;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

public class ArtMatcherTests : IDisposable
{
    private readonly string _dir;

    public ArtMatcherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cardinator_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void Touch(string fileName) => File.WriteAllText(Path.Combine(_dir, fileName), "x");

    [Fact]
    public void MatchInto_MatchesByNormalizedFilename()
    {
        Touch("serra angel.png");
        Touch("lightning_bolt.jpg");

        var cards = new List<CardModel>
        {
            new() { Name = "Serra Angel" },
            new() { Name = "Lightning Bolt" },
            new() { Name = "Unmatched Card" },
        };

        int matched = ArtMatcher.MatchInto(cards, _dir, overwrite: false);

        Assert.Equal(2, matched);
        Assert.EndsWith("serra angel.png", cards[0].ArtPath);
        Assert.EndsWith("lightning_bolt.jpg", cards[1].ArtPath);
        Assert.Equal("", cards[2].ArtPath);
    }

    [Fact]
    public void MatchInto_SkipsCardsThatAlreadyHaveArt_UnlessOverwrite()
    {
        Touch("goblin.png");
        var cards = new List<CardModel> { new() { Name = "Goblin", ArtPath = @"C:\existing.png" } };

        Assert.Equal(0, ArtMatcher.MatchInto(cards, _dir, overwrite: false));
        Assert.Equal(@"C:\existing.png", cards[0].ArtPath);

        Assert.Equal(1, ArtMatcher.MatchInto(cards, _dir, overwrite: true));
        Assert.EndsWith("goblin.png", cards[0].ArtPath);
    }

    [Fact]
    public void MatchInto_ReturnsAbsolutePaths()
    {
        Touch("dragon.png");
        var cards = new List<CardModel> { new() { Name = "Dragon" } };
        ArtMatcher.MatchInto(cards, _dir, overwrite: false);
        Assert.True(Path.IsPathRooted(cards[0].ArtPath));
    }

    [Fact]
    public void MatchInto_ShortName_DoesNotSpuriouslyContainsMatch()
    {
        // A one-letter card name must NOT "contains"-match an unrelated long filename.
        Touch("elesh_norn_grand_cenobite.png");
        var cards = new List<CardModel> { new() { Name = "X" } };
        Assert.Equal(0, ArtMatcher.MatchInto(cards, _dir, overwrite: false));
        Assert.Equal("", cards[0].ArtPath);
    }

    [Fact]
    public void MatchInto_MissingFolder_ReturnsZero()
    {
        var cards = new List<CardModel> { new() { Name = "Anything" } };
        Assert.Equal(0, ArtMatcher.MatchInto(cards, Path.Combine(_dir, "nope"), overwrite: false));
    }

    [Fact]
    public void MatchIntoWithReport_SeparatesExactFromFuzzy()   // L2
    {
        Touch("serra angel.png");            // exact for "Serra Angel"
        Touch("goblin raider token.png");    // only a fuzzy (contains) match for "Goblin Raider"

        var cards = new List<CardModel>
        {
            new() { Name = "Serra Angel" },
            new() { Name = "Goblin Raider" },
            new() { Name = "Nothing Here" },
        };

        var report = ArtMatcher.MatchIntoWithReport(cards, _dir, overwrite: false);

        Assert.Equal(2, report.Total);
        Assert.Equal(1, report.Exact);
        Assert.Equal(1, report.Fuzzy);
        Assert.Equal("Goblin Raider", report.Fuzzies.Single().Card.Name);
        Assert.EndsWith("goblin raider token.png", report.Fuzzies.Single().ArtPath);
    }

    [Fact]
    public void MatchIntoWithReport_MissingFolder_IsEmpty()   // L2
    {
        var report = ArtMatcher.MatchIntoWithReport(
            new List<CardModel> { new() { Name = "X" } }, Path.Combine(_dir, "nope"), overwrite: false);
        Assert.Equal(0, report.Total);
        Assert.Empty(report.Fuzzies);
    }
}
