using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>Tokens and emblems (1.6.10): told apart by their type line, drawn with the frame's token layout —
/// tall art, a text box that fits the text (none on a vanilla token), the name centred — on drawn frames and,
/// rearranged, on picture frames.</summary>
public class TokenTests
{
    private static CardModel Token(string type = "Token Creature — Soldier", string rules = "", string frame = "Crimson Red") => new()
    {
        Name = "Soldier", TypeLine = type, RulesText = rules, Power = "1", Toughness = "1", TemplateName = frame,
    };

    [Theory]
    [InlineData("Token Creature — Soldier", true)]
    [InlineData("Token Legendary Creature — Goblin Shaman", true)]
    [InlineData("Token Artifact — Treasure", true)]
    [InlineData("Emblem — Elspeth", true)]
    [InlineData("token creature — spirit", true)]
    [InlineData("Creature — Soldier", false)]
    [InlineData("Creature — Tokenmaker", false)]
    [InlineData("Artifact — Token", false)]   // only the types before the dash count
    [InlineData("", false)]
    public void ATokenOrEmblem_IsToldApartByItsTypes(string type, bool token)
        => Assert.Equal(token, new CardModel { TypeLine = type }.IsToken);

    [Fact]
    public void TheTokenLayout_GrowsTheArt_AndShortensTheTextBox_KeepingTheRestInPlace()
    {
        var spec = new TemplateSpec();
        var t = spec.ToToken(2);
        Assert.True(t.IsTokenLayout);
        double shift = t.TypeBar.Y - spec.TypeBar.Y;
        Assert.True(shift > 50, $"the type line only moved {shift}");
        Assert.Equal(spec.ArtWindow.H + shift, t.ArtWindow.H, 3);
        Assert.Equal(spec.ArtWindow.Y, t.ArtWindow.Y, 3);
        Assert.Equal(spec.TextBox.Bottom, t.TextBox.Bottom, 3);   // the box keeps its bottom edge
        Assert.True(t.TextBox.H >= 2 * 18 + 3 * spec.RulesFont.Size * 1.2, "fewer than 3 lines of room");
        Assert.Equal(spec.TitleBar.Y, t.TitleBar.Y, 3);
        Assert.Equal(spec.PtBox.Y, t.PtBox.Y, 3);
        Assert.Equal(spec.CreditBar.Y, t.CreditBar.Y, 3);
        Assert.Same(t, t.ToToken(2));   // already a token layout

        // More text, a bigger box (but never more than the frame's own).
        Assert.True(spec.ToToken(6).TextBox.H > t.TextBox.H);
        Assert.Equal(spec.TextBox.H, spec.ToToken(40).TextBox.H, 3);
    }

    [Fact]
    public void AVanillaToken_HasNoTextBox_ItsTypeLineSittingOnThePtBox()
    {
        var spec = new TemplateSpec { FooterPlacement = "border" };   // would otherwise stretch the box down
        var t = spec.ToToken(0);
        Assert.Equal(0, t.TextBox.H);
        Assert.Equal(0, t.EffectiveTextBox.H);
        Assert.Equal(spec.PtBox.Y + 4, t.TypeBar.Bottom, 3);
        Assert.Equal(t.TypeBar.Y - (spec.TypeBar.Y - spec.ArtWindow.Bottom), t.ArtWindow.Bottom, 3);
    }

