using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>The starter templates Cardinator generates on first run. All share the same layout
/// and fonts, differing only in frame colors â€” a good base to tweak or add to.</summary>
public static class BuiltInTemplates
{
    public static IEnumerable<TemplateSpec> All()
    {
        yield return Make("Gold Multicolor", "#B8912B", "#F0DA97", "#ECE6D2", "#6E5A1E", "ornate");
        yield return Make("Ocean Blue", "#2E6E9E", "#9FC7E0", "#DCE8F0", "#234F6E", "clean");
        yield return Make("Forest Green", "#3E7A44", "#A9CF9F", "#E1ECDD", "#2A5730", "classic");
        yield return Make("Crimson Red", "#A23A2E", "#E3A79A", "#F0DED9", "#6E241C", "ornate");
        yield return Make("Slate Artifact", "#6E7378", "#C4C9CE", "#E6E8EA", "#3E4348", "clean", crown: false);
        yield return Make("Planeswalker", "#6B4E8E", "#C9B3E0", "#E7DEF0", "#3E2E56", "classic");
        yield return Make("Midnight", "#23262E", "#4A5060", "#D9DDE6", "#12141A", "clean");
        yield return Make("Parchment", "#C9B88C", "#EBDDB4", "#F3ECD8", "#8A784A", "ornate");
        yield return Make("Sunset Orange", "#C46A24", "#F0BC7E", "#F5E4CE", "#7E4012", "faded");
        yield return MakeFullArt();
        yield return MakeShowcase();
        yield return MakeCinematic();
        yield return MakeTidecaller();
        yield return MakeModern();
        yield return MakeShowcaseConnected();
    }

    /// <summary>
    /// Full-bleed art with connected two-tone panels at the top (title) and bottom (type + text) plus a
    /// regal top crown â€” the "borderless showcase with connected boxes" look (thing4).
    /// </summary>
    private static TemplateSpec MakeShowcaseConnected() => new()
    {
        Name = "Ironwrought Showcase",
        CanvasWidth = 750,
        CanvasHeight = 1050,
        FrameStyle = "composable",
        PanelBackground = false,     // full-bleed art shows through
        ConnectedPanels = true,      // two-tone connected panels
        BottomTaper = false,
        TopEmblem = "regal",
        Colors = new FrameColors { Border = "#0A0A0A", Frame = "#8A3B2E", Frame2 = "#D9906E", Panel = "#EEE3D5", PanelBorder = "#4E1C12" },
        TitleBar = new Region { X = 48, Y = 54, W = 654, H = 56 },
        ArtWindow = new Region { X = 0, Y = 0, W = 750, H = 1050 },
        TypeBar = new Region { X = 48, Y = 726, W = 654, H = 52 },
        TextBox = new Region { X = 56, Y = 784, W = 638, H = 184 },
        PtBox = new Region { X = 572, Y = 926, W = 126, H = 72 },
        CreditBar = new Region { X = 54, Y = 976, W = 460, H = 34 },
        TitleFont = new FontSpec { Family = "Georgia", Size = 30, Bold = true, Align = "left", Color = "#12242E" },
        TypeFont = new FontSpec { Family = "Georgia", Size = 23, Bold = true, Align = "left", Color = "#12242E" },
        RulesFont = new FontSpec { Family = "Georgia", Size = 24, Align = "left", Color = "#14222A" },
        FlavorFont = new FontSpec { Family = "Georgia", Size = 23, Italic = true, Align = "left", Color = "#3A4650" },
        PtFont = new FontSpec { Family = "Georgia", Size = 32, Bold = true, Align = "center", Color = "#F4ECDE", Shadow = true },
        CreditFont = new FontSpec { Family = "Segoe UI", Size = 13, Italic = true, Align = "left", Color = "#F2ECE0", Shadow = true },
        ManaSymbolSize = 35,
        RulesSymbolSize = 26,
    };

