using System.Linq;
using Cardinator.Models;
using Cardinator.Services;
using static Cardinator.Tests.TestHelpers;

namespace Cardinator.Tests;

/// <summary>Render-diff tests for per-card draw features that aren't baked into the frame.</summary>
public class RendererDrawTests
{
    [Fact]
    public void Planeswalker_LoyaltyBadge_IsDrawn()
        => RunSta(() =>
        {
            var tpl = new TemplateService().LoadAll().First();
            var renderer = new CardRenderer(new SymbolService());

            var withLoyalty = new CardModel
            {
                Name = "Walker", ManaCost = "{2}{U}{U}", TypeLine = "Legendary Planeswalker — Walker",
                RulesText = "+1: Draw.\n-3: Bounce.", Loyalty = "4", TemplateName = tpl.Name,
            };
            var noLoyalty = withLoyalty.Clone();
            noLoyalty.Loyalty = "";
            noLoyalty.TypeLine = "Legendary Enchantment";   // avoid planeswalker layout so no badge

            var a = Pixels(renderer.RenderToBitmap(withLoyalty, tpl, supersample: 1));
            var b = Pixels(renderer.RenderToBitmap(noLoyalty, tpl, supersample: 1));
            Assert.False(a.SequenceEqual(b));   // the loyalty shield changes the render
        });
}