    [Fact]
    public void AVanillaTokensPtBox_SitsUnderItsTypeLine_NotOverIt() => TestHelpers.RunSta(() =>
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var ptRect = typeof(CardRenderer).GetMethod("PtRect", flags)!;
        foreach (var t in new TemplateService().LoadAll().Where(t => !t.Spec.CustomFrame && !t.Spec.FullArt))
        {
            var spec = TemplateService.ResolveFor(Token(frame: t.Name), t).Spec.WithSubBorderApplied();
            var pt = (System.Windows.Rect)ptRect.Invoke(null, new object[] { spec })!;
            Assert.True(pt.Top >= spec.TypeBar.Bottom - 4.5, $"{t.Name}: the P/T box covers the type line ({pt.Top} < {spec.TypeBar.Bottom})");
        }
    });

    [Fact]
    public void TheTextBox_IsSizedToTheTokensText()
    {
        var spec = new TemplateSpec();
        Assert.Equal(0, TemplateService.TokenTextLines(Token(), spec));
        int shortText = TemplateService.TokenTextLines(Token(rules: "Flying"), spec);
        int longText = TemplateService.TokenTextLines(Token(rules: "Flying, deathtouch\nWhenever this token deals combat damage to a player, that player discards a card and you create a 1/1 green Insect creature token.\nSacrifice this token: You gain 2 life."), spec);
        Assert.InRange(shortText, 1, 2);
        Assert.True(longText >= 5, $"long text counted as {longText} lines");
        Assert.True(TemplateService.TokenTextLines(new CardModel { TypeLine = "Emblem — X", FlavorText = "Only flavor." }, spec) > 0);
    }

    [Fact]
    public void ATokenRendersWithItsFramesTokenLayout_OthersDont() => TestHelpers.RunSta(() =>
    {
        var red = new TemplateService().LoadAll().First(t => t.Name == "Crimson Red");
        var vanilla = TemplateService.ResolveFor(Token(), red);
        var withText = TemplateService.ResolveFor(Token(rules: "Flying"), red);
        Assert.True(vanilla.Spec.IsTokenLayout && withText.Spec.IsTokenLayout);
        Assert.Equal(0, vanilla.Spec.TextBox.H);
        Assert.True(withText.Spec.TextBox.H > 0);
        Assert.Equal(red.Name, vanilla.Name);   // the card's frame name still matches
        Assert.Same(vanilla, TemplateService.ResolveFor(Token(), vanilla));   // idempotent
        Assert.Same(red, TemplateService.ResolveFor(Token(type: "Creature — Soldier"), red));

        var renderer = new CardRenderer(new SymbolService());
        var asToken = renderer.RenderToBitmap(Token(rules: "Flying"), red);
        var asCard = renderer.RenderToBitmap(Token(type: "Creature — Soldier", rules: "Flying"), red);
        Assert.True(TestHelpers.HasContent(asToken));
        Assert.False(Pixels(asToken).SequenceEqual(Pixels(asCard)), "the token drew like a normal card");
    });

    [Fact]
    public void ATokensName_IsCentred_WhenItHasNoCost() => TestHelpers.RunSta(() =>
    {
        var red = new TemplateService().LoadAll().First(t => t.Name == "Crimson Red");
        var renderer = new CardRenderer(new SymbolService());
        var bar = red.Spec.TitleBar;
        (double left, double right) Ink(CardModel c)
        {
            var px = Pixels(renderer.RenderToBitmap(c, red));
            int w = red.Spec.CanvasWidth, y0 = (int)(bar.Y + bar.H * 0.3), y1 = (int)(bar.Y + bar.H * 0.7);
            var bg = Pixel(px, w, (int)bar.X + 6, y0);
            double l = double.MaxValue, r = 0;
            for (int y = y0; y < y1; y++)
                for (int x = (int)bar.X + 10; x < bar.Right - 10; x++)
                {
                    var p = Pixel(px, w, x, y);
                    if (Math.Abs(p.r - bg.r) + Math.Abs(p.g - bg.g) + Math.Abs(p.b - bg.b) > 120) { l = Math.Min(l, x); r = Math.Max(r, x); }
                }
            return (l, r);
        }
        var token = Ink(Token());
        Assert.InRange((token.left + token.right) / 2, bar.X + bar.W / 2 - 6, bar.X + bar.W / 2 + 6);
        var card = Ink(Token(type: "Creature — Soldier"));
        Assert.True(card.left < token.left - 50, "a normal card's name should stay on the left");
    });

    [Fact]
    public void APictureFrame_IsRearrangedIntoATokenVersion_KeepingItsOwnPixels() => TestHelpers.RunSta(() =>
    {
        var steel = new TemplateService().LoadAll().First(t => t.Name == "Alchemist's Steel");
        Assert.True(steel.Spec.CustomFrame);
        var token = TemplateService.ResolveFor(Token(rules: "Flying", frame: steel.Name), steel);
        Assert.True(token.Spec.IsTokenLayout);
        Assert.NotSame(steel.FrameImage, token.FrameImage);

        var a = (BitmapSource)steel.FrameImage; var b = (BitmapSource)token.FrameImage;
        Assert.Equal(a.PixelWidth, b.PixelWidth);
        Assert.Equal(a.PixelHeight, b.PixelHeight);
        byte[] pa = Pixels(a), pb = Pixels(b);
        int stride = a.PixelWidth * 4;
        double k = a.PixelHeight / (double)steel.Spec.CanvasHeight;
        int Rows(double y) => (int)(y * k);
        // Above the art's middle (the title) and below the text box's cut (its bottom edge, the medallion, the
        // card's bottom border): the frame's own pixels, untouched.
        int top = Rows(steel.Spec.ArtWindow.Y + steel.Spec.ArtWindow.H * 0.2) - 1;
        Assert.True(pa.AsSpan(0, top * stride).SequenceEqual(pb.AsSpan(0, top * stride)), "the top of the frame changed");
        int from = Rows(steel.Spec.TextBox.Bottom - 10);
        Assert.True(pa.AsSpan(from * stride).SequenceEqual(pb.AsSpan(from * stride)), "the bottom of the frame changed");

        // Its medallion is the frame's own, where it was, and the text box leaves room above it for 3 lines.
        var medallion = CardRenderer.BottomOrnament(steel, steel.Spec);
        Assert.NotNull(medallion);
        Assert.Equal(medallion, CardRenderer.BottomOrnament(token, token.Spec));
        Assert.True(medallion!.Value.Top - 4 - (token.Spec.TextBox.Y + 18) >= 3 * steel.Spec.RulesFont.Size * 1.2, "no room above the medallion");

        // A vanilla token on a picture frame keeps a small box: its box is part of the picture.
        Assert.True(TemplateService.ResolveFor(Token(frame: steel.Name), steel).Spec.TextBox.H > 0);
        Assert.DoesNotContain(CardValidator.Validate(Token(frame: steel.Name), token.Spec), i => i.Code == "no-token-frame");
    });

    [Fact]
    public void AddToken_StartsAVanillaSoldier()
    {
        var t = SampleCards.BlankToken("Crimson Red");
        Assert.True(t.IsToken);
        Assert.False(t.HasRulesOrFlavor);
        Assert.Equal("", t.ManaCost);
        Assert.Equal("1", t.Power);
    }

    [Fact]
    public void ATokenRow_ImportsFromCsv_AsAToken()
    {
        var cards = ImportService.Parse("name,type,power,toughness\nSoldier,Token Creature — Soldier,1,1\n", null, "Crimson Red");
        Assert.True(Assert.Single(cards).Card.IsToken);
    }

    private static byte[] Pixels(BitmapSource bmp)
    {
        var src = bmp.Format == PixelFormats.Bgra32 || bmp.Format == PixelFormats.Pbgra32 ? bmp : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        var px = new byte[src.PixelWidth * src.PixelHeight * 4];
        src.CopyPixels(px, src.PixelWidth * 4, 0);
        return px;
    }

    private static (int r, int g, int b) Pixel(byte[] px, int w, int x, int y)
    {
        int i = (y * w + x) * 4;
        return (px[i + 2], px[i + 1], px[i]);
    }
}