    /// <summary>
    /// A modern (M15-style) frame: a textured colored background the panels sit on, joined by a
    /// two-tone pinline, with curved-out nameplates and a tapered base.
    /// </summary>
    private static TemplateSpec MakeModern() => new()
    {
        Name = "Azure Modern",
        CanvasWidth = 750,
        CanvasHeight = 1050,
        FrameStyle = "modern",
        TextureStrength = 0.4,
        Colors = new FrameColors { Border = "#0A0A0A", Frame = "#6098C2", Frame2 = "#AED2E8", Panel = "#EAF1F6", PanelBorder = "#2A5B80" },
        TitleBar = new Region { X = 48, Y = 54, W = 654, H = 58 },
        ArtWindow = new Region { X = 60, Y = 124, W = 630, H = 458 },
        TypeBar = new Region { X = 48, Y = 616, W = 654, H = 52 },
        TextBox = new Region { X = 48, Y = 676, W = 654, H = 278 },
        PtBox = new Region { X = 574, Y = 916, W = 126, H = 72 },
        CreditBar = new Region { X = 54, Y = 962, W = 470, H = 36 },
        TitleFont = new FontSpec { Family = "Georgia", Size = 32, Bold = true, Align = "left", Color = "#0C2436" },
        TypeFont = new FontSpec { Family = "Georgia", Size = 24, Bold = true, Align = "left", Color = "#0C2436" },
        RulesFont = new FontSpec { Family = "Georgia", Size = 25, Align = "left", Color = "#12222E" },
        FlavorFont = new FontSpec { Family = "Georgia", Size = 24, Italic = true, Align = "left", Color = "#3A4650" },
        PtFont = new FontSpec { Family = "Georgia", Size = 32, Bold = true, Align = "center", Color = "#0C2436" },
        CreditFont = new FontSpec { Family = "Segoe UI", Size = 13, Italic = true, Align = "left", Color = "#0C2436" },
        ManaSymbolSize = 35,
        RulesSymbolSize = 26,
    };

    /// <summary>
    /// A "wave" showcase frame: a full classic frame plus a colorful scalloped wave crown flowing
    /// across the top of the card (over the frame, above the title) â€” the flowing/colorful edge look.
    /// The title bar is dropped a little to leave room for the crown.
    /// </summary>
    private static TemplateSpec MakeTidecaller() => new()
    {
        Name = "Tidecaller",
        CanvasWidth = 750,
        CanvasHeight = 1050,
        FrameStyle = "wave",
        Embellishments = false,   // the wave crown is the top ornament
        Colors = new FrameColors { Border = "#0A0A0A", Frame = "#1E6E7A", Frame2 = "#5FD3DE", Panel = "#E4F2F1", PanelBorder = "#12454C" },
        TitleBar = new Region { X = 48, Y = 58, W = 654, H = 58 },
        ArtWindow = new Region { X = 48, Y = 136, W = 654, H = 476 },
        TypeBar = new Region { X = 48, Y = 622, W = 654, H = 56 },
        TextBox = new Region { X = 48, Y = 688, W = 654, H = 286 },
        PtBox = new Region { X = 572, Y = 926, W = 126, H = 72 },
        CreditBar = new Region { X = 54, Y = 980, W = 460, H = 38 },
        TitleFont = new FontSpec { Family = "Georgia", Size = 33, Bold = true, Align = "left", Color = "#0C2A2E" },
        TypeFont = new FontSpec { Family = "Georgia", Size = 24, Bold = true, Align = "left", Color = "#0C2A2E" },
        RulesFont = new FontSpec { Family = "Georgia", Size = 25, Align = "left", Color = "#12242A" },
        FlavorFont = new FontSpec { Family = "Georgia", Size = 24, Italic = true, Align = "left", Color = "#33474C" },
        PtFont = new FontSpec { Family = "Georgia", Size = 32, Bold = true, Align = "center", Color = "#0C2A2E" },
        CreditFont = new FontSpec { Family = "Segoe UI", Size = 13, Italic = true, Align = "left", Color = "#0C2A2E" },
        ManaSymbolSize = 35,
        RulesSymbolSize = 26,
    };

