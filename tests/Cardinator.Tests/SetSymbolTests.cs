using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;
using static Cardinator.Tests.TestHelpers;

namespace Cardinator.Tests;

/// <summary>Proves a custom set-symbol image is actually used by the renderer (a whole custom set can share one icon).</summary>
public class SetSymbolTests
{
    [Fact]
    public void CustomSetSymbol_ChangesTheRender()
        => RunSta(() =>
        {
            var tpl = new TemplateService().LoadAll().First();
            var card = new CardModel
            {
                Name = "Symbol Test", TypeLine = "Creature — Test", Power = "1", Toughness = "1",
                Rarity = "R", SetCode = "TST", TemplateName = tpl.Name,
            };
            var renderer = new CardRenderer(new SymbolService());
            var basePixels = Pixels(renderer.RenderToBitmap(card, tpl, supersample: 1));

            var symbolPath = Path.Combine(Path.GetTempPath(), "qa_sym_" + Guid.NewGuid().ToString("N") + ".png");
            SaveSolid(symbolPath, 64, Color.FromRgb(220, 20, 20));
            try
            {
                card.SetSymbolPath = symbolPath;
                var customPixels = Pixels(renderer.RenderToBitmap(card, tpl, supersample: 1));
                Assert.False(basePixels.SequenceEqual(customPixels));   // the custom icon replaced the generated one
            }
            finally { try { File.Delete(symbolPath); } catch { } }
        });

    private static void SaveSolid(string path, int size, Color c)
    {
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(c), null, new Rect(0, 0, size, size));
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(v);
        CardExporter.SavePng(rtb, path);
    }
}
