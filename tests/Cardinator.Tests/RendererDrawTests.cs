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
            // The SAME planeswalker with only its loyalty removed, so the shield is the one thing that can differ
            // (changing the type line too would differ everywhere and prove nothing about the shield).
            var noLoyalty = withLoyalty.Clone();
            noLoyalty.Loyalty = "";

            var bmpA = renderer.RenderToBitmap(withLoyalty, tpl, supersample: 1);
            var a = Pixels(bmpA);
            var b = Pixels(renderer.RenderToBitmap(noLoyalty, tpl, supersample: 1));
            var (_, shield) = renderer.InspectPlaneswalkerLayout(withLoyalty, tpl.Spec);
            double k = bmpA.PixelWidth / (double)tpl.Spec.CanvasWidth;
            int changedInShield = 0;
            for (int y = (int)(shield.Top * k); y < (int)(shield.Bottom * k); y++)
                for (int x = (int)(shield.Left * k); x < (int)(shield.Right * k); x++)
                {
                    int i = (y * bmpA.PixelWidth + x) * 4;
                    if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 60) changedInShield++;
                }
            int area = (int)(shield.Width * k * shield.Height * k);
            Assert.True(changedInShield > area / 10, $"only {changedInShield} of {area} pixels in the loyalty shield's place changed");
        });
}
