using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// 1.6.25: on a frame whose art fills the card (Cinematic, Full Art, Showcase, Ironwrought Showcase) the text box is
/// short, so a long card's text shrank to a squint. Now such a card gets the frame laid out with the type line and
/// text box raised into the art just far enough; a short card keeps the frame as it is, and other frames are never
/// changed. Each test fails with its fix taken out. In the STA-window collection because it builds frames.
/// </summary>
[Collection("STAWindows")]
public class TallTextTests
{
    private static Template Frame(string name) => new TemplateService().LoadAll().First(t => t.Name == name);

    private const string Atraxa = "Flying, vigilance, deathtouch, lifelink\nWhen Atraxa enters, reveal the top ten cards of your library. For each card type, you may put a card of that type from among the revealed cards into your hand. Put the rest on the bottom of your library in a random order. (Artifact, battle, creature, enchantment, instant, land, planeswalker, and sorcery are card types.)";

    private static CardModel Card(string rules, string frame) => new()
    {
        Name = "Card", TypeLine = "Legendary Creature — Angel", RulesText = rules, Power = "7", Toughness = "7", TemplateName = frame,
    };

    [Theory]
    [InlineData("Cinematic")]
    [InlineData("Full Art")]
    [InlineData("Showcase")]
    [InlineData("Ironwrought Showcase")]
    public void ALongCard_OnAFrameWhoseArtFillsTheCard_GetsATallerTextBox(string name) => TestHelpers.RunSta(() =>
    {
        var frame = Frame(name);
        var used = TemplateService.ResolveFor(Card(Atraxa, name), frame);
        Assert.True(used.Spec.TextBox.H > frame.Spec.TextBox.H, "the text box didn't grow");
        Assert.Equal(frame.Spec.TextBox.Bottom, used.Spec.TextBox.Bottom, 3);              // it grows upward only
        Assert.Equal(frame.Spec.TypeBar.Y - used.Spec.TypeBar.Y, used.Spec.TextBox.H - frame.Spec.TextBox.H, 3);
        Assert.True(used.Spec.TypeBar.Y >= frame.Spec.CanvasHeight * TemplateService.TallTypeLineTop - 0.5);
        Assert.Equal(frame.Spec.ArtWindow.H, used.Spec.ArtWindow.H, 3);                    // the art stays full-bleed
        Assert.NotEqual(frame.FramePath, used.FramePath);                                  // its own band, drawn for it
        // Resolving again changes nothing (every render path resolves).
        Assert.Equal(used.Spec.TextBox.H, TemplateService.ResolveFor(Card(Atraxa, name), used).Spec.TextBox.H, 3);
    });

    [Fact]
    public void AShortCard_KeepsTheFrameAsItIs() => TestHelpers.RunSta(() =>
    {
        var cinematic = Frame("Cinematic");
        Assert.Same(cinematic, TemplateService.ResolveFor(Card("Flying", "Cinematic"), cinematic));
    });

    [Fact]
    public void ACardThatOnlyJustOverflows_ShrinksALittle_AndKeepsTheFrame() => TestHelpers.RunSta(() =>
    {
        var cinematic = Frame("Cinematic");
        var card = new CardModel
        {
            Name = "Spell", TypeLine = "Instant", TemplateName = "Cinematic",
            RulesText = "Counter target spell unless its controller pays {3}. If that spell is countered this way, exile it instead of putting it into its owner's graveyard, then scry 2 and draw a card.",
        };
        Assert.Same(cinematic, TemplateService.ResolveFor(card, cinematic));
    });

    [Theory]
    [InlineData("Azure Modern")]        // a framed art window
    [InlineData("Alchemist's Steel")]   // a picture frame: its picture can't be redrawn
    public void OtherFrames_NeverChange(string name) => TestHelpers.RunSta(() =>
    {
        var frame = Frame(name);
        Assert.Same(frame, TemplateService.ResolveFor(Card(Atraxa, name), frame));
    });

    [Fact]
    public void ALongCard_ReadsLargerThanBefore_OnCinematic() => TestHelpers.RunSta(() =>
    {
        // The rules text's line height on the plain frame vs. the frame it now gets: the text is drawn larger.
        var cinematic = Frame("Cinematic");
        var card = Card(Atraxa, "Cinematic");
        double LineGap(Template t)
        {
            var r = new CardRenderer(new SymbolService());
            var box = new System.Windows.Rect(t.Spec.TextBox.X + 18, t.Spec.TextBox.Y + 18, t.Spec.TextBox.W - 36, t.Spec.TextBox.H - 36);
            for (double size = t.Spec.RulesFont.Size; size >= 11; size--)
            {
                var runs = r.InspectTextLayout(card.RulesText, box, t.Spec.RulesFont, size, size);
                if (runs.Max(p => p.Rect.Bottom) <= box.Bottom) return size;
            }
            return 11;
        }
        double before = LineGap(cinematic), after = LineGap(TemplateService.ResolveFor(card, cinematic));
        Assert.True(after >= before + 4, $"rules text fits at {after}pt on the taller box vs {before}pt before");
    });
}
