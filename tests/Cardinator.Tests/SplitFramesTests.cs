using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// 1.6.9: a split or aftermath card's other half with a frame of its own (<i>Wear</i> on a red frame, <i>Tear</i>
/// on a white one), the Fuse bar in each half's colour, and plain rules text keeping above a frame's medallion.
/// </summary>
public class SplitFramesTests
{
    private const string Fuse = "Fuse (You may cast one or both halves of this card from your hand.)";

    private static CardModel WearTear(string template, string halfFrame = "") => new()
    {
        Name = "Wear", ManaCost = "{1}{R}", TypeLine = "Instant", RulesText = "Destroy target artifact.\n" + Fuse,
        TemplateName = template, HalfLayout = "split", HalfTemplateName = halfFrame,
        OtherHalf = new CardModel { Name = "Tear", ManaCost = "{W}", TypeLine = "Instant", RulesText = "Destroy target enchantment.\n" + Fuse },
    };

    private static CardModel DestinedLead(string template, string halfFrame = "") => new()
    {
        Name = "Destined", ManaCost = "{1}{B}", TypeLine = "Instant",
        RulesText = "Target creature gets +1/+0 and gains indestructible until end of turn.",
        TemplateName = template, HalfLayout = "split", HalfTemplateName = halfFrame,
        OtherHalf = new CardModel
        {
            Name = "Lead", ManaCost = "{3}{G}", TypeLine = "Sorcery",
            RulesText = "Aftermath (Cast this spell only from your graveyard. Then exile it.)\nAll creatures able to block target creature this turn do so.",
        },
    };

    private static Rect OnCard(CardRenderer.SplitPart p)
        => Rect.Transform(new Rect(0, 0, p.Width, p.Height), p.ToCard);

    [Fact]
    public void TheOtherHalf_IsDrawnWithItsOwnFrame_AndOnlyThatHalfChanges() => TestHelpers.RunSta(() =>
    {
        var all = new TemplateService().LoadAll();
        var red = all.First(t => t.Name == "Crimson Red");
        var g = CardRenderer.SplitGeometry(WearTear(red.Name, "Parchment"), red);
        Assert.Equal("Crimson Red", g.First.Template.Name);
        Assert.Equal("Parchment", g.Other.Template.Name);

        var r = new CardRenderer(new SymbolService());
        var same = TestHelpers.Pixels(r.RenderToBitmap(WearTear(red.Name), red));
        var own = r.RenderToBitmap(WearTear(red.Name, "Parchment"), red);
        var px = TestHelpers.Pixels(own);
        int w = own.PixelWidth, h = own.PixelHeight, changedTop = 0, changedBottom = 0;
        var tear = OnCard(g.Other);
        for (int y = 0; y < h; y += 2)
            for (int x = 0; x < w; x += 2)
            {
                int i = (y * w + x) * 4;
                if (Math.Abs(px[i] - same[i]) + Math.Abs(px[i + 1] - same[i + 1]) + Math.Abs(px[i + 2] - same[i + 2]) < 30) continue;
                if (y < tear.Bottom + 2) changedTop++; else changedBottom++;
            }
        Assert.True(changedTop > 5000, $"the other half barely changed ({changedTop})");
        Assert.True(changedBottom < 200, $"the first half or the credits changed too ({changedBottom})");
    });

    [Fact]
    public void ABlankOrMissingHalfFrame_UsesTheCardsFrame_AndChecksSayWhenItIsMissing() => TestHelpers.RunSta(() =>
    {
        var all = new TemplateService().LoadAll();
        var red = all.First(t => t.Name == "Crimson Red");
        Assert.Same(red, CardRenderer.SplitGeometry(WearTear(red.Name, ""), red).Other.Template);
        Assert.Same(red, CardRenderer.SplitGeometry(WearTear(red.Name, "No Such Frame"), red).Other.Template);

        var installed = all.Select(t => t.Name).ToList();
        Assert.Contains(CardValidator.Validate(WearTear(red.Name, "No Such Frame"), red.Spec, null, installed),
            i => i.Code == "half-frame-missing" && i.Severity == IssueSeverity.Warning);
        Assert.DoesNotContain(CardValidator.Validate(WearTear(red.Name, "Parchment"), red.Spec, null, installed),
            i => i.Code == "half-frame-missing");
        // A half frame left on a card that is no longer split is ignored, not reported.
        var notSplit = WearTear(red.Name, "No Such Frame"); notSplit.OtherHalf = null;
        Assert.DoesNotContain(CardValidator.Validate(notSplit, red.Spec, null, installed), i => i.Code == "half-frame-missing");
    });

