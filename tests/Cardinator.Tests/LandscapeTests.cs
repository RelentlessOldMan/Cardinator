using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// Landscape (sideways) cards — Battles, Planes and Phenomena. Orientation is automatic by card type, any
/// portrait drawn frame re-lays-out sideways, Battles get a Defense shield of their own, and print sheets
/// turn the card back upright into a normal slot.
/// </summary>
public class LandscapeTests
{
    private static CardModel Siege(string defense = "5") => new()
    {
        Name = "Invasion of Testing", ManaCost = "{2}{R}", TypeLine = "Battle — Siege", Defense = defense,
        RulesText = "When this enters, it deals 3 damage to any target.",
    };

    // --- which cards are sideways ---------------------------------------------

    [Theory]
    [InlineData("Battle — Siege", true)]
    [InlineData("Plane — Dominaria", true)]
    [InlineData("Phenomenon", true)]
    [InlineData("Legendary Planeswalker — Jace", false)]   // "Plane" must match as a whole word
    [InlineData("Creature — Human Soldier", false)]
    [InlineData("Instant", false)]
    public void LandscapeTypes_AreRecognisedByWholeWord(string typeLine, bool landscape)
        => Assert.Equal(landscape, new CardModel { TypeLine = typeLine }.IsLandscapeType);

    [Fact]
    public void ABattle_IsNotTreatedAsAPlaneswalker()
    {
        // Defense has its own field precisely so a Battle doesn't pick up planeswalker ability badges.
        var siege = Siege();
        Assert.True(siege.IsBattle);
        Assert.False(siege.IsPlaneswalker);
    }

    [Theory]
    [InlineData("", "Battle — Siege", true)]
    [InlineData("auto", "Battle — Siege", true)]
    [InlineData("portrait", "Battle — Siege", false)]
    [InlineData("landscape", "Creature — Elf", true)]
    [InlineData("", "Creature — Elf", false)]
    public void Orientation_CanBeForcedEitherWay(string orientation, string typeLine, bool expected)
        => Assert.Equal(expected, new CardModel { TypeLine = typeLine, Orientation = orientation }.WantsLandscape);

    // --- every portrait frame lays out sideways --------------------------------

    public static IEnumerable<object[]> BuiltIns() => BuiltInTemplates.All().Select(s => new object[] { s.Name });

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void ToLandscape_ProducesASaneLayout_ForEveryBuiltInFrame(string name)
    {
        var portrait = BuiltInTemplates.All().First(s => s.Name == name);
        portrait.Normalize();
        var land = portrait.ToLandscape();

        Assert.Equal(portrait.CanvasHeight, land.CanvasWidth);
        Assert.Equal(portrait.CanvasWidth, land.CanvasHeight);
        Assert.True(land.IsLandscape);

        // Every region stays on the card.
        foreach (var (label, r) in new[] { ("title", land.TitleBar), ("art", land.ArtWindow), ("type", land.TypeBar),
                                           ("text", land.TextBox), ("pt", land.PtBox), ("credit", land.CreditBar) })
        {
            Assert.True(r.X >= 0 && r.Y >= 0 && r.Right <= land.CanvasWidth + 0.5 && r.Bottom <= land.CanvasHeight + 0.5,
                $"{name}: {label} ({r.X:F0},{r.Y:F0},{r.W:F0},{r.H:F0}) falls off the {land.CanvasWidth}x{land.CanvasHeight} card");
            Assert.True(r.W > 0 && r.H > 0, $"{name}: {label} collapsed");
        }

        // Plates keep their size (nothing squashed); top-to-bottom order is unchanged.
        Assert.Equal(portrait.TitleBar.H, land.TitleBar.H, 3);
        Assert.Equal(portrait.TypeBar.H, land.TypeBar.H, 3);
        Assert.Equal(portrait.PtBox.H, land.PtBox.H, 3);
        Assert.True(land.TitleBar.Bottom <= land.TypeBar.Y, $"{name}: title overlaps the type line");
        Assert.True(land.TypeBar.Bottom <= land.TextBox.Y + 0.5, $"{name}: type line overlaps the text box");

        // The words keep at least 40% of their box (legibility over art height).
        Assert.True(land.TextBox.H >= portrait.TextBox.H * 0.40 - 0.5,
            $"{name}: text box shrank to {land.TextBox.H:F0} from {portrait.TextBox.H:F0}");

        // The P/T box keeps its distance from the bottom-right corner.
        Assert.Equal(portrait.CanvasHeight - portrait.PtBox.Bottom, land.CanvasHeight - land.PtBox.Bottom, 3);
        Assert.Equal(portrait.CanvasWidth - portrait.PtBox.Right, land.CanvasWidth - land.PtBox.Right, 3);
    }

    [Fact]
    public void ToLandscape_KeepsAFullArtWindowFullBleed()
    {
        var fullArt = BuiltInTemplates.All().First(s => s.Name == "Full Art");
        var land = fullArt.ToLandscape();
        Assert.Equal(0, land.ArtWindow.X);
        Assert.Equal(0, land.ArtWindow.Y);
        Assert.Equal(land.CanvasWidth, land.ArtWindow.W);
        Assert.Equal(land.CanvasHeight, land.ArtWindow.H);
    }

