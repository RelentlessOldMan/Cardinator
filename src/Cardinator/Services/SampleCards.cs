using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>
/// Built-in sample/default cards so the app is usable with zero network — a starting point
/// for making your own original cards. These are original examples, not real MTG cards.
/// </summary>
public static class SampleCards
{
    public static CardModel Blank(string templateName) => new()
    {
        Name = "New Card",
        ManaCost = "{2}{W}",
        TypeLine = "Creature — Human",
        RulesText = "",
        Power = "2",
        Toughness = "2",
        TemplateName = templateName,
    };

    /// <summary>
    /// Starter cards shown on first run — original examples with bundled art so the app looks alive
    /// immediately. Each pins a fitting built-in frame (rather than the passed default) so it showcases a
    /// different layout; <paramref name="templateName"/> is the fallback if a pinned frame is unavailable.
    /// </summary>
    public static IReadOnlyList<CardModel> All(string templateName)
    {
        SampleAssets.EnsureExtracted();
        string Art(string f) => SampleAssets.Path(f);
        return new[]
        {
            new CardModel
            {
                Name = "Seraph of the Last Light",
                ManaCost = "{3}{W}{W}",
                TypeLine = "Legendary Creature — Angel",
                RulesText = "Flying, vigilance, lifelink\nOther creatures you control get +1/+1.",
                FlavorText = "Where she passes, the dark forgets itself.",
                Power = "5", Toughness = "5", Rarity = "M", Artist = "Cardinator Demo",
                SetCode = "DSK", CollectorNumber = "12",
                ArtPath = Art("seraph-angel.png"),
                TemplateName = Has("Gold Multicolor", templateName),
            },
            new CardModel
            {
                Name = "Tatiana, Tide Weaver",
                ManaCost = "{1}{U}{U}",
                TypeLine = "Legendary Creature — Merfolk Druid",
                RulesText = "When Tatiana enters, draw a card, then discard a card.\n{2}{U}: Tatiana can't be blocked this turn.",
                FlavorText = "The tide remembers every shore it has touched.",
                Power = "3", Toughness = "4", Rarity = "R", Artist = "Cardinator Demo",
                SetCode = "TID", CollectorNumber = "58",
                ArtPath = Art("tatiana-merfolk.png"),
                TemplateName = Has("Ocean Blue", templateName),
            },
            new CardModel
            {
                Name = "Kavora, Storm Herald",
                ManaCost = "{4}{U}{R}",
                TypeLine = "Legendary Planeswalker — Kavora",
                RulesText = "+1: Add {U}{R}. Until end of turn, instants and sorceries you cast cost {1} less.\n-3: Kavora, Storm Herald deals 5 damage divided as you choose among up to two targets.\n-9: Draw seven cards.",
                Loyalty = "5", Rarity = "M", Artist = "Cardinator Demo",
                SetCode = "WAR", CollectorNumber = "42",
                ArtPath = Art("kavora-storm.png"),
                TemplateName = Has("Planeswalker", templateName),
            },
        };
    }

    /// <summary>The preferred frame name (samples pin one); callers pass the loaded template list's default
    /// as a fallback — the renderer already falls back safely if the pinned frame is missing.</summary>
    private static string Has(string preferred, string fallback)
        => string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
}