    [Fact]
    public void Aftermath_TheLowerHalfTakesItsOwnFrame() => TestHelpers.RunSta(() =>
    {
        var all = new TemplateService().LoadAll();
        var steel = all.First(t => t.Name == "Alchemist's Steel");
        var g = CardRenderer.SplitGeometry(DestinedLead(steel.Name, "Arcane Parchment"), steel);
        Assert.True(g.Aftermath);
        Assert.Equal("Arcane Parchment", g.Other.Template.Name);
        var card = new Rect(0, 0, steel.Spec.CanvasWidth, steel.Spec.CanvasHeight + 0.001);
        Assert.True(card.Contains(OnCard(g.Other)), "the lower half leaves the card");
        Assert.True(OnCard(g.First).Bottom <= OnCard(g.Other).Top + 0.001, "the halves overlap");
    });

    [Fact]
    public void AHalfFrameOfAnotherShape_FitsItsOwnSlot_WithoutOverlapping() => TestHelpers.RunSta(() =>
    {
        var root = Directory.CreateTempSubdirectory("half-frame-shape").FullName;
        try
        {
            var red = new TemplateService().LoadAll().First(t => t.Name == "Crimson Red");
            var dir = Directory.CreateDirectory(Path.Combine(root, "narrow")).FullName;
            var spec = red.Spec.Clone();
            spec.Name = "Narrow Test Frame"; spec.CanvasWidth = 600; spec.CanvasHeight = 1000;
            File.WriteAllText(Path.Combine(dir, "template.json"), JsonSerializer.Serialize(spec));
            File.Copy(red.FramePath, Path.Combine(dir, "frame.png"));
            Assert.Single(new TemplateService().LoadFrom(root));

            var g = CardRenderer.SplitGeometry(WearTear(red.Name, "Narrow Test Frame"), red);
            Assert.Equal("Narrow Test Frame", g.Other.Template.Name);
            Rect a = OnCard(g.First), b = OnCard(g.Other);
            Assert.True(b.Bottom < a.Top, "the halves overlap");
            Assert.True(new Rect(0, 0, red.Spec.CanvasWidth, red.Spec.CanvasHeight + 0.001).Contains(b), $"{b} leaves the card");
            Assert.Equal(1000.0 / 600.0, b.Width / b.Height, 3);   // its own shape, lying on its side
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    });

    [Fact]
    public void TheHalfFrame_IsSaved_CopiedAndImported_AndOldCardsLoadWithout()
    {
        var path = Path.Combine(Path.GetTempPath(), $"halfframe-{Guid.NewGuid():N}.json");
        try
        {
            WearTear("Crimson Red", "Parchment").Save(path);
            Assert.Equal("Parchment", CardModel.Load(path).HalfTemplateName);
            File.WriteAllText(path, """{ "name": "Fire", "halfLayout": "split", "otherHalf": { "name": "Ice" } }""");
            var old = CardModel.Load(path);
            Assert.True(old.IsSplit);
            Assert.Equal("", old.HalfTemplateName);   // a set from before 1.6.9: both halves on the card's frame
        }
        finally { File.Delete(path); }

        Assert.Equal("Parchment", WearTear("Crimson Red", "Parchment").Clone().HalfTemplateName);

        var csv = "name,mana,type,rules,split_name,split_cost,split_rules,split_frame\n"
                + "Fire,{1}{R},Instant,Fire deals 2 damage.,Ice,{1}{U},Tap target permanent.,Ocean Blue\n";
        var card = ImportService.Parse(csv, null, "").Single().Card;
        Assert.True(card.IsSplit);
        Assert.Equal("Ocean Blue", card.HalfTemplateName);
    }

    [Fact]
    public void TheHalfFrame_TravelsWithTheSet_AndCountsAsInUse()
    {
        var cards = new[] { WearTear("Crimson Red", "Parchment"), new CardModel { TemplateName = "Ocean Blue" } };
        Assert.Equal(new[] { "Crimson Red", "Parchment" }, TemplateService.FramesUsed(cards[0]));
        Assert.Equal(1, TemplateService.CountReferencing(cards, "Parchment"));
        Assert.Equal(1, TemplateService.CountReferencing(cards, "Ocean Blue"));
    }

    [Fact]
    public void TheFuseBar_IsEachHalfsColour_AtItsEnd() => TestHelpers.RunSta(() =>
    {
        var red = new TemplateService().LoadAll().First(t => t.Name == "Crimson Red");
        var card = WearTear(red.Name);
        var g = CardRenderer.SplitGeometry(card, red);
        var bmp = new CardRenderer(new SymbolService()).RenderToBitmap(card, red);
        var px = TestHelpers.Pixels(bmp);
        double k = bmp.PixelWidth / (double)red.Spec.CanvasWidth;
        var toCard = CardRenderer.SplitLayout.ReadingToCard(red.Spec.CanvasHeight);
        (int r, int g, int b) At(double along)
        {
            // Near the bar's edge, clear of the text running down its middle.
            var p = toCard.Transform(new Point(g.Bar.X + g.Bar.Width * along, g.Bar.Y + g.Bar.Height * 0.15));
            int i = ((int)(p.Y * k) * bmp.PixelWidth + (int)(p.X * k)) * 4;
            return (px[i + 2], px[i + 1], px[i]);
        }
        var wear = At(0.1); var tear = At(0.9);
        Assert.True(wear.r - wear.b > 40, $"the end under Wear isn't red: {wear}");
        Assert.True(Math.Abs(tear.r - tear.g) < 25 && tear.r > 200, $"the end under Tear isn't white/cream: {tear}");

        var mono = WearTear(red.Name); mono.OtherHalf!.ManaCost = "{R}";
        Assert.IsType<SolidColorBrush>(CardRenderer.SplitBarFill(CardRenderer.SplitGeometry(mono, red)));
    });

    [Fact]
    public void LongRulesText_StaysAboveTheMedallion_ShortTextIsUnchanged() => TestHelpers.RunSta(() =>
    {
        var all = new TemplateService().LoadAll();
        var steel = all.First(t => t.Name == "Alchemist's Steel");
        var spec = steel.Spec.WithSubBorderApplied();
        var medallion = CardRenderer.BottomOrnament(steel, spec)!.Value;
        var r = new CardRenderer(new SymbolService());
        CardModel Card(string rules) => new() { Name = "Long", TypeLine = "Sorcery", RulesText = rules, TemplateName = steel.Name };
        var blank = TestHelpers.Pixels(r.RenderToBitmap(Card(""), steel));
        var longText = string.Join("\n", Enumerable.Repeat("Draw a card, then discard a card. Scry 2. You gain 3 life.", 6));
        var bmp = r.RenderToBitmap(Card(longText), steel);
        var px = TestHelpers.Pixels(bmp);
        int w = bmp.PixelWidth, inside = 0, elsewhere = 0;
        for (int y = 0; y < bmp.PixelHeight; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (Math.Abs(px[i] - blank[i]) + Math.Abs(px[i + 1] - blank[i + 1]) + Math.Abs(px[i + 2] - blank[i + 2]) < 40) continue;
                if (medallion.Contains(new Point(x, y))) inside++; else elsewhere++;
            }
        Assert.True(elsewhere > 2000, "the rules text didn't draw");
        Assert.Equal(0, inside);
    });
}
