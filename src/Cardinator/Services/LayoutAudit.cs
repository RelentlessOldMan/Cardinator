using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>
/// Checks where a rendered card's text actually landed, from the drawing itself rather than its pixels: every run of
/// glyphs, with the transforms and clips it was drawn under. It finds text that is cut off by a clip, runs off the
/// card, lands on other text or a symbol, slides under the P/T box, or (on a plain card) sits outside every box the
/// frame has for text. Used by <c>--qa … full</c> and the layout-audit test, so a layout change that pushes text
/// somewhere wrong shows up on every frame at once.
/// </summary>
public static class LayoutAudit
{
    /// <summary>One run of glyphs as drawn: its text, its ink box on the card, and the clip it was drawn under.</summary>
    public sealed record TextRun(string Text, Rect Ink, Rect Clip);

    /// <summary>A small picture or shape drawn on the card (a mana or set symbol, a badge).</summary>
    public sealed record Mark(Rect Box);

    public sealed record Finding(string Kind, string Detail)
    {
        public override string ToString() => $"{Kind}: {Detail}";
    }

    /// <summary>Every text run and small mark in <paramref name="drawing"/>, in card coordinates.</summary>
    public static (List<TextRun> Text, List<Mark> Marks) Collect(Drawing drawing, Size canvas)
    {
        var text = new List<TextRun>();
        var marks = new List<Mark>();
        Walk(drawing, Matrix.Identity, new Rect(canvas), text, marks, canvas);
        return (text, marks);
    }

    private static void Walk(Drawing d, Matrix m, Rect clip, List<TextRun> text, List<Mark> marks, Size canvas)
    {
        switch (d)
        {
            case DrawingGroup g:
                var inner = m;
                if (g.Transform != null) inner = g.Transform.Value * m;
                var c = clip;
                if (g.ClipGeometry != null)
                {
                    var b = g.ClipGeometry.Bounds;
                    if (!b.IsEmpty)
                    {
                        b.Transform(inner);
                        c = Rect.Intersect(c, b);
                    }
                }
                foreach (var child in g.Children) Walk(child, inner, c, text, marks, canvas);
                break;
            case GlyphRunDrawing gr when gr.GlyphRun != null:
                var ink = gr.Bounds;
                if (ink.IsEmpty || ink.Width <= 0 || ink.Height <= 0) break;   // spaces
                ink.Transform(m);
                text.Add(new TextRun(new string(gr.GlyphRun.Characters?.ToArray() ?? Array.Empty<char>()), ink, clip));
                break;
            case ImageDrawing img:
            {
                var r = img.Rect;
                r.Transform(m);
                if (r.Width * r.Height < canvas.Width * canvas.Height * 0.02) marks.Add(new Mark(Rect.Intersect(r, clip)));
                break;
            }
        }
    }

    /// <summary>The problems with where <paramref name="card"/>'s text landed (none when it's all where it belongs).</summary>
    /// <param name="spec">The layout the card was drawn with (the one <see cref="TemplateService.ResolveFor"/> gives).</param>
    /// <param name="frame">The frame picture the card was drawn on, to find text sitting on one of its drawn lines.</param>
    public static List<Finding> Inspect(Drawing drawing, CardModel card, TemplateSpec spec, BitmapSource? frame = null)
    {
        var canvas = new Size(spec.CanvasWidth, spec.CanvasHeight);
        var (text, marks) = Collect(drawing, canvas);
        var found = new List<Finding>();
        const double tol = 0.75;

        foreach (var t in text)
        {
            var shown = Rect.Intersect(t.Ink, t.Clip);
            if (shown.IsEmpty) continue;   // wholly clipped away on purpose (a hidden half)
            if (shown.Width < t.Ink.Width - tol * 2 || shown.Height < t.Ink.Height - tol * 2)
                found.Add(new("cut off", $"“{Short(t.Text)}” is cut by {Fmt(t.Ink.Width - shown.Width)}×{Fmt(t.Ink.Height - shown.Height)}px"));
            if (t.Ink.Left < -tol || t.Ink.Top < -tol || t.Ink.Right > canvas.Width + tol || t.Ink.Bottom > canvas.Height + tol)
                found.Add(new("off the card", $"“{Short(t.Text)}”"));
        }

        // Text on text, or on a symbol: ink boxes overlapping by more than a sliver.
        // (Punctuation is left out: a bracket hugging a mana symbol, or a kerned hyphen, is just tight type.)
        var visible = text.Select(t => t with { Ink = Rect.Intersect(t.Ink, t.Clip) }).Where(t => !t.Ink.IsEmpty).ToList();
        var words = visible.Where(t => t.Text.Any(char.IsLetterOrDigit)).ToList();
        for (int i = 0; i < words.Count; i++)
        {
            for (int j = i + 1; j < words.Count; j++)
                if (Clash(words[i].Ink, words[j].Ink))
                    found.Add(new("text on text", $"“{Short(words[i].Text)}” and “{Short(words[j].Text)}”"));
            foreach (var mk in marks)
                if (Clash(words[i].Ink, mk.Box))
                    found.Add(new("text on a symbol", $"“{Short(words[i].Text)}” at {Fmt(mk.Box.X)},{Fmt(mk.Box.Y)}"));
        }
        for (int i = 0; i < marks.Count; i++)
            for (int j = i + 1; j < marks.Count; j++)
                if (Clash(marks[i].Box, marks[j].Box, 0.25))
                    found.Add(new("symbol on symbol", $"at {Fmt(marks[i].Box.X)},{Fmt(marks[i].Box.Y)}"));

        // On a plain card (one face, upright), all text sits in one of the frame's boxes, and only the P/T is in the P/T box.
        if (!card.IsSplit && !card.IsFlip && !card.IsMeldBack && !spec.IsLandscape)
        {
            var boxes = new[] { spec.TitleBar, spec.TypeBar, spec.EffectiveTextBox, spec.PtBox, spec.CreditBar }
                .Select(r => new Rect(r.X, r.Y, Math.Max(0, r.W), Math.Max(0, r.H))).ToList();
            var pt = boxes[3];
            bool hasPt = card.HasPowerToughness || card.HasDefense || (card.IsPlaneswalker && card.Loyalty.Length > 0);
            var lines = frame == null ? null : FramePixels.Of(frame, canvas);
            foreach (var t in visible)
            {
                // (Not the P/T itself: its box is drawn over the frame, hiding whatever line is under it.)
                bool onPlate = pt.Contains(new Point(t.Ink.X + t.Ink.Width / 2, t.Ink.Y + t.Ink.Height / 2));
                if (lines != null && !onPlate && t.Text.Any(char.IsLetterOrDigit) && lines.CrossesLine(t.Ink))
                    found.Add(new("on a frame line", $"“{Short(t.Text)}” at {Fmt(t.Ink.X)},{Fmt(t.Ink.Y)}"));
                var ink = t.Ink;
                bool inPt = pt.Contains(new Point(ink.X + ink.Width / 2, ink.Y + ink.Height / 2));
                if (hasPt && !inPt && Clash(ink, pt, 0.2) && !IsFooter(t, spec))
                    found.Add(new("under the P/T box", $"“{Short(t.Text)}”"));
                // The footer may use the whole band under the text box (picture frames draw it below their credit box).
                if (!CardRenderer.ArtText(spec) && !boxes.Any(b => Inflate(b, 3).Contains(ink)) && !IsFooter(t, spec))
                    found.Add(new("outside its box", $"“{Short(t.Text)}” at {Fmt(ink.X)},{Fmt(ink.Y)} {Fmt(ink.Width)}×{Fmt(ink.Height)}"));
            }
        }
        return found.Distinct().ToList();
    }

