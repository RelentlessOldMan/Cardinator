using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cardinator.Models;

/// <summary>
/// The editable content of a single card. Fields are auto-filled from Scryfall but every
/// one can be overridden by hand (so fully original cards work too). Implements
/// INotifyPropertyChanged so the live preview updates as fields change.
/// </summary>
public sealed class CardModel : INotifyPropertyChanged
{
    private string _name = "";
    private string _manaCost = "";
    private string _typeLine = "";
    private string _rulesText = "";
    private string _flavorText = "";
    private string _artist = "";
    private string _power = "";
    private string _toughness = "";
    private string _loyalty = "";
    private string _setCode = "";
    private string _collectorNumber = "";
    private string _rarity = "";
    private string _copyright = "";
    private string _layout = "";
    private string _adventureName = "";
    private string _adventureCost = "";
    private string _adventureType = "";
    private string _adventureText = "";
    private string _artPath = "";
    private double _artScale = 1.0;
    private double _artOffsetX;
    private double _artOffsetY;
    private string _templateName = "";

    /// <summary>Card title, e.g. "Edward Elric, The Fullmetal Alchemist".</summary>
    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>Mana cost using Scryfall tokens, e.g. "{2}{R}{W}{B}".</summary>
    public string ManaCost { get => _manaCost; set => Set(ref _manaCost, value); }

    /// <summary>Type line, e.g. "Legendary Creature — Gnome Artificer".</summary>
    public string TypeLine { get => _typeLine; set => Set(ref _typeLine, value); }

    /// <summary>Rules text; may contain inline symbols like {T} and {R}. Newlines separate abilities.</summary>
    public string RulesText { get => _rulesText; set => Set(ref _rulesText, value); }

    /// <summary>Italic flavor text shown below the rules, separated by a divider.</summary>
    public string FlavorText { get => _flavorText; set => Set(ref _flavorText, value); }

    /// <summary>Artist credit shown along the bottom (optional).</summary>
    public string Artist { get => _artist; set => Set(ref _artist, value); }

    public string Power { get => _power; set => Set(ref _power, value); }
    public string Toughness { get => _toughness; set => Set(ref _toughness, value); }

    /// <summary>Starting loyalty for planeswalkers (empty otherwise).</summary>
    public string Loyalty { get => _loyalty; set => Set(ref _loyalty, value); }

    /// <summary>Set code shown in the footer, e.g. "CST".</summary>
    public string SetCode { get => _setCode; set => Set(ref _setCode, value); }

    /// <summary>Collector number shown in the footer, e.g. "1".</summary>
    public string CollectorNumber { get => _collectorNumber; set => Set(ref _collectorNumber, value); }

    /// <summary>Rarity: C, U, R, or M (drives the rarity pip color).</summary>
    public string Rarity { get => _rarity; set => Set(ref _rarity, value); }

    /// <summary>Optional copyright/footer text shown bottom-right.</summary>
    public string Copyright { get => _copyright; set => Set(ref _copyright, value); }

    /// <summary>Scryfall layout hint, e.g. "normal", "adventure", "saga", "transform".</summary>
    public string Layout { get => _layout; set => Set(ref _layout, value); }

    /// <summary>Adventure sub-spell (shown in a sub-box on the creature card).</summary>
    public string AdventureName { get => _adventureName; set => Set(ref _adventureName, value); }
    public string AdventureCost { get => _adventureCost; set => Set(ref _adventureCost, value); }
    public string AdventureType { get => _adventureType; set => Set(ref _adventureType, value); }
    public string AdventureText { get => _adventureText; set => Set(ref _adventureText, value); }

    /// <summary>Absolute path to the custom art image (may be empty).</summary>
    public string ArtPath { get => _artPath; set => Set(ref _artPath, value); }

    /// <summary>Art zoom multiplier on top of the cover-fit baseline (1.0 = fill window).</summary>
    public double ArtScale { get => _artScale; set => Set(ref _artScale, value); }

    /// <summary>Art pan, in fractions of the art window (-1..1 typical).</summary>
    public double ArtOffsetX { get => _artOffsetX; set => Set(ref _artOffsetX, value); }
    public double ArtOffsetY { get => _artOffsetY; set => Set(ref _artOffsetY, value); }

    /// <summary>Name of the template to render with (matches a folder under templates/).</summary>
    public string TemplateName { get => _templateName; set => Set(ref _templateName, value); }

    /// <summary>
    /// Scryfall art URL from the last lookup (the "art crop"), used to offer real art from the
    /// internet. Transient — not saved with the project (the downloaded file is referenced by
    /// <see cref="ArtPath"/> instead).
    /// </summary>
    [JsonIgnore]
    public string ArtUrl { get; set; } = "";

    [JsonIgnore]
    public bool HasPowerToughness => !string.IsNullOrWhiteSpace(Power) || !string.IsNullOrWhiteSpace(Toughness);

    [JsonIgnore]
    public bool IsPlaneswalker =>
        TypeLine.Contains("Planeswalker", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(Loyalty);

    [JsonIgnore]
    public bool IsSaga => TypeLine.Contains("Saga", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsClass => TypeLine.Contains("Class", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsAdventure =>
        string.Equals(Layout, "adventure", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(AdventureName);

    // --- persistence ---------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static CardModel Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<CardModel>(json, JsonOpts)
               ?? throw new InvalidDataException($"Could not parse card: {path}");
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }

    public CardModel Clone() =>
        (CardModel)JsonSerializer.Deserialize<CardModel>(
            JsonSerializer.Serialize(this, JsonOpts), JsonOpts)!;

    // --- INotifyPropertyChanged ---------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? prop = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        if (prop is nameof(Power) or nameof(Toughness))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPowerToughness)));
    }
}
