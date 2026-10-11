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
    private readonly SemaphoreSlim _mapGate = new(1, 1);   // serialize the symbology fetch/write
    private volatile Dictionary<string, string>? _symbologyMap;   // normalized token -> svg_uri

    private static readonly HttpClient Http = CreateClient();

    private readonly bool _drawnOnly;

    public SymbolService() { }

    /// <param name="drawnOnly">Always draw the app's own pips, never a cached Scryfall SVG — for golden-image tests,
    /// whose output must not depend on which symbols a machine happens to have downloaded.</param>
    internal SymbolService(bool drawnOnly) => _drawnOnly = drawnOnly;

    /// <summary>Raised after a background prime downloads new symbols (so callers can re-render).</summary>
    public event Action? Updated;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(ScryfallClient.UserAgent);
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
            if (!_drawnOnly && File.Exists(svgPath) && TryConvertSvg(svgPath, out var svgImg))
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
    /// <summary>Set by tests (or CARDINATOR_NO_NETWORK=1) to keep <see cref="PrimeAsync"/> offline. Unit
    /// tests construct real windows, whose constructors prime symbols — that would otherwise download the
    /// whole Scryfall symbology on every test run and make rendering depend on run history.</summary>
    public static bool SuppressPrime { get; set; } =
        Environment.GetEnvironmentVariable("CARDINATOR_NO_NETWORK") == "1";

    public async Task PrimeAsync(IEnumerable<string>? tokens = null, CancellationToken ct = default)
    {
        if (SuppressPrime) return;
        try
        {
            var map = await EnsureSymbologyMapAsync(ct);
            if (map == null) return;

            IEnumerable<string> wanted = tokens != null
                ? tokens.Select(Normalize).Distinct().ToList()
                : map.Keys;
            // A symbol the cached list doesn't know may be one Scryfall added since: fetch the list again, once a run.
            if (tokens != null && !_listRefreshed && wanted.Any(t => t.Length > 0 && !map.ContainsKey(t)))
            {
                _listRefreshed = true;
                map = await EnsureSymbologyMapAsync(ct, refresh: true) ?? map;
            }

            bool changed = false;
            foreach (var inner in wanted)
            {
                ct.ThrowIfCancellationRequested();
                if (inner.Length == 0 || !map.TryGetValue(inner, out var uri)) continue;

                var svgPath = Path.Combine(AppPaths.SymbolsDir, SafeFile(inner) + ".svg");
                if (File.Exists(svgPath) && LooksLikeSvg(File.ReadAllBytes(svgPath))) continue;   // a bad one is fetched again

                try
                {
                    var bytes = await Http.GetByteArrayAsync(uri, ct);
                    // A captive portal or error page answers 200 with HTML — never cache that as the symbol.
                    if (!LooksLikeSvg(bytes)) continue;
                    IoUtil.AtomicWriteBytes(svgPath, bytes);   // avoid a torn .svg if two primes race
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

    private static volatile bool _listRefreshed;   // the symbol list was fetched again this run (see PrimeAsync)

    /// <param name="refresh">Fetch the list from Scryfall even though one is cached; on failure the cached one stays.</param>
    private async Task<Dictionary<string, string>?> EnsureSymbologyMapAsync(CancellationToken ct, bool refresh = false)
    {
        if (_symbologyMap != null && !refresh) return _symbologyMap;

        // Serialize so two concurrent primes don't both fetch /symbology and race on the cache file.
        await _mapGate.WaitAsync(ct);
        try
        {
            if (_symbologyMap != null && !refresh) return _symbologyMap;   // another caller filled it while we waited

            var mapPath = Path.Combine(AppPaths.SymbolsDir, "symbology.json");
            // A list over 30 days old is fetched again too (kept if Scryfall can't be reached).
            bool stale = File.Exists(mapPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(mapPath) > TimeSpan.FromDays(30);
            Dictionary<string, string>? fallback = _symbologyMap;
            if (File.Exists(mapPath) && (refresh || stale) && fallback == null)
            {
                try { fallback = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(mapPath, ct)); }
                catch { /* unreadable: nothing to fall back on */ }
            }
            if (File.Exists(mapPath) && !refresh && !stale)
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
                if (!resp.IsSuccessStatusCode) return Keep(fallback);

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return Keep(fallback);   // unexpected shape (e.g. an error page) — keep what we had
                var map = new Dictionary<string, string>();
                foreach (var el in data.EnumerateArray())
                {
                    if (el.TryGetProperty("symbol", out var s) && el.TryGetProperty("svg_uri", out var u))
                    {
                        var key = Normalize(s.GetString() ?? "");
                        var uri = u.GetString();
                        if (key.Length > 0 && !string.IsNullOrEmpty(uri)) map[key] = uri;
                    }
                }
                if (map.Count == 0) return Keep(fallback);
                IoUtil.AtomicWriteText(mapPath, JsonSerializer.Serialize(map));
                return _symbologyMap = map;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                return Keep(fallback);
            }
        }
        finally { _mapGate.Release(); }
    }

    private Dictionary<string, string>? Keep(Dictionary<string, string>? fallback)
        => fallback is { Count: > 0 } ? _symbologyMap = fallback : null;

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
            if (parts.Length == 3 && parts[2] == "P")   // phyrexian hybrid ({G/U/P}): both colours, each marked P
            {
                AddHalf(group, parts[0], left: true, d, glyph: "P");
                AddHalf(group, parts[1], left: false, d, glyph: "P");
            }
            else
            {
                AddHalf(group, parts[0], left: true, d);
                AddHalf(group, parts[^1], left: false, d);
            }
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

    private static void AddHalf(DrawingGroup g, string part, bool left, double d, string? glyph = null)
    {
        var full = new EllipseGeometry(new Rect(0, 0, d, d));
        var clip = new RectangleGeometry(left ? new Rect(0, 0, d / 2, d) : new Rect(d / 2, 0, d / 2, d));
        var half = new CombinedGeometry(GeometryCombineMode.Intersect, full, clip);
        half.Freeze();
        var fill = new SolidColorBrush(ColorFor(part));
        fill.Freeze();
        g.Children.Add(new GeometryDrawing(fill, null, half));

        var box = left ? new Rect(0, 0, d / 2, d) : new Rect(d / 2, 0, d / 2, d);
        AddGlyph(g, glyph ?? GlyphFor(part), box, scale: 0.6);
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
        FormattedText Text(double size) => new(
            glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            GlyphFace, size, new SolidColorBrush(GlyphColor), 1.0);
        var ft = Text(box.Height * scale);
        // A long glyph ({1000000}, {HW}, an unknown word) is made smaller to fit across the disc.
        double room = box.Width * 0.8;
        if (ft.Width > room) ft = Text(Math.Max(1, box.Height * scale * room / ft.Width));
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

    // Scryfall's canonical two-color hybrid order (guild order). Cards may type either order.
    private static readonly HashSet<string> Guilds = new(StringComparer.Ordinal)
        { "W/U", "U/B", "B/R", "R/G", "G/W", "W/B", "U/R", "B/G", "R/W", "G/U" };

    private static string Normalize(string token)
    {
        var s = token.Trim();
        if (s.StartsWith('{') && s.EndsWith('}')) s = s[1..^1];
        s = s.Trim().ToUpperInvariant();
        // Canonicalize a reversed two-color hybrid (e.g. "G/R" -> "R/G") so it matches Scryfall + the cache.
        if (s.Contains('/'))
        {
            var parts = s.Split('/');
            if (parts.Length == 2 && !Guilds.Contains(s))
            {
                var rev = parts[1] + "/" + parts[0];
                if (Guilds.Contains(rev)) return rev;
            }
        }
        return s;
    }

    /// <summary>The cache file name for a symbol: "R/W" → "R_W" (as always), and any other character by its code
    /// ("½" → "u00BD", "∞" → "u221E") — they all used to become "_", so those symbols shared one file.</summary>
    internal static string SafeFile(string inner) => string.Concat(inner.Select(c =>
        char.IsAsciiLetterOrDigit(c) ? c.ToString() : c == '/' ? "_" : $"u{(int)c:X4}"));

    /// <summary>True when downloaded bytes are an SVG document (not an HTML error or portal page).</summary>
    internal static bool LooksLikeSvg(byte[] bytes)
    {
        var head = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 1024));
        return head.Contains("<svg", StringComparison.OrdinalIgnoreCase)
               && !head.Contains("<html", StringComparison.OrdinalIgnoreCase);
    }
}
