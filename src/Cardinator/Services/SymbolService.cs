using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace Cardinator.Services;

/// <summary>
/// Provides mana/tap symbols as vector images. Prefers authentic Scryfall SVGs (downloaded once
/// and cached on disk), and falls back to clean procedurally-drawn pips when a symbol isn't
/// cached yet or there's no internet — so it always works offline.
/// </summary>
public sealed class SymbolService
{
    private readonly Dictionary<string, DrawingImage> _cache = new();
    private readonly object _lock = new();
    private Dictionary<string, string>? _symbologyMap;   // normalized token -> svg_uri

    private static readonly HttpClient Http = CreateClient();

    /// <summary>Raised after a background prime downloads new symbols (so callers can re-render).</summary>
    public event Action? Updated;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Cardinator/1.0");
        return http;
    }

    /// <summary>
    /// Returns a vector image for a symbol token such as "{R}", "{2}", "{T}", "{R/W}".
    /// Uses the cached Scryfall SVG if present, else a procedural pip. Null if unrecognizable.
    /// </summary>
    public DrawingImage? GetSymbol(string token)
    {
        var inner = Normalize(token);
        if (inner.Length == 0) return null;

        lock (_lock)
        {
            if (_cache.TryGetValue(inner, out var cached)) return cached;

            var svgPath = Path.Combine(AppPaths.SymbolsDir, SafeFile(inner) + ".svg");
            if (File.Exists(svgPath) && TryConvertSvg(svgPath, out var svgImg))
            {
                _cache[inner] = svgImg!;
                return svgImg;
            }

            var img = BuildProcedural(inner);
            _cache[inner] = img;
            return img;
        }
    }

    // --- Scryfall SVG prime -------------------------------------------------

    /// <summary>
    /// Downloads authentic symbol SVGs to the on-disk cache. If <paramref name="tokens"/> is null,
    /// primes the full Scryfall symbol set; otherwise only the given tokens. Safe to call in the
    /// background; silently no-ops when offline. Raises <see cref="Updated"/> if anything changed.
    /// </summary>
    public async Task PrimeAsync(IEnumerable<string>? tokens = null, CancellationToken ct = default)
    {
        try
        {
            var map = await EnsureSymbologyMapAsync(ct);
            if (map == null) return;

            IEnumerable<string> wanted = tokens != null
                ? tokens.Select(Normalize).Distinct()
                : map.Keys;

            bool changed = false;
            foreach (var inner in wanted)
            {
                ct.ThrowIfCancellationRequested();
                if (inner.Length == 0 || !map.TryGetValue(inner, out var uri)) continue;

                var svgPath = Path.Combine(AppPaths.SymbolsDir, SafeFile(inner) + ".svg");
                if (File.Exists(svgPath)) continue;

                try
                {
                    var bytes = await Http.GetByteArrayAsync(uri, ct);
                    await File.WriteAllBytesAsync(svgPath, bytes, ct);
                    changed = true;
                    await Task.Delay(60, ct);   // be polite to Scryfall's CDN
                }
                catch (OperationCanceledException) { throw; }
                catch { /* skip this symbol; procedural fallback remains */ }
            }

            if (changed)
            {
                lock (_lock) _cache.Clear();   // let freshly-downloaded SVGs be picked up
                Updated?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
        catch { /* offline or blocked: keep procedural pips */ }
    }

    private async Task<Dictionary<string, string>?> EnsureSymbologyMapAsync(CancellationToken ct)
    {
        if (_symbologyMap != null) return _symbologyMap;

        var mapPath = Path.Combine(AppPaths.SymbolsDir, "symbology.json");
        if (File.Exists(mapPath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    await File.ReadAllTextAsync(mapPath, ct));
                if (cached is { Count: > 0 }) return _symbologyMap = cached;
            }
            catch { /* fall through to refetch */ }
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.scryfall.com/symbology");
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var map = new Dictionary<string, string>();
            foreach (var el in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                if (el.TryGetProperty("symbol", out var s) && el.TryGetProperty("svg_uri", out var u))
                {
                    var key = Normalize(s.GetString() ?? "");
                    var uri = u.GetString();
                    if (key.Length > 0 && !string.IsNullOrEmpty(uri)) map[key] = uri;
                }
            }
            await File.WriteAllTextAsync(mapPath, JsonSerializer.Serialize(map), ct);
            return _symbologyMap = map;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryConvertSvg(string svgPath, out DrawingImage? image)
    {
        image = null;
        try
        {
            var settings = new WpfDrawingSettings
            {
                IncludeRuntime = false,
                TextAsGeometry = true,
                OptimizePath = true,
            };
            using var reader = new FileSvgReader(settings);
            var group = reader.Read(svgPath);
            if (group == null) return false;
            group.Freeze();
            var img = new DrawingImage(group);
            img.Freeze();
            image = img;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // --- procedural fallback pips -------------------------------------------

    private static readonly Color White = Color.FromRgb(0xF8, 0xF6, 0xD8);
    private static readonly Color Blue = Color.FromRgb(0xC1, 0xD7, 0xE9);
    private static readonly Color Black = Color.FromRgb(0xCB, 0xC5, 0xBF);
    private static readonly Color Red = Color.FromRgb(0xE4, 0x99, 0x77);
    private static readonly Color Green = Color.FromRgb(0xA3, 0xC0, 0x95);
    private static readonly Color Generic = Color.FromRgb(0xCA, 0xC5, 0xC0);

    private static readonly Color GlyphColor = Color.FromRgb(0x1A, 0x1A, 0x1A);
    private static readonly Typeface GlyphFace =
        new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private static DrawingImage BuildProcedural(string inner)
    {
        const double d = 100.0;
        var group = new DrawingGroup();

        if (inner.Contains('/'))
        {
            var parts = inner.Split('/');
            AddHalf(group, parts[0], left: true, d);
            AddHalf(group, parts[^1], left: false, d);
            AddOutline(group, d);
        }
        else
        {
            AddDisc(group, ColorFor(inner), new Rect(0, 0, d, d));
            AddGlyph(group, GlyphFor(inner), new Rect(0, 0, d, d));
            AddOutline(group, d);
        }

        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static void AddDisc(DrawingGroup g, Color fill, Rect box)
    {
        var brush = new RadialGradientBrush(Lighten(fill, 0.18), fill)
        {
            GradientOrigin = new Point(0.35, 0.30),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.65,
            RadiusY = 0.65,
        };
        brush.Freeze();
        var geo = new EllipseGeometry(box);
        geo.Freeze();
        g.Children.Add(new GeometryDrawing(brush, null, geo));
    }

    private static void AddHalf(DrawingGroup g, string part, bool left, double d)
    {
        var full = new EllipseGeometry(new Rect(0, 0, d, d));
        var clip = new RectangleGeometry(left ? new Rect(0, 0, d / 2, d) : new Rect(d / 2, 0, d / 2, d));
        var half = new CombinedGeometry(GeometryCombineMode.Intersect, full, clip);
        half.Freeze();
        var fill = new SolidColorBrush(ColorFor(part));
        fill.Freeze();
        g.Children.Add(new GeometryDrawing(fill, null, half));

        var box = left ? new Rect(0, 0, d / 2, d) : new Rect(d / 2, 0, d / 2, d);
        AddGlyph(g, GlyphFor(part), box, scale: 0.6);
    }

    private static void AddOutline(DrawingGroup g, double d)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), d * 0.045);
        pen.Freeze();
        var geo = new EllipseGeometry(new Rect(0, 0, d, d));
        geo.Freeze();
        g.Children.Add(new GeometryDrawing(null, pen, geo));
    }

    private static void AddGlyph(DrawingGroup g, string glyph, Rect box, double scale = 0.62)
    {
        if (string.IsNullOrEmpty(glyph)) return;
        var ft = new FormattedText(
            glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            GlyphFace, box.Height * scale, new SolidColorBrush(GlyphColor), 1.0);
        var origin = new Point(box.X + (box.Width - ft.Width) / 2, box.Y + (box.Height - ft.Height) / 2);
        var geo = ft.BuildGeometry(origin);
        geo.Freeze();
        var fill = new SolidColorBrush(GlyphColor);
        fill.Freeze();
        g.Children.Add(new GeometryDrawing(fill, null, geo));
    }

    private static Color ColorFor(string part) => part switch
    {
        "W" => White,
        "U" => Blue,
        "B" => Black,
        "R" => Red,
        "G" => Green,
        _ => Generic,
    };

    private static string GlyphFor(string part) => part switch
    {
        "T" => "T",
        "Q" => "Q",
        "P" => "P",
        "S" => "❄",
        _ => part,
    };

    private static Color Lighten(Color c, double amount)
    {
        byte L(byte v) => (byte)Math.Clamp(v + (255 - v) * amount, 0, 255);
        return Color.FromRgb(L(c.R), L(c.G), L(c.B));
    }

    // --- token helpers ------------------------------------------------------

    private static string Normalize(string token)
    {
        var s = token.Trim();
        if (s.StartsWith('{') && s.EndsWith('}')) s = s[1..^1];
        return s.Trim().ToUpperInvariant();
    }

    private static string SafeFile(string inner)
    {
        var chars = inner.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        return new string(chars);
    }
}
