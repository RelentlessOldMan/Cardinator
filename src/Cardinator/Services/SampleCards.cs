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

    public static IReadOnlyList<CardModel> All(string templateName) => new[]
    {
        new CardModel
        {
            Name = "Ember Sprite, Chaos Crafter",
            ManaCost = "{2}{R}{W}",
            TypeLine = "Legendary Creature — Gnome Artificer",
            RulesText = "Haste\n\n{T}, Sacrifice an artifact creature: Create two Treasure tokens.\n\n{T}, Sacrifice a noncreature artifact: Create two 1/1 colorless Construct artifact creature tokens.",
            FlavorText = "\"Every creation demands a price. I intend to collect.\"",
            Artist = "Your Name Here",
            Power = "3",
            Toughness = "3",
            TemplateName = templateName,
        },
        new CardModel
        {
            Name = "Verdant Awakening",
            ManaCost = "{3}{G}",
            TypeLine = "Sorcery",
            RulesText = "Search your library for a basic land card, put it onto the battlefield tapped, then shuffle. You gain 3 life.",
            TemplateName = templateName,
        },
        new CardModel
        {
            Name = "Tidecaller Adept",
            ManaCost = "{1}{U}",
            TypeLine = "Creature — Merfolk Wizard",
            RulesText = "When Tidecaller Adept enters the battlefield, draw a card, then discard a card.\n\n{2}{U}: Tidecaller Adept can't be blocked this turn.",
            Power = "1",
            Toughness = "3",
            TemplateName = templateName,
        },
    };
}