    [Fact]
    public void ToLandscape_OnALandscapeSpec_IsANoOp()
    {
        var land = BuiltInTemplates.All().First().ToLandscape();
        Assert.Same(land, land.ToLandscape());
    }

    // --- which template a face renders with -----------------------------------

    private static Template Portrait(string name = "Crimson Red") =>
        new TemplateService().LoadAll().First(t => t.Name == name);

    [Fact]
    public void ABattle_OnAPortraitFrame_RendersWithItsLandscapeLayout()
        => TestHelpers.RunSta(() =>
        {
            var t = Portrait();
            var resolved = TemplateService.ResolveFor(Siege(), t);

            Assert.True(resolved.Spec.IsLandscape);
            Assert.Equal(t.Name, resolved.Name);                     // still "the same frame" to the user
            // A real landscape frame was generated (frames are baked at 2x, so check the shape, not the size).
            Assert.True(resolved.FrameImage.PixelWidth > resolved.FrameImage.PixelHeight);
            Assert.Same(resolved, TemplateService.ResolveFor(Siege(), resolved));   // idempotent
            Assert.Same(resolved, TemplateService.ResolveFor(Siege(), t));          // cached
        });

    [Fact]
    public void OrdinaryCards_AndForcedPortrait_KeepThePortraitFrame()
        => TestHelpers.RunSta(() =>
        {
            var t = Portrait();
            Assert.Same(t, TemplateService.ResolveFor(new CardModel { TypeLine = "Creature — Elf" }, t));
            var upright = Siege(); upright.Orientation = "portrait";
            Assert.Same(t, TemplateService.ResolveFor(upright, t));
        });

    [Fact]
    public void AnImportedRasterFrame_IsNeverReLaidOut()
    {
        // A custom frame is a fixed image — turning it would distort the user's art.
        var spec = new TemplateSpec { Name = "Mine", CustomFrame = true };
        var blank = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        var t = new Template { Name = "Mine", Spec = spec, FramePath = "", FrameImage = blank };
        Assert.Same(t, TemplateService.ResolveFor(Siege(), t));
    }

    // --- rendering ------------------------------------------------------------

    [Fact]
    public void ABattle_RendersSideways_AndItsTransformedBack_Upright()
        => TestHelpers.RunSta(() =>
        {
            var t = Portrait();
            var renderer = new CardRenderer(new SymbolService());
            var battle = Siege();
            battle.BackFace = new CardModel { Name = "Testing, Conquered", TypeLine = "Creature — Dragon", Power = "5", Toughness = "5" };

            var front = renderer.RenderToBitmap(battle, t, supersample: 1);
            var back = renderer.RenderToBitmap(battle.BackFace, t, supersample: 1);

            Assert.Equal((1050, 750), (front.PixelWidth, front.PixelHeight));
            Assert.Equal((750, 1050), (back.PixelWidth, back.PixelHeight));
        });

    [Fact]
    public void TheDefenseShield_IsDrawn()
        => TestHelpers.RunSta(() =>
        {
            var t = Portrait();
            var renderer = new CardRenderer(new SymbolService());
            Assert.NotEqual(TestHelpers.Pixels(renderer.RenderToBitmap(Siege("5"), t)),
                            TestHelpers.Pixels(renderer.RenderToBitmap(Siege(""), t)));
        });

    [Fact]
    public void ALandscapeRender_PassesThePixelInspector()
        => TestHelpers.RunSta(() =>
        {
            // The inspector must be given the geometry that was drawn; with it, a good card is clean.
            var card = Siege();
            card.Artist = "A"; card.CollectorNumber = "1/1"; card.SetCode = "TST";
            var t = TemplateService.ResolveFor(card, Portrait());
            var bmp = new CardRenderer(new SymbolService()).RenderToBitmap(card, t);
            Assert.DoesNotContain(RenderInspector.Inspect(bmp, card, t.Spec), i => i.Severity == IssueSeverity.Error);
        });

    // --- printing: sideways cards go back into a normal slot -------------------

    [Fact]
    public void FitToSlot_TurnsALandscapeRenderUpright_AndLeavesPortraitAlone()
        => TestHelpers.RunSta(() =>
        {
            var land = BitmapSource.Create(1050, 750, 96, 96, PixelFormats.Bgra32, null, new byte[1050 * 750 * 4], 1050 * 4);
            var port = BitmapSource.Create(750, 1050, 96, 96, PixelFormats.Bgra32, null, new byte[750 * 1050 * 4], 750 * 4);

            var turned = SheetExporter.FitToSlot(land);
            Assert.Equal((750, 1050), (turned.PixelWidth, turned.PixelHeight));
            Assert.Same(port, SheetExporter.FitToSlot(port));
        });

