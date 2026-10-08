using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// 1.6.2 split cards (<i>Wear // Tear</i>, Duskmourn Rooms) and 1.6.3 aftermath (<i>Destined // Lead</i>): one card whose two halves are small cards side
/// by side, read with the card turned sideways. Covers the model, the shared reminder bar (Fuse / a Room's
/// door rules), the layout geometry, the renderer putting each half where a real split card has it, every
/// frame (drawn and picture) working unchanged, lookup (incl. cutting Scryfall's side-by-side art), CSV, and
/// the checks.
/// </summary>
public class SplitCardTests
{
    private const string Fuse = "Fuse (You may cast one or both halves of this card from your hand.)";

    private static CardModel Split(string template = "") => new()
    {
        Name = "Wear", ManaCost = "{1}{R}", TypeLine = "Instant", RulesText = "Destroy target artifact.\n" + Fuse,
        SetCode = "MOC", CollectorNumber = "343", Rarity = "U", Artist = "Ryan Pancoast",
        TemplateName = template, HalfLayout = "split",
        OtherHalf = new CardModel
        {
            Name = "Tear", ManaCost = "{W}", TypeLine = "Instant", RulesText = "Destroy target enchantment.\n" + Fuse,
        },
    };

    private static CardModel Aftermath(string template = "") => new()
    {
        Name = "Destined", ManaCost = "{1}{B}", TypeLine = "Instant",
        RulesText = "Target creature gets +1/+0 and gains indestructible until end of turn.",
        SetCode = "AKH", CollectorNumber = "217", Rarity = "U", TemplateName = template, HalfLayout = "split",
        OtherHalf = new CardModel
        {
            Name = "Lead", ManaCost = "{3}{G}", TypeLine = "Sorcery",
            RulesText = "Aftermath (Cast this spell only from your graveyard. Then exile it.)\nAll creatures able to block target creature this turn do so.",
        },
    };

    // --- model ---------------------------------------------------------------------------------------

