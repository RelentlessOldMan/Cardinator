using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// 1.6.34, the graphics review: every layout on every frame checked by <see cref="LayoutAudit"/> (text cut off, off the
/// card, on other text or a symbol, under the P/T box, outside its box, or on one of the frame's drawn lines). What it
/// found: the picture frames' credit line within a pixel of their bottom border; Crimson Red's footer and Ironwrought
/// Showcase's P/T in light ink on a light ground; an adventure's pages and a leveler's bands shrunk to a squint on the
/// frames whose art fills the card.
/// </summary>
public class GraphicsReviewTests
{
    private static Template Frame(string name) => new TemplateService().LoadAll().First(t => t.Name == name);

    /// <summary>Every layout case and stress card on five frames that between them cover the styles: framed, full art,
    /// borderless with panels, a composable frame and a picture frame with a medallion.</summary>
    public static IEnumerable<object[]> AuditFrames()
    {
        foreach (var f in new[] { "Gold Multicolor", "Cinematic", "Showcase", "Ironwrought Showcase", "Sealed Gate" })
            yield return new object[] { f };
    }

    [Theory]
    [MemberData(nameof(AuditFrames))]
    public void EveryLayout_PutsItsTextWhereItBelongs(string frame) => TestHelpers.RunSta(() =>
    {
        var art = Path.Combine(Path.GetTempPath(), $"cardinator-audit-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(art, TestHelpers.PngBytes(64, 48));
        try
        {
            var tpl = Frame(frame);
            var renderer = new CardRenderer(new SymbolService(drawnOnly: true));
            var problems = new List<string>();
            foreach (var (card, _, label) in SelfTest.QaLayoutCards(art).Concat(SelfTest.QaStressCards(art)))
            {
                card.TemplateName = tpl.Name;
                var used = TemplateService.ResolveFor(card, tpl);
                foreach (var f in LayoutAudit.Inspect(renderer.DrawToGroup(card, tpl), card, used.Spec, used.FrameImage))
                    problems.Add($"{label}: {f}");
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }
        finally { try { File.Delete(art); } catch { } }
    });

    [Fact]
    public void TheAudit_FindsAFooterOnTheTextBoxBorder() => TestHelpers.RunSta(() =>
    {
        // The reported case: a frame drawn for one layout, the footer placed for another, right on the box's edge.
        var tpl = Frame("Gold Multicolor");
        var spec = tpl.Spec.Clone();
        spec.CreditBar = new Region { X = spec.CreditBar.X, Y = spec.TextBox.Y + spec.TextBox.H - 8, W = spec.CreditBar.W, H = spec.CreditBar.H };
        var moved = new Template { Name = tpl.Name, Spec = spec, FramePath = tpl.FramePath, FrameImage = tpl.FrameImage };
        var card = new CardModel { Name = "Footer Probe", TypeLine = "Instant", RulesText = "Draw a card.", SetCode = "FTR", CollectorNumber = "12", Artist = "Someone", TemplateName = tpl.Name };
        var findings = LayoutAudit.Inspect(new CardRenderer(new SymbolService(drawnOnly: true)).DrawToGroup(card, moved), card, spec, tpl.FrameImage);
        Assert.Contains(findings, f => f.Kind == "on a frame line");
    });

    [Fact]
    public void TheFooterInk_ReadsOnWhatIsReallyUnderIt() => TestHelpers.RunSta(() =>
    {
        // Crimson Red's frame colour is a dark red, but its footer band is drawn pale pink: dark ink, not white.
        var tpl = Frame("Crimson Red");
        var bar = new Rect(tpl.Spec.CreditBar.X, tpl.Spec.CreditBar.Y, tpl.Spec.CreditBar.W, tpl.Spec.CreditBar.H);
        Assert.True(CardRenderer.FrameLuminanceUnder(tpl.FrameImage as BitmapSource, bar, tpl.Spec) > 120);

        var card = new CardModel { Name = "Ink", TypeLine = "Instant", SetCode = "INK", CollectorNumber = "1", Artist = "Someone", TemplateName = tpl.Name };
        var footerBrushes = Brushes(new CardRenderer(new SymbolService(drawnOnly: true)).DrawToGroup(card, tpl), "Someone");
        Assert.NotEmpty(footerBrushes);
        Assert.All(footerBrushes, c => Assert.True(c.R < 100 && c.G < 100 && c.B < 100, $"footer ink {c}"));
    });

    [Fact]
    public void AComposableFramesPt_UsesInkThatReadsOnItsLightCentre() => TestHelpers.RunSta(() =>
    {
        var tpl = Frame("Ironwrought Showcase");
        var card = new CardModel { Name = "Hollow", TypeLine = "Creature — Golem", Power = "5", Toughness = "5", TemplateName = tpl.Name };
        var ink = Brushes(new CardRenderer(new SymbolService(drawnOnly: true)).DrawToGroup(card, tpl), "5/5");
        Assert.NotEmpty(ink);
        Assert.All(ink, c => Assert.True(c.R < 100 && c.G < 100 && c.B < 100, $"P/T ink {c}"));
    });

    [Theory]
    [InlineData("Cinematic")]
    [InlineData("Full Art")]
    [InlineData("Showcase")]
    [InlineData("Ironwrought Showcase")]
    public void AdventuresAndLevelers_OnFramesWhoseArtFillsTheCard_GetRoomInsteadOfShrinking(string name) => TestHelpers.RunSta(() =>
    {
        var frame = Frame(name);
        var leveler = new CardModel
        {
            Name = "Leveler", ManaCost = "{W}", TypeLine = "Creature — Human Knight", Power = "1", Toughness = "1",
            RulesText = "Level up {W} ({W}: Put a level counter on this. Level up only as a sorcery.)\nLEVEL 2-6\n3/3\nFirst strike\nLEVEL 7+\n4/4\nDouble strike",
        };
        var giant = new CardModel
        {
            Name = "Giant", ManaCost = "{2}{R}", TypeLine = "Creature — Giant", Power = "4", Toughness = "3", Layout = "adventure",
            RulesText = "Whenever this creature becomes the target of a spell, it deals 2 damage to that spell's controller.",
            AdventureName = "Stomp", AdventureCost = "{1}{R}", AdventureType = "Instant — Adventure",
            AdventureText = "Damage can't be prevented this turn. Stomp deals 2 damage to any target.",
        };
        foreach (var card in new[] { leveler, giant })
        {
            Assert.True(CardRenderer.FittedSize(card, frame.Spec) < frame.Spec.RulesFont.Size * TemplateService.TallReadable,
                $"{card.Name} already fit on {name} (the test no longer covers anything)");
            var used = TemplateService.ResolveFor(card, frame);
            Assert.True(used.Spec.TextBox.H > frame.Spec.TextBox.H, $"{card.Name}'s box didn't grow on {name}");
            Assert.True(CardRenderer.FittedSize(card, used.Spec) >= frame.Spec.RulesFont.Size * 0.8, $"{card.Name} still squints on {name}");
            Assert.Equal(used.Spec.TextBox.H, TemplateService.ResolveFor(card, used).Spec.TextBox.H, 3);   // resolving again changes nothing
        }
    });

    [Fact]
    public void ThePictureFrames_KeepTheirCreditLineClearOfTheBottomBorder()
    {
        // Their bottom border is drawn at y≈1008 (708 sideways): the credit box ends at least 6px above it (it was 4, and 2 sideways).
        foreach (var dir in Directory.GetDirectories(Path.Combine(RepoRoot(), "src", "Cardinator", "Assets", "templates")))
            foreach (var json in Directory.GetFiles(dir, "template.json", SearchOption.AllDirectories))
            {
                var spec = TemplateSpec.Load(json);
                double border = spec.IsLandscape ? 708 : 1008;
                Assert.True(spec.CreditBar.Y + spec.CreditBar.H <= border - 6, $"{json}: credit box ends at {spec.CreditBar.Y + spec.CreditBar.H}");
            }
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "src", "Cardinator", "Cardinator.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("repo root");
    }

    /// <summary>The colours of the glyph runs whose text contains <paramref name="contains"/>.</summary>
    private static List<Color> Brushes(Drawing d, string contains)
    {
        var found = new List<Color>();
        void Walk(Drawing x)
        {
            if (x is DrawingGroup g) foreach (var c in g.Children) Walk(c);
            else if (x is GlyphRunDrawing gr && gr.GlyphRun?.Characters != null && new string(gr.GlyphRun.Characters.ToArray()).Contains(contains)
                     && gr.ForegroundBrush is SolidColorBrush b && b.Color.A > 200)
                found.Add(b.Color);
        }
        Walk(d);
        return found;
    }
}
