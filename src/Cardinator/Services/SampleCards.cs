using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>
/// Built-in sample/default cards so the app is usable with zero network — a starting point
/// for making your own original cards. These are original examples, not real MTG cards.
/// </summary>
public static class SampleCards
{
    /// <summary>A card with nothing on it but a name to replace: type a real card's name and Look up (or Fill in
    /// blanks) fills it, or write your own. (It used to be a {2}{W} 2/2 Human, so Fill in blanks skipped it.)</summary>
    public static CardModel Blank(string templateName) => new()
    {
        Name = "New Card",
        TemplateName = templateName,
    };

    /// <summary>A token to start from: a 1/1 Soldier, no cost, no rules (so no text box until it gets some).
    /// The "Token" in its type line is what gives it the token layout.</summary>
    public static CardModel BlankToken(string templateName) => new()
    {
        Name = "Soldier",
        TypeLine = "Token Creature — Soldier",
        Power = "1",
        Toughness = "1",
        TemplateName = templateName,
    };

    /// <summary>
    /// The single starter card shown on first run — an original example with its own bundled art so the app
    /// looks alive immediately. It pins a fitting built-in frame; <paramref name="templateName"/> is the
    /// fallback if that frame is unavailable. (Only this one card's art is embedded in the exe.)
    /// </summary>
    public static IReadOnlyList<CardModel> All(string templateName)
    {
        SampleAssets.EnsureExtracted();
        return new[]
        {
            new CardModel
            {
                Name = "Seraph of the Last Light",
                ManaCost = "{3}{W}{W}",
                TypeLine = "Legendary Creature — Angel",
                RulesText = "Flying, vigilance, lifelink\nOther creatures you control get +1/+1.",
                FlavorText = StarterFlavor,
                Power = "5", Toughness = "5", Rarity = "M", Artist = StarterArtist,
                SetCode = "DSK", CollectorNumber = "12",
                ArtPath = SampleAssets.Path("seraph-angel.png"),
                TemplateName = Has("Gold Multicolor", templateName),
            },
        };
    }

    private const string StarterFlavor = "Where she passes, the dark forgets itself.";
    private const string StarterArtist = "Cardinator Demo";

    /// <summary>After a lookup turned the starter card into a real one: its sample flavor text and "Cardinator Demo"
    /// credit don't belong on Lightning Bolt. The user's own flavor and artist are left alone.</summary>
    public static void ClearStarterLeftovers(CardModel card)
    {
        if (card.FlavorText == StarterFlavor) card.FlavorText = "";
        if (card.Artist == StarterArtist) card.Artist = "";
    }

    /// <summary>The preferred frame name (samples pin one); callers pass the loaded template list's default
    /// as a fallback — the renderer already falls back safely if the pinned frame is missing.</summary>
    private static string Has(string preferred, string fallback)
        => string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
}