    /// <summary>
    /// A "full art with a frame over it" card: the artwork fills the whole card, and a cinematic
    /// dark band with gold trim sits over the lower portion where the type line + rules text are
    /// drawn directly on the art (Ã  la mtgcardsmith showcase full-arts). P/T box on creatures.
    /// </summary>
    private static TemplateSpec MakeCinematic() => new()
    {
        Name = "Cinematic",
        CanvasWidth = 750,
        CanvasHeight = 1050,
        FrameStyle = "overlay",
        BorderThickness = 28,
        Colors = new FrameColors { Border = "#0A0A0A", Frame = "#B8912B", Frame2 = "#F0DA97", Panel = "#12141B", PanelBorder = "#6E5A1E" },
        TitleBar = new Region { X = 44, Y = 40, W = 662, H = 64 },
        ArtWindow = new Region { X = 0, Y = 0, W = 750, H = 1050 },
        TypeBar = new Region { X = 44, Y = 720, W = 662, H = 52 },
        TextBox = new Region { X = 52, Y = 780, W = 646, H = 150 },
        PtBox = new Region { X = 572, Y = 926, W = 126, H = 72 },
        CreditBar = new Region { X = 50, Y = 966, W = 460, H = 40 },
        TitleFont = new FontSpec { Family = "Georgia", Size = 35, Bold = true, Align = "left", Color = "#F6E9C7", Shadow = true },
        TypeFont = new FontSpec { Family = "Georgia", Size = 24, Bold = true, Align = "left", Color = "#F6E9C7", Shadow = true },
        RulesFont = new FontSpec { Family = "Georgia", Size = 24, Align = "left", Color = "#F4F5F8", Shadow = true },
        FlavorFont = new FontSpec { Family = "Georgia", Size = 23, Italic = true, Align = "left", Color = "#DADDE4", Shadow = true },
        PtFont = new FontSpec { Family = "Georgia", Size = 32, Bold = true, Align = "center", Color = "#F6E9C7", Shadow = true },
        CreditFont = new FontSpec { Family = "Segoe UI", Size = 13, Italic = true, Align = "left", Color = "#E7E3D6", Shadow = true },
        ManaSymbolSize = 35,
        RulesSymbolSize = 26,
    };

    /// <summary>
    /// A "full art" template: the artwork fills the whole card and the text sits directly on it,
    /// with a light legibility scrim and white shadowed fonts. A good starting point for cards
    /// whose background already is the finished art.
    /// </summary>
    private static TemplateSpec MakeFullArt() => new()
    {
        Name = "Full Art",
        CanvasWidth = 750,
        CanvasHeight = 1050,
        FullArt = true,
        BorderThickness = 28,
        Colors = new FrameColors
        {
            Border = "#0A0A0A", Frame = "#C9A24B", Frame2 = "#E7D08A", Panel = "#101216", PanelBorder = "#000000",
        },
        TitleBar = new Region { X = 40, Y = 34, W = 670, H = 64 },
        ArtWindow = new Region { X = 0, Y = 0, W = 750, H = 1050 },
        TypeBar = new Region { X = 40, Y = 742, W = 670, H = 52 },
        TextBox = new Region { X = 40, Y = 800, W = 670, H = 176 },
        PtBox = new Region { X = 572, Y = 926, W = 126, H = 72 },
        CreditBar = new Region { X = 44, Y = 982, W = 460, H = 36 },
        TitleFont = new FontSpec { Family = "Georgia", Size = 36, Bold = true, Align = "left", Color = "#FFFFFF", Shadow = true },
        TypeFont = new FontSpec { Family = "Georgia", Size = 24, Bold = true, Align = "left", Color = "#FFFFFF", Shadow = true },
        RulesFont = new FontSpec { Family = "Georgia", Size = 24, Align = "left", Color = "#FFFFFF", Shadow = true },
        FlavorFont = new FontSpec { Family = "Georgia", Size = 23, Italic = true, Align = "left", Color = "#ECECEC", Shadow = true },
        PtFont = new FontSpec { Family = "Georgia", Size = 32, Bold = true, Align = "center", Color = "#FFFFFF", Shadow = true },
        CreditFont = new FontSpec { Family = "Segoe UI", Size = 13, Italic = true, Align = "left", Color = "#E8E8E8", Shadow = true },
        ManaSymbolSize = 35,
        RulesSymbolSize = 26,
    };

