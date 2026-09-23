using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cardinator.Services;

/// <summary>
/// Print-service helpers. Adds a bleed margin around a rendered card by replicating its edge
/// pixels outward (compositing over a background so transparent corners don't turn see-through),
/// which is what services like MakePlayingCards expect for safe cutting.
/// </summary>
public static class PrintExporter
{
    /// <summary>
    /// Returns the card padded by <paramref name="bleed"/> pixels on every side. The bleed area is
    /// the card's outermost pixels extended outward; transparent areas fall back to
    /// <paramref name="background"/> (default black, a common card-edge color).
    /// </summary>
    public static BitmapSource AddBleed(BitmapSource card, int bleed, Color? background = null)
    {
        if (bleed <= 0) return card;
        var bg = background ?? Colors.Black;

        var s = CardExporter.ToBgra32(card, out int sStride);
        int w = card.PixelWidth, h = card.PixelHeight;
        int nw = w + 2 * bleed, nh = h + 2 * bleed, dStride = nw * 4;
        var d = new byte[nh * dStride];

        for (int y = 0; y < nh; y++)
        {
            int sy = Math.Clamp(y - bleed, 0, h - 1);
            for (int x = 0; x < nw; x++)
            {
                int sx = Math.Clamp(x - bleed, 0, w - 1);
                int si = sy * sStride + sx * 4, di = y * dStride + x * 4;

                double a = s[si + 3] / 255.0;   // straight alpha; composite over bg -> opaque
                d[di] = (byte)(s[si] * a + bg.B * (1 - a));
                d[di + 1] = (byte)(s[si + 1] * a + bg.G * (1 - a));
                d[di + 2] = (byte)(s[si + 2] * a + bg.R * (1 - a));
                d[di + 3] = 255;
            }
        }

        var outBmp = BitmapSource.Create(nw, nh, card.DpiX, card.DpiY, PixelFormats.Bgra32, null, d, dStride);
        outBmp.Freeze();
        return outBmp;
    }
}