    private static bool IsFooter(TextRun t, TemplateSpec spec)
        => t.Ink.Top >= Math.Min(spec.CreditBar.Y, spec.EffectiveTextBox.Y + spec.EffectiveTextBox.H) - 2;

    /// <summary>
    /// A frame picture's pixels, to tell whether text sits on one of its drawn lines (a panel's border, a rule). A
    /// line is a row (or column) across the text's box, grown by a pixel, in which nearly every opaque pixel stands out
    /// from the box's usual colour — a texture or a gradient never does that across a whole row.
    /// </summary>
    private sealed class FramePixels
    {
        private readonly byte[] _px; private readonly int _w, _h; private readonly double _sx, _sy;
        private FramePixels(byte[] px, int w, int h, double sx, double sy) => (_px, _w, _h, _sx, _sy) = (px, w, h, sx, sy);

        public static FramePixels Of(BitmapSource frame, Size canvas)
        {
            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var px = new byte[w * h * 4];
            bgra.CopyPixels(px, w * 4, 0);
            return new FramePixels(px, w, h, w / canvas.Width, h / canvas.Height);
        }

        public bool CrossesLine(Rect ink)
        {
            int x0 = Math.Max(0, (int)Math.Floor((ink.X - 1) * _sx)), x1 = Math.Min(_w - 1, (int)Math.Ceiling((ink.Right + 1) * _sx));
            int y0 = Math.Max(0, (int)Math.Floor((ink.Y - 1) * _sy)), y1 = Math.Min(_h - 1, (int)Math.Ceiling((ink.Bottom + 1) * _sy));
            if (x1 - x0 < 12 || y1 - y0 < 6) return false;
            var lum = new List<int>();
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (A(x, y) > 200) lum.Add(L(x, y));
            if (lum.Count < 20) return false;   // over the art: no frame here
            lum.Sort();
            int usual = lum[lum.Count / 2];
            bool Stands(int x, int y) => A(x, y) > 200 && Math.Abs(L(x, y) - usual) > 60;
            for (int y = y0; y <= y1; y++)
            {
                int n = 0;
                for (int x = x0; x <= x1; x++) if (Stands(x, y)) n++;
                if (n >= (x1 - x0 + 1) * 0.9) return true;
            }
            for (int x = x0; x <= x1; x++)
            {
                int n = 0;
                for (int y = y0; y <= y1; y++) if (Stands(x, y)) n++;
                if (n >= (y1 - y0 + 1) * 0.9) return true;
            }
            return false;
        }

        private int A(int x, int y) => _px[(y * _w + x) * 4 + 3];
        private int L(int x, int y) { int i = (y * _w + x) * 4; return (_px[i] * 114 + _px[i + 1] * 587 + _px[i + 2] * 299) / 1000; }
    }

    /// <summary>Whether two boxes overlap by more than a sliver: at least 1.5px each way and a share of the smaller box.</summary>
    private static bool Clash(Rect a, Rect b, double share = 0.12)
    {
        var x = Rect.Intersect(a, b);
        if (x.IsEmpty || x.Width < 1.5 || x.Height < 1.5) return false;
        double smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return smaller > 0 && x.Width * x.Height > smaller * share;
    }

    private static Rect Inflate(Rect r, double by) { r.Inflate(by, by); return r; }
    private static string Short(string s) { s = s.Trim(); return s.Length <= 28 ? s : s[..27] + "…"; }
    private static string Fmt(double v) => Math.Round(v, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