    [Fact]
    public void SplitCard_SurvivesSaveAndReload_AndIsOnePrintedSide()
    {
        var path = Path.Combine(Path.GetTempPath(), $"split-{Guid.NewGuid():N}.json");
        try
        {
            Split().Save(path);
            var back = CardModel.Load(path);
            Assert.True(back.IsSplit);
            Assert.False(back.IsFlip);
            Assert.Equal("{W}", back.OtherHalf!.ManaCost);
            Assert.True(back.OtherHalf.IsOtherHalf);
            Assert.Single(back.Faces());             // prints as ONE card, no back
            Assert.Equal(2, back.Parts().Count());   // but the half's art travels with the set
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // --- the shared reminder bar ---------------------------------------------------------------------

    [Fact]
    public void Fuse_ComesOffBothHalves_AndIsSharedOnce()
    {
        var (front, half, shared) = CardRenderer.SplitSharedLine(Split());
        Assert.Equal(Fuse, shared);
        Assert.Equal("Destroy target artifact.", front);
        Assert.Equal("Destroy target enchantment.", half);
    }

    [Fact]
    public void ARoomsDoorReminder_IsShared()
    {
        const string door = "(You may cast either half. That door unlocks on the battlefield.)";
        var card = Split();
        card.RulesText = "When you unlock this door, draw a card.\n" + door;
        card.OtherHalf!.RulesText = "Creatures you control get +1/+0.\r\n" + door;
        Assert.Equal(door, CardRenderer.SplitSharedLine(card).shared);
    }

    [Theory]
    [InlineData("Draw a card.", "Draw a card.")]                                // alike, but not a reminder
    [InlineData("Destroy target artifact.\n" + Fuse, "Destroy target enchantment.")]   // only one half has it
    public void HalvesThatMerelyEndAlike_KeepTheirText(string a, string b)
    {
        var card = Split();
        card.RulesText = a; card.OtherHalf!.RulesText = b;
        var (front, half, shared) = CardRenderer.SplitSharedLine(card);
        Assert.Equal("", shared);
        Assert.Equal(a, front);
        Assert.Equal(b, half);
    }

    // --- geometry ------------------------------------------------------------------------------------

    /// <summary>Where a half's whole canvas lands on the upright card.</summary>
    private static Rect OnCard(CardRenderer.SplitPart part)
    {
        var r = new Rect(0, 0, part.Width, part.Height);
        r.Transform(part.ToCard);
        return r;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Geometry_TwoEqualCardShapedHalves_StackedUpTheCard_InsideIt(bool fuse) => TestHelpers.RunSta(() =>
    {
        var tpl = new TemplateService().LoadAll().First(t => t.Name == "Crimson Red");
        double W = tpl.Spec.CanvasWidth, H = tpl.Spec.CanvasHeight;
        var card = Split(tpl.Name);
        if (!fuse) { card.RulesText = "Destroy target artifact."; card.OtherHalf!.RulesText = "Destroy target enchantment."; }
        var g = CardRenderer.SplitGeometry(card, tpl);
        var a = OnCard(g.First); var b = OnCard(g.Other);
        var cardRect = new Rect(0, 0, W, H + 0.001);

        Assert.False(g.Aftermath);
        Assert.True(cardRect.Contains(a) && cardRect.Contains(b), $"{a} / {b} leave the card");
        Assert.True(b.Bottom < a.Top, "the other half isn't above the first (or they overlap)");
        Assert.Equal(a.Width, b.Width, 3);
        Assert.Equal(H / W, a.Width / a.Height, 3);           // card-shaped, lying on its side
        Assert.True(a.Bottom <= H - g.FooterStrip + 0.001, "a half runs into the card's credits edge");
        Assert.True(g.First.Scale > 0.55, $"halves drawn too small ({g.First.Scale:0.00})");
        Assert.Equal(fuse, !g.Bar.IsEmpty);
    });

    [Fact]
    public void Geometry_TheFirstHalfReadsWithTheCardTurnedClockwise() => TestHelpers.RunSta(() =>
    {
        var tpl = new TemplateService().LoadAll().First(t => t.Name == "Crimson Red");
        var g = CardRenderer.SplitGeometry(Split(tpl.Name), tpl);
        // The half's own "up" (its title bar) runs along the card's LEFT edge; its right edge is the card's top.
        var up = g.First.ToCard.Transform(new Vector(0, -1));
        var right = g.First.ToCard.Transform(new Vector(1, 0));
        Assert.True(up.X < 0 && Math.Abs(up.Y) < 1e-9, $"up = {up}");
        Assert.True(right.Y < 0 && Math.Abs(right.X) < 1e-9, $"right = {right}");
        Assert.Equal(new Point(0, 1050), CardRenderer.SplitLayout.ReadingToCard(1050).Transform(new Point(0, 0)));
    });

    [Fact]
    public void Aftermath_FirstHalfUprightAcrossTheTop_OtherHalfTurnedTheOtherWayBelow() => TestHelpers.RunSta(() =>
    {
        var tpl = new TemplateService().LoadAll().First(t => t.Name == "Crimson Red");
        double W = tpl.Spec.CanvasWidth, H = tpl.Spec.CanvasHeight;
        var card = Aftermath(tpl.Name);
        Assert.True(card.IsSplit && card.IsAftermath);
        var g = CardRenderer.SplitGeometry(card, tpl);
        var a = OnCard(g.First); var b = OnCard(g.Other);

        Assert.True(g.Aftermath);
        Assert.True(g.First.Template.Spec.IsLandscape, "the top half isn't the frame's wide layout");
        Assert.True(g.First.ToCard.M12 == 0 && g.First.ToCard.M11 > 0, "the top half isn't upright");
        Assert.True(a.Bottom < b.Top, "the top half isn't above the other half (or they overlap)");
        Assert.True(a.Width > W * 0.9, "the top half doesn't span the card");
        Assert.True(new Rect(0, 0, W, H + 0.001).Contains(b));
        Assert.True(b.Bottom <= H - g.FooterStrip + 0.001);
        // The other half's "up" is the card's RIGHT edge (read with the card turned counter-clockwise).
        var up = g.Other.ToCard.Transform(new Vector(0, -1));
        Assert.True(up.X > 0 && Math.Abs(up.Y) < 1e-9, $"up = {up}");
        Assert.Equal("", g.SharedLine);   // its Aftermath reminder is the half's own text, not a shared bar
    });

    [Fact]
    public void OnlyAnAftermathKeyword_MakesItAftermath()
    {
        Assert.False(Split().IsAftermath);
        var card = Split(); card.OtherHalf!.RulesText = "  aftermath (Cast this spell only from your graveyard.)";
        Assert.True(card.IsAftermath);
        card.HalfLayout = "flip";
        Assert.False(card.IsAftermath);   // only a split card
    }

    [Fact]
    public void PartFromCard_FindsTheHalfUnderAPoint_AndTurnsMovesIntoIt() => TestHelpers.RunSta(() =>
    {
        var tpl = new TemplateService().LoadAll().First(t => t.Name == "Crimson Red");
        foreach (var card in new[] { Split(tpl.Name), Aftermath(tpl.Name) })
        {
            var g = CardRenderer.SplitGeometry(card, tpl);
            foreach (var (part, other) in new[] { (g.First, g.Other), (g.Other, g.First) })
            {
                var centre = part.ToCard.Transform(new Point(part.Width / 2, part.Height / 2));
                var back = part.FromCard(centre)!.Value;
                Assert.Equal(part.Width / 2, back.X, 3);
                Assert.Equal(part.Height / 2, back.Y, 3);
                Assert.Null(other.FromCard(centre));
                // A move across the half's whole width on the card is a move of its whole width in the half.
                var across = part.ToCard.Transform(new Vector(part.Width, 0));
                Assert.Equal(part.Width, part.FromCard(across).X, 3);
            }
        }
    });

    // --- rendering -----------------------------------------------------------------------------------

    [Fact]
    public void ResolveFor_KeepsTheUprightFrame_EvenForASidewaysType() => TestHelpers.RunSta(() =>
    {
        var tpl = new TemplateService().LoadAll().First(t => !t.Spec.CustomFrame);
        var card = Split(tpl.Name);
        card.TypeLine = "Battle — Siege";   // would otherwise turn landscape
        Assert.Same(tpl, TemplateService.ResolveFor(card, tpl));
    });

    [Fact]
    public void FirstHalf_IsTheBottomOfTheCard_OtherHalfTheTop_ReadSideways() => TestHelpers.RunSta(() =>
    {
        var tpl = new TemplateService().LoadAll().First(t => t.Name == "Crimson Red");
        var r = new CardRenderer(new SymbolService());
        var full = r.RenderToBitmap(Split(tpl.Name), tpl);
        int h = full.PixelHeight;

        var noFront = Split(tpl.Name); noFront.Name = "";
        var (fx0, fy0, fx1, fy1) = DiffBox(full, r.RenderToBitmap(noFront, tpl));
        Assert.True(fy0 > h / 2, $"the first half's name drew at y={fy0}..{fy1}, not in the bottom half");
        Assert.True(fy1 - fy0 > fx1 - fx0, "the name isn't running up the card (sideways)");
        Assert.True(fx0 < 750 * 0.2, $"the name isn't along the card's left edge (x={fx0})");

        var noHalf = Split(tpl.Name); noHalf.OtherHalf!.Name = "";
        var (_, hy0, _, hy1) = DiffBox(full, r.RenderToBitmap(noHalf, tpl));
        Assert.True(hy1 < h / 2, $"the other half's name drew at y={hy0}..{hy1}, not in the top half");
    });

    [Fact]
    public void EachHalf_ShowsItsOwnArt() => TestHelpers.RunSta(() =>
    {
        var dir = Directory.CreateTempSubdirectory("split-art").FullName;
        try
        {
            var red = SolidPng(Path.Combine(dir, "red.png"), Colors.Red);
            var blue = SolidPng(Path.Combine(dir, "blue.png"), Colors.Blue);
            var tpl = new TemplateService().LoadAll().First(t => t.Name == "Crimson Red");
            var card = Split(tpl.Name);
            card.ArtPath = red; card.OtherHalf!.ArtPath = blue;
            var bmp = new CardRenderer(new SymbolService()).RenderToBitmap(card, tpl);
            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            var px = TestHelpers.Pixels(bmp);
            int Count(int y0, int y1, Func<byte, byte, byte, bool> hit)
            {
                int n = 0;
                for (int y = y0; y < y1; y += 3) for (int x = 0; x < w; x += 3)
                { int i = (y * w + x) * 4; if (hit(px[i + 2], px[i + 1], px[i])) n++; }
                return n;
            }
            static bool Red(byte r, byte g, byte b) => r > 200 && g < 40 && b < 40;
            static bool Blue(byte r, byte g, byte b) => b > 200 && r < 40 && g < 40;
            Assert.True(Count(h / 2, h, Red) > 1000 && Count(h / 2, h, Blue) == 0, "the bottom half isn't the first half's art");
            Assert.True(Count(0, h / 2, Blue) > 1000 && Count(0, h / 2, Red) == 0, "the top half isn't the other half's art");
        }
        finally { Directory.Delete(dir, true); }
    });

    [Fact]
    public void EveryInstalledFrame_DrawsASplitCard_AndItsHalvesPassThePixelInspector() => TestHelpers.RunSta(() =>
    {
        var r = new CardRenderer(new SymbolService());
        foreach (var tpl in new TemplateService().LoadAll())
        foreach (var card in new[] { Split(tpl.Name), Aftermath(tpl.Name) })
        {
            var bmp = r.RenderToBitmap(card, tpl);
            Assert.True(TestHelpers.HasContent(bmp), tpl.Name);
            Assert.Empty(RenderInspector.Inspect(bmp, card, tpl.Spec));   // the whole render isn't measured…
            var issues = RenderInspector.InspectCard(r, card, tpl, bmp);  // …its halves are, as normal cards
            Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
        }
    });

    // --- lookup, art, CSV, checks --------------------------------------------------------------------

    [Fact]
    public void LookingUpARealSplitCard_GivesOneCardWithBothHalves()
    {
        const string json = """
        {"object":"card","name":"Wear // Tear","layout":"split","set":"moc","collector_number":"343",
         "rarity":"uncommon","mana_cost":"{1}{R} // {W}","type_line":"Instant // Instant",
         "image_uris":{"art_crop":"https://cards.scryfall.io/art_crop/front/e/0/x.jpg"},
         "card_faces":[
          {"name":"Wear","mana_cost":"{1}{R}","type_line":"Instant","oracle_text":"Destroy target artifact.\nFuse (You may cast one or both halves of this card from your hand.)"},
          {"name":"Tear","mana_cost":"{W}","type_line":"Instant","oracle_text":"Destroy target enchantment.\nFuse (You may cast one or both halves of this card from your hand.)"}]}
        """;
        var faces = ScryfallMapper.MapFaces(json);
        var card = faces[0].Clone();
        Assert.Empty(CardDetailsFill.AttachFaces(card, faces));   // not split off as a second card
        Assert.True(card.IsSplit);
        Assert.Equal("Tear", card.OtherHalf!.Name);
        Assert.Equal("{W}", card.OtherHalf.ManaCost);             // a split half keeps its own cost
        Assert.Null(card.BackFace);
    }

    [Fact]
    public void ScryfallsSideBySideArt_IsCutBetweenTheHalves() => TestHelpers.RunSta(() =>
    {
        var dir = Directory.CreateTempSubdirectory("split-cut").FullName;
        try
        {
            var path = Path.Combine(dir, "both.png");
            var rt = new RenderTargetBitmap(200, 80, 96, 96, PixelFormats.Pbgra32);
            var v = new DrawingVisual();
            using (var dc = v.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 100, 80));
                dc.DrawRectangle(Brushes.Blue, null, new Rect(100, 0, 100, 80));
            }
            rt.Render(v);
            CardExporter.SavePng(rt, path);

            var card = Split(); card.ArtPath = path;
            CardDetailsFill.SplitSharedArt(card);
            Assert.NotEqual(path, card.ArtPath);
            var left = Load(card.ArtPath); var right = Load(card.OtherHalf!.ArtPath);
            Assert.Equal(100, left.PixelWidth);
            Assert.Equal(255, TestHelpers.Pixels(left)[2]);    // red
            Assert.Equal(255, TestHelpers.Pixels(right)[0]);   // blue

            // A half that already has art is the user's: left alone.
            var mine = Split(); mine.ArtPath = path; mine.OtherHalf!.ArtPath = "mine.png";
            CardDetailsFill.SplitSharedArt(mine);
            Assert.Equal(path, mine.ArtPath);
            Assert.Equal("mine.png", mine.OtherHalf.ArtPath);
        }
        finally { Directory.Delete(dir, true); }
    });

    [Fact]
    public void CsvImport_SplitColumns_MakeASplitCard()
    {
        var csv = "name,mana,type,rules,split_name,split_cost,split_type,split_rules\n"
                + "Fire,{1}{R},Instant,Fire deals 2 damage.,Ice,{1}{U},Instant,Tap target permanent.\n"
                + "Plain Bear,{1}{G},Creature — Bear,,,,,\n";
        var cards = ImportService.Parse(csv, null, "");
        Assert.True(cards[0].Card.IsSplit);
        Assert.Equal("Ice", cards[0].Card.OtherHalf!.Name);
        Assert.Equal("{1}{U}", cards[0].Card.OtherHalf!.ManaCost);
        Assert.False(cards[1].Card.IsTwoPart);
    }

    [Fact]
    public void Checks_CoverTheOtherHalf_IncludingItsOwnArt()
    {
        var card = Split();
        var issues = CardValidator.ValidateOtherHalf(card, new TemplateSpec());
        Assert.Contains(issues, i => i.Code == "no-art" && i.Message.StartsWith("Other half:"));

        card.OtherHalf!.ArtPath = @"C:\nowhere\gone.png";
        Assert.Contains(CardValidator.ValidateOtherHalf(card, new TemplateSpec()), i => i.Code == "art-missing");
    }

    [Fact]
    public void Aftermath_TopHalfDrawsAtTheTop_OtherHalfBelowRunningDown() => TestHelpers.RunSta(() =>
    {
        var tpl = new TemplateService().LoadAll().First(t => t.Name == "Crimson Red");
        var r = new CardRenderer(new SymbolService());
        var full = r.RenderToBitmap(Aftermath(tpl.Name), tpl);
        int h = full.PixelHeight;

        var noTop = Aftermath(tpl.Name); noTop.Name = "";
        var (tx0, ty0, tx1, ty1) = DiffBox(full, r.RenderToBitmap(noTop, tpl));
        Assert.True(ty1 < h * 0.2 && tx1 - tx0 > ty1 - ty0, $"the top half's name drew at {tx0},{ty0}..{tx1},{ty1}");

        var noHalf = Aftermath(tpl.Name); noHalf.OtherHalf!.Name = "";
        var (hx0, hy0, hx1, hy1) = DiffBox(full, r.RenderToBitmap(noHalf, tpl));
        Assert.True(hy0 > h / 2, $"the other half's name drew at y={hy0}, not in the bottom part");
        Assert.True(hy1 - hy0 > hx1 - hx0, $"the other half's name isn't running down the card ({hx0},{hy0}..{hx1},{hy1})");
        Assert.True(hx0 > 750 * 0.8, $"the other half's name isn't along the card's right edge (x={hx0})");
    });

    [Fact]
    public void AftermathArt_IsCutWhereTheWidePictureEnds() => TestHelpers.RunSta(() =>
    {
        var dir = Directory.CreateTempSubdirectory("after-cut").FullName;
        try
        {
            var path = SolidPng(Path.Combine(dir, "both.png"), Colors.Gray, 1000, 200);
            var card = Aftermath(); card.ArtPath = path;
            CardDetailsFill.SplitSharedArt(card);
            Assert.Equal(613, Load(card.ArtPath).PixelWidth);
            Assert.Equal(387, Load(card.OtherHalf!.ArtPath).PixelWidth);
        }
        finally { Directory.Delete(dir, true); }
    });

    // --- helpers --------------------------------------------------------------------------------------

    private static string SolidPng(string path, Color color, int w = 60, int h = 60)
    {
        var rt = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, w, h));
        rt.Render(v);
        CardExporter.SavePng(rt, path);
        return path;
    }

    private static BitmapSource Load(string path)
    {
        var img = new BitmapImage();
        img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = new MemoryStream(File.ReadAllBytes(path)); img.EndInit(); img.Freeze();
        return new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
    }

    /// <summary>The box around what changed between two renders. A row or column counts only when at least
    /// <see cref="MinChanged"/> of its pixels changed, so a stray anti-aliased pixel elsewhere on the card can't
    /// stretch the box (a one-pixel blip made the aftermath test flaky on CI); a drawn name changes far more.</summary>
    private static (int minX, int minY, int maxX, int maxY) DiffBox(BitmapSource a, BitmapSource b)
    {
        var pa = TestHelpers.Pixels(a); var pb = TestHelpers.Pixels(b);
        int w = a.PixelWidth, h = a.PixelHeight;
        var cols = new int[w]; var rows = new int[h];
        for (int i = 0; i < pa.Length; i += 4)
        {
            if (Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 1] - pb[i + 1]) + Math.Abs(pa[i + 2] - pb[i + 2]) < 30) continue;
            int p = i / 4;
            cols[p % w]++; rows[p / w]++;
        }
        int minX = Array.FindIndex(cols, c => c >= MinChanged), maxX = Array.FindLastIndex(cols, c => c >= MinChanged);
        int minY = Array.FindIndex(rows, c => c >= MinChanged), maxY = Array.FindLastIndex(rows, c => c >= MinChanged);
        Assert.True(maxX >= 0 && maxY >= 0, "the two renders were (all but) identical");
        return (minX, minY, maxX, maxY);
    }

    private const int MinChanged = 3;
}