    /// <summary>Borderless "showcase": full-bleed art with translucent floating panels for the text.</summary>
    private static TemplateSpec MakeShowcase() => new()
    {
        Name = "Showcase",
        CanvasWidth = 750,
        CanvasHeight = 1050,
        FrameStyle = "borderless",
        BorderThickness = 28,
        Colors = new FrameColors { Border = "#0A0A0A", Frame = "#2A2E38", Frame2 = "#5A6172", Panel = "#12141B", PanelBorder = "#0A0B10" },
        TitleBar = new Region { X = 40, Y = 36, W = 670, H = 62 },
        ArtWindow = new Region { X = 0, Y = 0, W = 750, H = 1050 },
        TypeBar = new Region { X = 40, Y = 748, W = 670, H = 50 },
        TextBox = new Region { X = 40, Y = 800, W = 670, H = 176 },
        PtBox = new Region { X = 572, Y = 926, W = 126, H = 72 },
        CreditBar = new Region { X = 44, Y = 982, W = 460, H = 36 },
        TitleFont = new FontSpec { Family = "Georgia", Size = 34, Bold = true, Align = "left", Color = "#FFFFFF", Shadow = true },
        TypeFont = new FontSpec { Family = "Georgia", Size = 23, Bold = true, Align = "left", Color = "#FFFFFF", Shadow = true },
        RulesFont = new FontSpec { Family = "Georgia", Size = 23, Align = "left", Color = "#F2F3F6", Shadow = true },
        FlavorFont = new FontSpec { Family = "Georgia", Size = 22, Italic = true, Align = "left", Color = "#D8DBE2", Shadow = true },
        PtFont = new FontSpec { Family = "Georgia", Size = 32, Bold = true, Align = "center", Color = "#FFFFFF", Shadow = true },
        CreditFont = new FontSpec { Family = "Segoe UI", Size = 13, Italic = true, Align = "left", Color = "#D8DBE2", Shadow = true },
        ManaSymbolSize = 35,
        RulesSymbolSize = 26,
    };

    private static TemplateSpec Make(string name, string frame, string frame2, string panel, string panelBorder, string style = "classic", bool crown = true) => new()
    {
        Name = name,
        CanvasWidth = 750,
        CanvasHeight = 1050,
        FrameStyle = style,
        LegendaryCrown = crown,
        Colors = new FrameColors
        {
            Border = "#0A0A0A",
            Frame = frame,
            Frame2 = frame2,
            Panel = panel,
            PanelBorder = panelBorder,
        },
        TitleBar = new Region { X = 48, Y = 46, W = 654, H = 60 },   // ~18px gap above (frame/crown) and below
        ArtWindow = new Region { X = 48, Y = 124, W = 654, H = 464 },
        TypeBar = new Region { X = 48, Y = 598, W = 654, H = 56 },
        TextBox = new Region { X = 48, Y = 662, W = 654, H = 296 },
        PtBox = new Region { X = 572, Y = 926, W = 126, H = 72 },   // evenly offset (~18px) from the right + bottom border
        TitleFont = new FontSpec { Family = "Georgia", Size = 34, Bold = true, Align = "left", Color = "#141414" },
        TypeFont = new FontSpec { Family = "Georgia", Size = 25, Bold = true, Align = "left", Color = "#141414" },
        RulesFont = new FontSpec { Family = "Georgia", Size = 25, Align = "left", Color = "#141414" },
        PtFont = new FontSpec { Family = "Georgia", Size = 32, Bold = true, Align = "center", Color = "#141414" },
        ManaSymbolSize = 35,
        RulesSymbolSize = 26,
    };
}


