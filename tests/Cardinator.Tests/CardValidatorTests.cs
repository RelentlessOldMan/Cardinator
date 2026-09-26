using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;
using static Cardinator.Tests.TestHelpers;

namespace Cardinator.Tests;

/// <summary>Proves the automated validators actually catch the bug classes we used to find by eye —
/// missing/broken art, bad symbols, footer-over-panel, out-of-bounds regions, duplicate collectors,
/// and (via RenderInspector) a blank art window.</summary>
public class CardValidatorTests
{
    private static TemplateSpec Framed() => new()
    {
        Name = "T", CanvasWidth = 750, CanvasHeight = 1050, BorderThickness = 28,
        TitleBar = new() { X = 48, Y = 46, W = 654, H = 60 },
        ArtWindow = new() { X = 48, Y = 124, W = 654, H = 464 },
        TypeBar = new() { X = 48, Y = 598, W = 654, H = 56 },
        TextBox = new() { X = 48, Y = 662, W = 654, H = 296 },
        CreditBar = new() { X = 54, Y = 962, W = 460, H = 34 },
        PtBox = new() { X = 572, Y = 926, W = 126, H = 72 },
    };

    private static CardModel CleanCreature() => new()
    {
        Name = "Grizzly", ManaCost = "{1}{G}", TypeLine = "Creature — Bear",
        RulesText = "Vigilance\n{T}: Add {G}.", Power = "2", Toughness = "2",
        SetCode = "TST", CollectorNumber = "1", Rarity = "C",
    };

    [Fact]
    public void CleanCard_HasNoWarningsOrErrors()
    {
        var issues = CardValidator.Validate(CleanCreature(), Framed());
        Assert.DoesNotContain(issues, i => i.Severity is IssueSeverity.Warning or IssueSeverity.Error);
    }

    [Fact]
    public void UnknownSymbol_IsFlagged()
    {
        var c = CleanCreature();
        c.RulesText = "{Q9}: Do a thing.";
        var issues = CardValidator.Validate(c, Framed());
        Assert.Contains(issues, i => i.Code == "bad-symbol");
    }

    [Fact]
    public void KnownSymbols_AreNotFlagged()
    {
        var c = CleanCreature();
        c.ManaCost = "{2}{G/U}{U/P}";
        c.RulesText = "{T}, {X}: Add {C}.";
        var issues = CardValidator.Validate(c, Framed());
        Assert.DoesNotContain(issues, i => i.Code == "bad-symbol");
    }

    [Fact]
    public void MissingArtFile_IsError()
    {
        var c = CleanCreature();
        c.ArtPath = Path.Combine(Path.GetTempPath(), "definitely-not-here-" + System.Guid.NewGuid() + ".png");
        var issues = CardValidator.Validate(c, Framed());
        Assert.Contains(issues, i => i.Code == "art-missing" && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void FooterOverlappingPanel_IsFlagged()
    {
        var spec = Framed();
        spec.CreditBar = new() { X = 54, Y = 700, W = 460, H = 34 };   // inside the text box (662..958)
        var issues = CardValidator.Validate(CleanCreature(), spec);
        Assert.Contains(issues, i => i.Code == "footer-overlap");
    }

    [Fact]
    public void RegionUnderBorder_IsFlagged()
    {
        var spec = Framed();
        spec.TitleBar = new() { X = 4, Y = 46, W = 700, H = 60 };   // X=4 < border 28
        var issues = CardValidator.Validate(CleanCreature(), spec);
        Assert.Contains(issues, i => i.Code == "out-of-bounds");
    }

    [Fact]
    public void DuplicateCollectorNumbers_AreFlagged()
    {
        var a = CleanCreature(); a.CollectorNumber = "5";
        var b = CleanCreature(); b.Name = "Other"; b.CollectorNumber = "5";
        var issues = CardValidator.Validate(a, Framed(), new[] { a, b });
        Assert.Contains(issues, i => i.Code == "dup-collector");
    }

    [Fact]
    public void HalfPowerToughness_IsFlagged()
    {
        var c = CleanCreature(); c.Toughness = "";   // power set, toughness blank
        var issues = CardValidator.Validate(c, Framed());
        Assert.Contains(issues, i => i.Code == "half-pt");
    }

    [Fact]
    public void RenderInspector_BlankArtWindow_IsFlagged() => RunSta(() =>
    {
        var c = CleanCreature();
        c.ArtPath = Path.Combine(Path.GetTempPath(), "missing-" + System.Guid.NewGuid() + ".png");  // won't load → white window
        var tpl = new TemplateService().LoadAll().First();
        var bmp = new CardRenderer(new SymbolService()).RenderToBitmap(c, tpl, supersample: 1);
        var issues = RenderInspector.Inspect(bmp, c, tpl.Spec);
        Assert.Contains(issues, i => i.Code == "art-blank");
    });

    [Fact]
    public void RenderInspector_NormalCard_HasEvenBorder() => RunSta(() =>
    {
        var c = CleanCreature();
        var tpl = new TemplateService().LoadAll().First();
        var bmp = new CardRenderer(new SymbolService()).RenderToBitmap(c, tpl, supersample: 1);
        var issues = RenderInspector.Inspect(bmp, c, tpl.Spec);
        Assert.DoesNotContain(issues, i => i.Code == "border-thin");
    });

    [Fact]
    public void RenderInspector_MissingBorder_IsFlagged()
    {
        // An all-white card (no black rim at all) must trip the border-thin check on every side.
        int w = 750, h = 1050, stride = w * 4;
        var px = new byte[h * stride];
        for (int i = 0; i < px.Length; i++) px[i] = 255;
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        var issues = RenderInspector.Inspect(bmp, new CardModel { Name = "White" }, Framed());
        Assert.Contains(issues, i => i.Code == "border-thin" && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void NoName_IsFlagged()
    {
        var c = CleanCreature(); c.Name = "";
        Assert.Contains(CardValidator.Validate(c, Framed()), i => i.Code == "no-name");
    }

    [Fact]
    public void NoArt_IsInfo()
    {
        var issues = CardValidator.Validate(CleanCreature(), Framed());   // CleanCreature has no art
        Assert.Contains(issues, i => i.Code == "no-art" && i.Severity == IssueSeverity.Info);
    }

    [Fact]
    public void InvalidArtPath_IsError()
    {
        var c = CleanCreature(); c.ArtPath = "bad\0path.png";   // null char makes GetFullPath throw
        Assert.Contains(CardValidator.Validate(c, Framed()), i => i.Code is "art-badpath" or "art-missing" && i.Severity == IssueSeverity.Error);
    }
}
