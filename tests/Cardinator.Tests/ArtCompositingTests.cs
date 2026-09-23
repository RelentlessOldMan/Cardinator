using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// The core goal: real artwork fills the card's art window, with the frame border, text and
/// mana symbols composited over the top. These tests load an actual image as the card art and
/// inspect the rendered pixels to prove the layering is correct.
/// </summary>
public class ArtCompositingTests
{
    [Fact]
    public void Artwork_ShowsThroughArtWindow_WithFrameOverTheBorder()
        => RunSta(() =>
        {
            // Distinctive magenta art, larger than the window so cover-fit fills it.
            var artPath = CreateSolidPng(Color.FromRgb(255, 0, 255), 900, 700);
            try
            {
                var template = new TemplateService().LoadAll()[0];
                var spec = template.Spec;
                var card = new CardModel
                {
                    Name = "Full Art Test", ManaCost = "{2}{R}", TypeLine = "Creature — Test",
                    RulesText = "Trample", Power = "3", Toughness = "3",
                    TemplateName = template.Name, ArtPath = artPath,
                };

                var bmp = new CardRenderer(new SymbolService()).RenderToBitmap(card, template, supersample: 1);

                // (1) Art shows through: the centre of the art window is the magenta artwork.
                var center = Sample(bmp,
                    (int)(spec.ArtWindow.X + spec.ArtWindow.W / 2),
                    (int)(spec.ArtWindow.Y + spec.ArtWindow.H / 2));
                Assert.True(IsMagenta(center), $"art window centre was {center}; expected magenta artwork");

                // (2) The frame is drawn OVER the top: the extreme card corner is not the art —
                //     art is clipped to its window and the border covers everything outside it.
                var corner = Sample(bmp, 3, 3);
                Assert.False(IsMagenta(corner), $"card corner was {corner}; the frame border should cover it");

                // (3) The type bar (below the art window) is frame/text, never the art colour.
                var typeBar = Sample(bmp,
                    (int)(spec.TypeBar.X + spec.TypeBar.W / 2),
                    (int)(spec.TypeBar.Y + spec.TypeBar.H / 2));
                Assert.False(IsMagenta(typeBar), $"type bar was {typeBar}; it should sit over the frame, not the art");
            }
            finally { File.Delete(artPath); }
        });

    [Fact]
    public void ArtScale_ChangesWhatFillsTheWindow_ButStaysClipped()
        => RunSta(() =>
        {
            var artPath = CreateSolidPng(Color.FromRgb(0, 200, 0), 900, 700);
            try
            {
                var template = new TemplateService().LoadAll()[0];
                var spec = template.Spec;
                var card = new CardModel { Name = "Zoom", TemplateName = template.Name, ArtPath = artPath, ArtScale = 2.5 };

                var bmp = new CardRenderer(new SymbolService()).RenderToBitmap(card, template, supersample: 1);

                // Zoomed-in solid art still fills the window centre...
                var center = Sample(bmp,
                    (int)(spec.ArtWindow.X + spec.ArtWindow.W / 2),
                    (int)(spec.ArtWindow.Y + spec.ArtWindow.H / 2));
                Assert.True(center.g > 150 && center.r < 120 && center.b < 120, $"expected green art, got {center}");

                // ...and is still clipped — the corner outside the window is not the art.
                var corner = Sample(bmp, 3, 3);
                Assert.False(corner.g > 150 && corner.r < 120 && corner.b < 120, $"art leaked past its window: {corner}");
            }
            finally { File.Delete(artPath); }
        });

    [Fact]
    public void MissingArtFile_DoesNotCrash_AndStillRendersFrame()
        => RunSta(() =>
        {
            var template = new TemplateService().LoadAll()[0];
            var card = new CardModel
            {
                Name = "Broken Art", TemplateName = template.Name,
                ArtPath = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".png"),
            };

            var bmp = new CardRenderer(new SymbolService()).RenderToBitmap(card, template, supersample: 1);
            Assert.Equal(template.Spec.CanvasWidth, bmp.PixelWidth);
            Assert.True(HasContent(bmp), "frame should still render when the art file is missing");
        });

    // --- helpers ------------------------------------------------------------

    private static string CreateSolidPng(Color c, int w, int h)
    {
        int stride = w * 4;
        var px = new byte[h * stride];
        for (int i = 0; i < px.Length; i += 4) { px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = 255; }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        var path = Path.Combine(Path.GetTempPath(), $"art-{Guid.NewGuid():N}.png");
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
        return path;
    }

    private static (int r, int g, int b, int a) Sample(BitmapSource bmp, int x, int y)
    {
        var one = new CroppedBitmap(bmp, new Int32Rect(x, y, 1, 1));
        var px = new byte[4];
        one.CopyPixels(px, 4, 0);
        return (px[2], px[1], px[0], px[3]);   // Pbgra32 → B,G,R,A
    }

    private static bool IsMagenta((int r, int g, int b, int a) p) => p.r > 200 && p.b > 200 && p.g < 90 && p.a > 200;

    private static bool HasContent(BitmapSource bmp)
    {
        int stride = bmp.PixelWidth * 4;
        var pixels = new byte[bmp.PixelHeight * stride];
        bmp.CopyPixels(pixels, stride, 0);
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i + 3] > 10 && (pixels[i + 2] < 200 || pixels[i + 1] < 200 || pixels[i] < 200)) return true;
        return false;
    }

    private static void RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { captured = ex; } })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured != null) throw captured;
    }
}
