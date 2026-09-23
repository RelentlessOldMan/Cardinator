using System.Text.Json;
using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>
/// Pure mapping from Scryfall card JSON to <see cref="CardModel"/>s. Kept separate from the HTTP
/// client so it can be unit-tested without a network. Returns one card for a normal card, or two
/// for a double-faced card (front + back).
/// </summary>
public static class ScryfallMapper
{
    public static List<CardModel> MapFaces(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return MapElement(doc.RootElement);
    }

    /// <summary>Maps the cards from a Scryfall /cards/search response (its <c>data[]</c> array).</summary>
    public static List<CardModel> MapSearch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<CardModel>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var el in data.EnumerateArray())
                result.AddRange(MapElement(el));
        return result;
    }

    /// <summary>Maps a single Scryfall card object (one card, or its faces).</summary>
    public static List<CardModel> MapElement(JsonElement root)
    {
        string layout = Str(root, "layout");
        string setCode = Up(Str(root, "set"));
        string collector = Str(root, "collector_number");
        string rarity = MapRarity(Str(root, "rarity"));

        bool hasFaces = root.TryGetProperty("card_faces", out var faces)
                        && faces.ValueKind == JsonValueKind.Array
                        && faces.GetArrayLength() >= 1;

        // Adventure: one physical card (the creature) with the spell shown in a sub-box.
        if (hasFaces && layout.Equals("adventure", StringComparison.OrdinalIgnoreCase)
            && faces.GetArrayLength() >= 2)
        {
            var creature = MapFace(faces[0], root, layout, setCode, collector, rarity);
            var adv = faces[1];
            creature.AdventureName = Str(adv, "name");
            creature.AdventureCost = Str(adv, "mana_cost");
            creature.AdventureType = Str(adv, "type_line");
            creature.AdventureText = Str(adv, "oracle_text");
            return new List<CardModel> { creature };
        }

        // Transform / modal DFC / split: return each face as its own card.
        if (hasFaces)
        {
            var list = new List<CardModel>();
            foreach (var face in faces.EnumerateArray())
                list.Add(MapFace(face, root, layout, setCode, collector, rarity));
            return list;
        }

        return new List<CardModel> { MapFace(root, root, layout, setCode, collector, rarity) };
    }

    private static CardModel MapFace(JsonElement el, JsonElement root, string layout,
        string setCode, string collector, string rarity) => new()
    {
        Name = Str(el, "name", root),
        ManaCost = Str(el, "mana_cost"),
        TypeLine = Str(el, "type_line", root),
        RulesText = Str(el, "oracle_text"),
        Power = Str(el, "power"),
        Toughness = Str(el, "toughness"),
        Loyalty = Str(el, "loyalty"),
        SetCode = setCode,
        CollectorNumber = collector,
        Rarity = rarity,
        Layout = layout,
        ArtUrl = ArtCrop(el, root),
    };

    /// <summary>The Scryfall "art crop" URL for a face, falling back to the card root.</summary>
    private static string ArtCrop(JsonElement el, JsonElement root)
    {
        string FromImageUris(JsonElement e)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("image_uris", out var u)
               ? Str(u, "art_crop") : "";
        var s = FromImageUris(el);
        return s.Length > 0 ? s : FromImageUris(root);
    }

    public static CardModel? MapFirst(string json) => MapFaces(json).FirstOrDefault();

    /// <summary>common/uncommon/rare/mythic -> C/U/R/M.</summary>
    public static string MapRarity(string rarity) => rarity.ToLowerInvariant() switch
    {
        "common" => "C",
        "uncommon" => "U",
        "rare" => "R",
        "mythic" or "special" or "bonus" => "M",
        _ => "",
    };

    private static string Str(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    /// <summary>Reads prop from el, falling back to the same prop on <paramref name="fallback"/>.</summary>
    private static string Str(JsonElement el, string prop, JsonElement fallback)
    {
        var s = Str(el, prop);
        return s.Length > 0 ? s : Str(fallback, prop);
    }

    private static string Up(string s) => s.ToUpperInvariant();
}
