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
/// 1.6.2 split cards (<i>Wear // Tear</i>, Duskmourn Rooms): one card whose two halves are small cards side
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Geometry_TwoEqualCardShapedHalves_SideBySide_InsideTheCard(bool fuse)
    {
        var spec = new TemplateSpec();
        var card = Split();
        if (!fuse) { card.RulesText = "Destroy target artifact."; card.OtherHalf!.RulesText = "Destroy target enchantment."; }
        var g = CardRenderer.SplitGeometry(card, spec);
        var reading = new Rect(0, 0, g.ReadingWidth, g.ReadingHeight);

        Assert.Equal(spec.CanvasHeight, g.ReadingWidth);     // the card turned sideways
        Assert.Equal(spec.CanvasWidth, g.ReadingHeight);
        Assert.True(reading.Contains(g.Left) && reading.Contains(g.Right));
        Assert.True(g.Left.Right < g.Right.Left, "the halves overlap");
        Assert.Equal(g.Left.Size, g.Right.Size);
        Assert.Equal(spec.CanvasWidth / (double)spec.CanvasHeight, g.Left.Width / g.Left.Height, 3);   // card-shaped
        Assert.True(g.Left.Left >= g.FooterStrip, "a half runs into the card's credits edge");
        Assert.True(g.Scale > 0.55, $"halves drawn too small ({g.Scale:0.00})");
        if (fuse)
        {
            Assert.False(g.Bar.IsEmpty);
            Assert.True(g.Bar.Top > g.Left.Bottom, "the Fuse bar overlaps the halves");
            Assert.True(g.Bar.Bottom <= g.ReadingHeight);
        }
        else Assert.True(g.Bar.IsEmpty);
    }

    [Fact]
    public void ReadingView_MapsOntoTheUprightCard_LeftEdgeBecomesTheBottom()
    {
        var m = CardRenderer.SplitLayout.ToCard(1050);
        Assert.Equal(new Point(0, 1050), m.Transform(new Point(0, 0)));      // reading top-left → card bottom-left
        Assert.Equal(new Point(750, 0), m.Transform(new Point(1050, 750)));  // reading bottom-right → card top-right
    }

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
        {
            var card = Split(tpl.Name);
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

    // --- helpers --------------------------------------------------------------------------------------

    private static string SolidPng(string path, Color color)
    {
        var rt = new RenderTargetBitmap(60, 60, 96, 96, PixelFormats.Pbgra32);
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, 60, 60));
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
}
