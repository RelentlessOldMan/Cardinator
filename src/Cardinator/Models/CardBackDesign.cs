namespace Cardinator.Models;

/// <summary>
/// The back every single-faced card in a set shares — what a real deck's cards show when face down. One of three:
/// <list type="bullet">
/// <item><c>default</c>: Cardinator's own back (a dark panel, a diamond emblem, a wordmark).</item>
/// <item><c>designed</c>: the same layout in the set's own colours, with its own wordmark and, if wanted, the set
/// symbol in place of the diamond.</item>
/// <item><c>picture</c>: an image of the user's own, filling the card.</item>
/// </list>
/// Stored on the set's <see cref="SetProfile"/>; an older set has none, which is the default back.
/// </summary>
public sealed class CardBackDesign
{
    public const string DefaultStyle = "default", DesignedStyle = "designed", PictureStyle = "picture";

    public string Style { get; set; } = DefaultStyle;

    /// <summary>The words under the emblem on a designed back (blank: the set's name).</summary>
    public string Wordmark { get; set; } = "";

    /// <summary>A designed back's panel colour and its accent (the keylines, the emblem's edge, the wordmark).</summary>
    public string Color { get; set; } = "#1F2A44";
    public string Accent { get; set; } = "#C9A24B";

    /// <summary>A designed back shows the set symbol in place of the diamond, when the set has one.</summary>
    public bool UseSetSymbol { get; set; } = true;

    /// <summary>A picture back's image (travels with the set folder like card art).</summary>
    public string ImagePath { get; set; } = "";

    /// <summary>Whatever a newer Cardinator stored here that this one doesn't know, written back unchanged on save.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>? Unknown { get; set; }

    public bool IsDefault => !string.Equals(Style, DesignedStyle, System.StringComparison.OrdinalIgnoreCase)
                             && !(string.Equals(Style, PictureStyle, System.StringComparison.OrdinalIgnoreCase) && ImagePath.Trim().Length > 0);

    public CardBackDesign Clone()
    {
        var c = (CardBackDesign)MemberwiseClone();
        if (Unknown != null) c.Unknown = new(Unknown);
        return c;
    }
}