    [Fact]
    public void APrintSheet_HoldsABattleInANormalSlot()
        => TestHelpers.RunSta(() =>
        {
            var templates = new TemplateService().LoadAll();
            var battle = Siege(); battle.TemplateName = "Crimson Red";
            var pages = SheetExporter.Compose(new List<CardModel> { battle }, templates, new SymbolService(), PageSpec.Letter).ToList();

            Assert.Single(pages);
            Assert.Equal(2550, pages[0].PixelWidth);                 // page size is unchanged

            // The slot (top-left at 150,75, drawn 1:1) must hold the card TURNED upright — not the sideways
            // render stretched to fit, which would also fill the slot and so can't be caught by "not blank".
            var expected = SheetExporter.FitToSlot(new CardRenderer(new SymbolService()).RenderToBitmap(battle, templates.First(x => x.Name == "Crimson Red")));
            int mismatches = 0, samples = 0;
            for (int y = 60; y < 1050; y += 97)
                for (int x = 60; x < 750; x += 89)
                {
                    var a = new byte[4]; var b = new byte[4];
                    new CroppedBitmap(pages[0], new System.Windows.Int32Rect(150 + x, 75 + y, 1, 1)).CopyPixels(a, 4, 0);
                    new CroppedBitmap(new FormatConvertedBitmap(expected, PixelFormats.Bgra32, null, 0),
                        new System.Windows.Int32Rect(x, y, 1, 1)).CopyPixels(b, 4, 0);
                    samples++;
                    if (Math.Abs(a[0] - b[0]) + Math.Abs(a[1] - b[1]) + Math.Abs(a[2] - b[2]) > 24) mismatches++;
                }
            Assert.True(mismatches <= samples / 20, $"{mismatches}/{samples} sampled pixels differ from the upright card");
        });

    // --- data paths -----------------------------------------------------------

    [Fact]
    public void Scryfall_MapsABattlesDefense()
    {
        const string json = """
        { "layout":"battle", "name":"Invasion of Tarkir // Defiant Thundermaw", "card_faces":[
            { "name":"Invasion of Tarkir", "mana_cost":"{1}{R}", "type_line":"Battle — Siege", "oracle_text":"x", "defense":"5" },
            { "name":"Defiant Thundermaw", "mana_cost":"", "type_line":"Creature — Dragon", "oracle_text":"y", "power":"5", "toughness":"5" } ] }
        """;
        var faces = ScryfallMapper.MapFaces(json);

        Assert.Equal("5", faces[0].Defense);
        Assert.True(faces[0].WantsLandscape);
        Assert.False(faces[1].WantsLandscape);                  // the transformed back is a creature, upright

        var card = new CardModel { Name = "Invasion of Tarkir" };
        CardDetailsFill.AttachFaces(card, faces);              // "battle" is a genuinely two-sided layout
        Assert.True(card.IsDoubleFaced);
    }

    [Fact]
    public void CsvImport_ReadsDefenseAndOrientationColumns()
    {
        var cards = ImportService.Parse("name,type,defense,orientation\nSiege Test,Battle — Siege,4,\nSideways Elf,Creature — Elf,,landscape\n", null, "");
        Assert.Equal("4", cards[0].Card.Defense);
        Assert.Equal("landscape", cards[1].Card.Orientation);
        Assert.True(cards[1].Card.WantsLandscape);
    }

    [Fact]
    public void OldFilesWithoutTheNewFields_StayUpright()
    {
        var path = Path.Combine(Path.GetTempPath(), "pre15_" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{ "name": "Old Card", "typeLine": "Creature — Elf", "power": "1", "toughness": "1" }""");
        try
        {
            var card = CardModel.Load(path);
            Assert.Equal("", card.Defense);
            Assert.Equal("", card.Orientation);
            Assert.False(card.WantsLandscape);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void CHECKS_ExplainWhenAnImportedFrameCantGoSideways()
    {
        var custom = new TemplateSpec { Name = "Mine", CustomFrame = true };
        custom.Normalize();
        var drawn = new TemplateSpec { Name = "Drawn" };
        drawn.Normalize();

        Assert.Contains(CardValidator.Validate(Siege(), custom), i => i.Code == "portrait-only-frame");
        Assert.DoesNotContain(CardValidator.Validate(Siege(), drawn), i => i.Code == "portrait-only-frame");
    }

    [Fact]
    public void ImportingAWideFrameImage_MakesALandscapeTemplate()
        => TestHelpers.RunSta(() =>
        {
            int w = 105, h = 75, stride = w * 4;
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, new byte[h * stride], stride)));
            using var ms = new MemoryStream();
            enc.Save(ms);

            var name = TemplateImporter.CreateFromFrame("Wide " + Guid.NewGuid().ToString("N")[..6], ms.ToArray());
            var dir = Path.Combine(AppPaths.TemplatesDir, TextUtil.Slug(name));
            try
            {
                var spec = TemplateSpec.Load(Path.Combine(dir, "template.json"));
                Assert.True(spec.IsLandscape);
                Assert.True(spec.CustomFrame);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });
}
