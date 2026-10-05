using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// 1.6.0 flip cards (Kamigawa): one card whose other half is printed upside down below the shared art.
/// Covers the model (persistence, one-level invariant, the half never becoming a printed side), the flip
/// layout derived from any drawn frame, hand-made frame versions (flip/ and landscape/ subfolders) for
/// picture frames, the renderer actually drawing the other half upside down, lookup, and the checks.
/// </summary>
public class FlipCardTests
{
    private static CardModel Flip(string template = "") => new()
    {
        Name = "Bushi Tenderfoot", ManaCost = "{W}", TypeLine = "Creature — Human Soldier",
        RulesText = "When a creature dealt damage by Bushi Tenderfoot this turn dies, flip Bushi Tenderfoot.",
        Power = "1", Toughness = "1", TemplateName = template, HalfLayout = "flip",
        OtherHalf = new CardModel
        {
            Name = "Kenzo the Hardhearted", TypeLine = "Legendary Creature — Human Samurai",
            RulesText = "Double strike; bushido 2", Power = "3", Toughness = "4",
        },
    };

    // --- model ---------------------------------------------------------------------------------------

    [Fact]
    public void FlipCard_SurvivesSaveAndReload_WithItsHalfStillMarked()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flip-{Guid.NewGuid():N}.json");
        try
        {
            Flip().Save(path);
            var back = CardModel.Load(path);
            Assert.True(back.IsFlip);
            Assert.Equal("Kenzo the Hardhearted", back.OtherHalf!.Name);
            Assert.True(back.OtherHalf.IsOtherHalf);   // transient flag is re-implied on load
            Assert.Equal("3", back.OtherHalf.Power);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void OtherHalf_IsOneLevelOnly()
    {
        var card = Flip();
        var nested = new CardModel { Name = "Deeper" };
        var half = new CardModel { Name = "Half", OtherHalf = nested, BackFace = new CardModel { Name = "Back" } };
        card.OtherHalf = half;
        Assert.Null(card.OtherHalf!.OtherHalf);
        Assert.Null(card.OtherHalf.BackFace);
    }

    [Fact]
    public void CopyFrom_DeepClonesTheOtherHalf_NeverAliasesIt()
    {
        var src = Flip();
        var dst = new CardModel();
        dst.CopyFrom(src);
        Assert.NotSame(src.OtherHalf, dst.OtherHalf);
        dst.OtherHalf!.Name = "Changed";
        Assert.Equal("Kenzo the Hardhearted", src.OtherHalf!.Name);
        Assert.False(dst.IsOtherHalf);
    }

    [Fact]
    public void OtherHalf_IsAPart_ButNeverAPrintedFace()
    {
        var card = Flip();
        Assert.Contains(card.OtherHalf, card.Parts());     // art paths etc. reach it
        Assert.DoesNotContain(card.OtherHalf, card.Faces());   // ...but it isn't a side of the card
        Assert.Single(BatchService.ExpandFaces(new[] { card }));   // one print slot, no back
    }

    [Fact]
    public void NullStringsInsideTheOtherHalf_AreCoalesced()
    {
        var json = "{\"name\":\"A\",\"halfLayout\":\"flip\",\"otherHalf\":{\"name\":\"B\",\"typeLine\":null}}";
        var path = Path.Combine(Path.GetTempPath(), $"flipnull-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);
            var card = CardModel.Load(path);
            Assert.Equal("", card.OtherHalf!.TypeLine);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void ALayoutWithoutAHalf_IsNotAFlipCard()
    {
        Assert.False(new CardModel { HalfLayout = "flip" }.IsFlip);
        Assert.False(new CardModel { OtherHalf = new CardModel() }.IsTwoPart);
    }

    // --- layout ---------------------------------------------------------------------------------------

    [Fact]
    public void ToFlip_ReadsNameRulesTypeThenArt_SymmetricAboutTheCentre()
    {
        var spec = new TemplateSpec().ToFlip();
        Assert.True(spec.IsFlipLayout);
        Assert.True(spec.TitleBar.Bottom <= spec.TextBox.Y);
        Assert.True(spec.TextBox.Bottom <= spec.TypeBar.Y);   // rules ABOVE the type line, like the real cards
        Assert.True(spec.TypeBar.Bottom <= spec.ArtWindow.Y);
        Assert.Equal(spec.CanvasHeight / 2.0, spec.ArtWindow.Y + spec.ArtWindow.H / 2, 3);   // art centred
        Assert.InRange(spec.ArtWindow.H / spec.CanvasHeight, 0.28, 0.40);
        // P/T sits at the right end of the type line, not in the rules box.
        Assert.Equal(spec.TypeBar.Right, spec.PtBox.Right, 3);
        Assert.True(spec.PtBox.Y < spec.TypeBar.Bottom && spec.PtBox.Bottom > spec.TypeBar.Y);
        Assert.Equal("border", spec.FooterPlacement);
        Assert.Same(spec, spec.ToFlip());   // idempotent
    }

    [Fact]
    public void ToFlip_KeepsTheRulesBoxItsOwnSize_EvenWithTheFooterOnTheBorder()
    {
        var spec = new TemplateSpec().ToFlip();
        Assert.Equal(spec.TextBox.H, spec.EffectiveTextBox.H);   // must not stretch down over the art
    }

    [Fact]
    public void ToFlip_FullArtFrame_KeepsTheArtFullBleed()
    {
        var full = new TemplateSpec { ArtWindow = new Region { X = 0, Y = 0, W = 750, H = 1050 } }.ToFlip();
        Assert.Equal(1050, full.ArtWindow.H);
    }

    [Fact]
    public void ResolveFor_FlipCardOnADrawnFrame_GetsTheDerivedFlipLayout() => TestHelpers.RunSta(() =>
    {
        var tpl = new TemplateService().LoadAll().First(t => !t.Spec.CustomFrame);
        var resolved = TemplateService.ResolveFor(Flip(tpl.Name), tpl);
        Assert.True(resolved.Spec.IsFlipLayout);
        Assert.Same(resolved, TemplateService.ResolveFor(Flip(tpl.Name), resolved));   // idempotent
        Assert.Same(tpl, TemplateService.ResolveFor(new CardModel { Name = "Plain" }, tpl));
    });

    [Fact]
    public void DerivedFlipFrame_BottomHalfIsTheTopHalfMirrored() => TestHelpers.RunSta(() =>
    {
        var tpl = new TemplateService().LoadAll().First(t => t.Name == "Crimson Red");
        var flip = TemplateService.FlipOf(tpl)!;
        var px = TestHelpers.Pixels(flip.FrameImage);
        int w = flip.FrameImage.PixelWidth, h = flip.FrameImage.PixelHeight;
        int diffs = 0, samples = 0;
        for (int y = 0; y < h / 2; y += 7)
            for (int x = 0; x < w; x += 11)
            {
                int a = (y * w + x) * 4, b = ((h - 1 - y) * w + x) * 4;
                samples++;
                if (Math.Abs(px[a] - px[b]) + Math.Abs(px[a + 1] - px[b + 1]) + Math.Abs(px[a + 2] - px[b + 2]) > 24) diffs++;
            }
        Assert.True(diffs < samples * 0.02, $"{diffs}/{samples} sampled rows differ from their mirror");
    });

    // --- rendering ------------------------------------------------------------------------------------

    [Fact]
    public void OtherHalf_IsDrawnInTheBottomHalf_UpsideDown() => TestHelpers.RunSta(() =>
    {
        var tpl = new TemplateService().LoadAll().First(t => t.Name == "Crimson Red");
        var r = new CardRenderer(new SymbolService());
        var withName = Flip(tpl.Name);
        var noName = Flip(tpl.Name); noName.OtherHalf!.Name = "";
        var a = r.RenderToBitmap(withName, tpl);
        var b = r.RenderToBitmap(noName, tpl);

        // Where does the other half's NAME draw? Only its pixels differ between the two renders.
        var (minX, minY, maxX, maxY) = DiffBox(a, b);
        int w = a.PixelWidth, h = a.PixelHeight;
        Assert.True(minY > h * 0.85, $"the other half's name drew at y={minY}, not along the bottom");
        // Upside down: a left-aligned name turned 180° hugs the RIGHT end of its bar (upright it would
        // start at the bar's left padding, ~x=64, and end well short of the right edge).
        Assert.True(minX > w * 0.30, $"name spans x={minX}..{maxX} — starts at the left, so not rotated");
        Assert.True(maxX > w * 0.85, $"name spans x={minX}..{maxX} — doesn't reach the right end");
    });

    [Fact]
    public void EveryBuiltInFlipLayout_PassesThePixelInspector() => TestHelpers.RunSta(() =>
    {
        // Credits ride the bottom border on a flip card; the border check used to measure inside a rounded
        // corner there and flag Showcase's perfectly good border as missing.
        var r = new CardRenderer(new SymbolService());
        foreach (var tpl in new TemplateService().LoadAll().Where(t => !t.Spec.CustomFrame))
        {
            var card = Flip(tpl.Name); card.SetCode = "TST"; card.CollectorNumber = "1"; card.Artist = "Tester";
            var resolved = TemplateService.ResolveFor(card, tpl);
            var issues = RenderInspector.Inspect(r.RenderToBitmap(card, resolved), card, resolved.Spec);
            Assert.DoesNotContain(issues, i => i.Code == "border-thin");
        }
    });

    [Fact]
    public void FrontEdits_OnlyChangeTheTopHalf() => TestHelpers.RunSta(() =>
    {
        var tpl = new TemplateService().LoadAll().First(t => t.Name == "Crimson Red");
        var r = new CardRenderer(new SymbolService());
        var a = Flip(tpl.Name);
        var b = Flip(tpl.Name); b.RulesText = "Something else entirely.";
        var (_, _, _, maxY) = DiffBox(r.RenderToBitmap(a, tpl), r.RenderToBitmap(b, tpl));
        Assert.True(maxY < 1050 / 2, $"front rules reached y={maxY}");
    });

    [Fact]
    public void FlipCard_OnAPictureFrameWithoutAFlipVersion_StillRendersItsFront() => TestHelpers.RunSta(() =>
    {
        var root = NewTemplateRoot(withFlip: false, withLandscape: false);
        try
        {
            var tpl = new TemplateService().LoadFrom(root).Single();
            var resolved = TemplateService.ResolveFor(Flip(tpl.Name), tpl);
            Assert.Same(tpl, resolved);
            Assert.True(TestHelpers.HasContent(new CardRenderer(new SymbolService()).RenderToBitmap(Flip(tpl.Name), tpl)));
        }
        finally { Directory.Delete(root, true); }
    });

    // --- frame versions -------------------------------------------------------------------------------

    [Fact]
    public void PictureFrame_UsesItsOwnFlipAndSidewaysVersions() => TestHelpers.RunSta(() =>
    {
        var root = NewTemplateRoot(withFlip: true, withLandscape: true);
        try
        {
            var tpl = new TemplateService().LoadFrom(root).Single();   // the variants aren't listed as frames
            Assert.Equal(2, tpl.Variants.Count);

            var flip = TemplateService.ResolveFor(Flip(tpl.Name), tpl);
            Assert.True(flip.Spec.IsFlipLayout);
            Assert.Equal(tpl.Name, flip.Name);   // a card's TemplateName still matches

            var battle = new CardModel { Name = "Siege", TypeLine = "Battle — Siege", TemplateName = tpl.Name };
            Assert.True(TemplateService.ResolveFor(battle, tpl).Spec.IsLandscape);
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public void AVariantOfTheWrongShape_IsIgnored() => TestHelpers.RunSta(() =>
    {
        var root = NewTemplateRoot(withFlip: false, withLandscape: false);
        try
        {
            // A flip/ folder whose spec isn't a flip layout would mis-render every flip card — skip it.
            var dir = Directory.GetDirectories(root).Single();
            WriteTemplate(Path.Combine(dir, "flip"), new TemplateSpec { Name = "X", CustomFrame = true }, 750, 1050);
            Assert.Empty(new TemplateService().LoadFrom(root).Single().Variants);
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public void ShippedSampleFrames_ComeWithFlipAndSidewaysVersions() => TestHelpers.RunSta(() =>
    {
        var all = new TemplateService().LoadAll();
        foreach (var name in new[] { "Alchemist's Steel", "Arcane Parchment", "Sealed Gate" })
        {
            var t = all.First(x => x.Name == name);
            Assert.True(t.Variants.ContainsKey(TemplateService.FlipVariant), $"{name} has no flip version");
            Assert.True(t.Variants.ContainsKey(TemplateService.LandscapeVariant), $"{name} has no sideways version");
        }
        Assert.DoesNotContain(all, t => t.Name.Contains("flip", StringComparison.OrdinalIgnoreCase));
    });

    [Fact]
    public void Bundle_ExportAndImport_CarriesTheFrameVersions() => TestHelpers.RunSta(() =>
    {
        var root = NewTemplateRoot(withFlip: true, withLandscape: true);
        var bundle = Path.Combine(Path.GetTempPath(), $"flipbundle-{Guid.NewGuid():N}.cardframe");
        string? imported = null;
        try
        {
            TemplateImporter.ExportBundle(Directory.GetDirectories(root).Single(), bundle);
            using (var zip = ZipFile.OpenRead(bundle))
            {
                Assert.Contains(zip.Entries, e => e.FullName == "flip/frame.png");
                Assert.Contains(zip.Entries, e => e.FullName == "landscape/template.json");
            }
            var name = TemplateImporter.ImportBundle(bundle);
            imported = Directory.GetDirectories(AppPaths.TemplatesDir)
                .First(d => File.Exists(Path.Combine(d, "template.json"))
                            && TemplateSpec.Load(Path.Combine(d, "template.json")).Name == name);
            Assert.True(File.Exists(Path.Combine(imported, "flip", "frame.png")));
            Assert.True(TemplateSpec.Load(Path.Combine(imported, "template.json")).CardLayout == "");   // main spec, not the flip one
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            try { File.Delete(bundle); } catch { }
            if (imported != null) try { Directory.Delete(imported, true); } catch { }
        }
    });

    // --- lookup & checks ------------------------------------------------------------------------------

    [Fact]
    public void LookingUpARealFlipCard_GivesOneCardWithItsHalf()
    {
        const string json = """
        {"object":"card","name":"Erayo, Soratami Ascendant // Erayo's Essence","layout":"flip","set":"sok",
         "collector_number":"35","rarity":"rare","mana_cost":"{1}{U} // ",
         "card_faces":[
          {"name":"Erayo, Soratami Ascendant","mana_cost":"{1}{U}","type_line":"Legendary Creature — Moonfolk Monk",
           "oracle_text":"Flying","power":"1","toughness":"1"},
          {"name":"Erayo's Essence","mana_cost":"","type_line":"Legendary Enchantment",
           "oracle_text":"Whenever an opponent casts their first spell each turn, counter that spell."}]}
        """;
        var faces = ScryfallMapper.MapFaces(json);
        var card = faces[0].Clone();
        var extras = CardDetailsFill.AttachFaces(card, faces);
        Assert.Empty(extras);   // not split off as a second card
        Assert.True(card.IsFlip);
        Assert.Equal("Erayo's Essence", card.OtherHalf!.Name);
        Assert.Equal("", card.OtherHalf.ManaCost);
        Assert.Null(card.BackFace);
    }

    [Fact]
    public void Checks_WarnWhenAPictureFrameCantShowTheFlippedHalf()
    {
        var picture = new TemplateSpec { CustomFrame = true };
        Assert.Contains(CardValidator.Validate(Flip(), picture), i => i.Code == "no-flip-frame");
        Assert.DoesNotContain(CardValidator.Validate(Flip(), picture.ToFlip()), i => i.Code == "no-flip-frame");
    }

    [Fact]
    public void Checks_CoverTheFlippedHalf_WithoutNaggingAboutArt()
    {
        var card = Flip();
        card.OtherHalf!.Toughness = "";   // a lone power
        var issues = CardValidator.ValidateOtherHalf(card, new TemplateSpec().ToFlip());
        Assert.Contains(issues, i => i.Code == "half-pt" && i.Message.StartsWith("Flipped half:"));
        Assert.DoesNotContain(issues, i => i.Code == "no-art");   // the half shares the card's art

        var set = SetValidator.ValidateAll(new[] { card }, _ => new TemplateSpec().ToFlip());
        Assert.Contains(set.Single().Issues, i => i.Message.StartsWith("Flipped half:"));
    }

    [Fact]
    public void CsvImport_FlipColumns_MakeAFlipCard()
    {
        var csv = "name,mana,type,pt,flip_name,flip_type,flip_rules,flip_pt\n"
                + "Bushi Tenderfoot,{W},Creature — Human Soldier,1/1,Kenzo the Hardhearted,Legendary Creature — Human Samurai,Double strike,3/4\n"
                + "Plain Bear,{1}{G},Creature — Bear,2/2,,,,\n";
        var cards = ImportService.Parse(csv, null, "");
        Assert.True(cards[0].Card.IsFlip);
        Assert.Equal("Kenzo the Hardhearted", cards[0].Card.OtherHalf!.Name);
        Assert.Equal("4", cards[0].Card.OtherHalf!.Toughness);
        Assert.False(cards[1].Card.IsFlip);   // blank flip columns leave a normal card alone
        Assert.Null(cards[1].Card.OtherHalf);
    }

    // --- helpers --------------------------------------------------------------------------------------

    private static (int minX, int minY, int maxX, int maxY) DiffBox(BitmapSource a, BitmapSource b)
    {
        var pa = TestHelpers.Pixels(a); var pb = TestHelpers.Pixels(b);
        int w = a.PixelWidth, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int i = 0; i < pa.Length; i += 4)
        {
            if (Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 1] - pb[i + 1]) + Math.Abs(pa[i + 2] - pb[i + 2]) < 30) continue;
            int p = i / 4, x = p % w, y = p / w;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        Assert.True(maxX >= 0, "the two renders were identical");
        return (minX, minY, maxX, maxY);
    }

    /// <summary>A temp templates root holding one picture frame, optionally with flip/ and landscape/ versions.</summary>
    private static string NewTemplateRoot(bool withFlip, bool withLandscape)
    {
        var root = Path.Combine(Path.GetTempPath(), $"fliptpl-{Guid.NewGuid():N}");
        var dir = Path.Combine(root, "picture-frame");
        WriteTemplate(dir, new TemplateSpec { Name = "Picture Frame " + Guid.NewGuid().ToString("N")[..6], CustomFrame = true }, 750, 1050);
        var name = TemplateSpec.Load(Path.Combine(dir, "template.json")).Name;
        if (withFlip)
        {
            var f = new TemplateSpec { Name = name, CustomFrame = true }.ToFlip();
            f.CustomFrame = true;
            WriteTemplate(Path.Combine(dir, "flip"), f, 750, 1050);
        }
        if (withLandscape)
            WriteTemplate(Path.Combine(dir, "landscape"),
                new TemplateSpec { Name = name, CustomFrame = true, CanvasWidth = 1050, CanvasHeight = 750 }, 1050, 750);
        return root;
    }

    private static void WriteTemplate(string dir, TemplateSpec spec, int w, int h)
    {
        Directory.CreateDirectory(dir);
        spec.Save(Path.Combine(dir, "template.json"));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(90, 30, 30)), null, new Rect(0, 0, w, h));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        CardExporter.SavePng(rtb, Path.Combine(dir, "frame.png"));
    }
}
