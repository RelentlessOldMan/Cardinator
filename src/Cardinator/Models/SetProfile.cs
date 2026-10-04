namespace Cardinator.Models;

/// <summary>
/// The set's house defaults (W1): metadata that a whole set shares — set code, rarity, copyright,
/// artist, and set-symbol image. Stored on the <see cref="CardProject"/> and auto-applied to each new
/// or imported card so the user doesn't re-type them per card. (The default <em>frame</em> is kept
/// separately on the project as <see cref="CardProject.DefaultTemplate"/>.)
/// </summary>
public sealed class SetProfile
{
    public string SetCode { get; set; } = "";
    public string Rarity { get; set; } = "";
    public string Copyright { get; set; } = "";
    public string Artist { get; set; } = "";

    /// <summary>A set-symbol image path shared across the set (travels with the set folder like card art).</summary>
    public string SetSymbolPath { get; set; } = "";

    /// <summary>True when nothing is set — so we can skip applying/saving an all-blank profile.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(SetCode)
        && string.IsNullOrWhiteSpace(Rarity)
        && string.IsNullOrWhiteSpace(Copyright)
        && string.IsNullOrWhiteSpace(Artist)
        && string.IsNullOrWhiteSpace(SetSymbolPath);

    /// <summary>
    /// Fills a card's <em>blank</em> profile fields from this profile. Never overwrites a value the card
    /// already has (so per-card overrides and real imported values win). Returns true if anything changed.
    /// </summary>
    public bool ApplyDefaults(CardModel card)
    {
        if (card == null) return false;
        bool changed = false;
        changed |= Fill(() => card.SetCode, v => card.SetCode = v, SetCode);
        changed |= Fill(() => card.Rarity, v => card.Rarity = v, Rarity);
        changed |= Fill(() => card.Copyright, v => card.Copyright = v, Copyright);
        changed |= Fill(() => card.Artist, v => card.Artist = v, Artist);
        changed |= Fill(() => card.SetSymbolPath, v => card.SetSymbolPath = v, SetSymbolPath);
        return changed;
    }

    private static bool Fill(System.Func<string?> get, System.Action<string> set, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;      // nothing to apply for this field
        if (!string.IsNullOrWhiteSpace(get())) return false;     // card already has its own value — keep it
        set(value);
        return true;
    }

    public SetProfile Clone() => new()
    {
        SetCode = SetCode, Rarity = Rarity, Copyright = Copyright, Artist = Artist, SetSymbolPath = SetSymbolPath,
    };
}
