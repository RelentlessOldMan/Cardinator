using System;
using System.Linq;
using System.Windows;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// 1.6.5 adventure cards in the modern storybook layout (<i>Bonecrusher Giant</i>): the text box opens like a
/// book, the adventure spell (name bar with cost, type, rules) on the left page and the creature's rules on the
/// right, both at one shared, readable rules size — the old sub-box squeezed the spell's rules to a few pixels.
/// </summary>
public class AdventureTests
{
    private static CardModel Giant(string template = "") => new()
    {
        Name = "Bonecrusher Giant", ManaCost = "{2}{R}", TypeLine = "Creature — Giant", Power = "4", Toughness = "3",
        RulesText = "Whenever this creature becomes the target of a spell, this creature deals 2 damage to that spell's controller.",
        AdventureName = "Stomp", AdventureCost = "{1}{R}", AdventureType = "Instant — Adventure",
        AdventureText = "Damage can't be prevented this turn. Stomp deals 2 damage to any target.",
        Layout = "adventure", TemplateName = template,
    };

    [Fact]
    public void Layout_PutsTheSpellOnTheLeftPage_AndTheCreatureOnTheRight_AtAReadableSize() => TestHelpers.RunSta(() =>
    {
        var tpl = new TemplateService().LoadAll().First(t => t.Name == "Gold Multicolor");
        var spec = tpl.Spec.WithSubBorderApplied();
        var (left, nameBar, typeRow, leftText, right, size) = new CardRenderer(new SymbolService()).AdventureLayout(Giant(tpl.Name), spec, null);
        var tb = new Rect(spec.EffectiveTextBox.X, spec.EffectiveTextBox.Y, spec.EffectiveTextBox.W, spec.EffectiveTextBox.H);

        Assert.True(tb.Contains(left) && tb.Contains(right), "a page leaves the text box");
        Assert.True(left.Right <= right.Left, "the pages overlap");
        Assert.InRange(left.Width / tb.Width, 0.45, 0.55);                  // the book opens in the middle
        Assert.True(left.Contains(nameBar) && left.Contains(typeRow) && left.Contains(leftText));
        Assert.True(nameBar.Bottom <= typeRow.Top + 0.5 && typeRow.Bottom <= leftText.Top + 0.5, "the left page's parts are out of order");
        Assert.Equal(spec.RulesFont.Size, size);                            // short rules: no shrinking at all
    });

    [Fact]
    public void LongRules_ShrinkBothPagesTogether_ButStayReadable() => TestHelpers.RunSta(() =>
    {
        var tpl = new TemplateService().LoadAll().First(t => t.Name == "Gold Multicolor");
        var card = Giant(tpl.Name);
        card.RulesText = string.Join("\n", Enumerable.Repeat("Whenever this creature attacks, it gets +1/+0 until end of turn.", 3));
        card.FlavorText = "The giant had never heard of a fair fight.";
        var (_, _, _, _, _, size) = new CardRenderer(new SymbolService()).AdventureLayout(card, tpl.Spec.WithSubBorderApplied(), null);
        Assert.True(size < tpl.Spec.RulesFont.Size, "long rules should shrink");
        Assert.True(size >= 9, $"rules shrank to {size}");
    });

    [Fact]
    public void Render_TheSpellChangesOnlyTheLeftPage_AndTheCreatureOnlyTheRight() => TestHelpers.RunSta(() =>
    {
        var tpl = new TemplateService().LoadAll().First(t => t.Name == "Gold Multicolor");
        var spec = tpl.Spec.WithSubBorderApplied();
        var r = new CardRenderer(new SymbolService());
        var basePx = TestHelpers.Pixels(r.RenderToBitmap(Giant(tpl.Name), tpl));
        double mid = spec.EffectiveTextBox.X + spec.EffectiveTextBox.W / 2;

        (int minX, int maxX) Changed(CardModel other)
        {
            var px = TestHelpers.Pixels(r.RenderToBitmap(other, tpl));
            int w = 750, minX = int.MaxValue, maxX = -1;
            for (int i = 0; i < px.Length; i += 4)
                if (Math.Abs(px[i] - basePx[i]) + Math.Abs(px[i + 1] - basePx[i + 1]) + Math.Abs(px[i + 2] - basePx[i + 2]) > 30)
                { int x = i / 4 % w; minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); }
            return (minX, maxX);
        }

        var spell = Giant(tpl.Name); spell.AdventureText = "Stomp deals 3 damage to each opponent.";
        var (_, spellMax) = Changed(spell);
        Assert.True(spellMax >= 0 && spellMax < mid, $"the spell's rules reached x={spellMax}");

        var creature = Giant(tpl.Name); creature.RulesText = "Trample";
        var (creatureMin, _) = Changed(creature);
        Assert.True(creatureMin > mid, $"the creature's rules reached x={creatureMin}");
    });

    [Fact]
    public void EveryInstalledFrame_DrawsAnAdventure_WithoutPixelErrors() => TestHelpers.RunSta(() =>
    {
        var r = new CardRenderer(new SymbolService());
        foreach (var tpl in new TemplateService().LoadAll())
        {
            var card = Giant(tpl.Name);
            var resolved = TemplateService.ResolveFor(card, tpl);
            var issues = RenderInspector.Inspect(r.RenderToBitmap(card, resolved), card, resolved.Spec);
            Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
        }
    });
}
