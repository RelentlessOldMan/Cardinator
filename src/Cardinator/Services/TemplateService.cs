using System.IO;
using System.Windows.Media.Imaging;
using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>A loaded template: its spec plus the frame image to composite over the art.</summary>
public sealed class Template
{
    public required string Name { get; init; }
    public required TemplateSpec Spec { get; init; }
    public required string FramePath { get; init; }
    public required BitmapSource FrameImage { get; init; }

    /// <summary>Hand-made layouts of this same frame for card shapes a fixed picture can't be re-laid into,
    /// keyed by <see cref="TemplateService.FlipVariant"/> / <see cref="TemplateService.LandscapeVariant"/>.
    /// Each lives in a subfolder of the template's folder (<c>flip/</c>, <c>landscape/</c>) and is a complete
    /// template of its own. Procedural frames don't need them — they derive these layouts on the fly.</summary>
    public IReadOnlyDictionary<string, Template> Variants { get; init; } = new Dictionary<string, Template>();

    // Shown by the Frame combo box's collapsed selection box, which falls back to ToString().
    public override string ToString() => Name;
}

/// <summary>
/// Discovers templates under CardinatorData/templates. Each template is a folder containing
/// template.json (+ a frame.png, generated from the spec if missing). Creates the built-in
/// starter templates on first run.
/// </summary>
public sealed class TemplateService
{
    // Serializes first-time generation so concurrent callers can't race on the same files.
    private static readonly object LoadLock = new();

    // --- orientation ---------------------------------------------------------------------------------

    private static readonly object VariantLock = new();
    private static readonly Dictionary<string, Template> LandscapeVariants = new();
    private static readonly Dictionary<string, Template> FlipVariants = new();

    // Frames by name, for the few places that pick a frame by name mid-render (a split card's other half with a
    // frame of its own): the installed ones, replaced by each LoadAll so a deleted frame drops out, and frames
    // loaded from any other folder (a batch's templates=<dir>), which are only ever added. A folder's frame wins
    // over an installed one of the same name, as it does for the card itself in --rendercards.
    private static volatile Dictionary<string, Template> _installed = new(StringComparer.Ordinal);
    private static volatile Dictionary<string, Template> _extra = new(StringComparer.Ordinal);

    /// <summary>The frame called <paramref name="name"/>: one loaded from another folder with <see cref="LoadFrom"/>,
    /// else an installed one (as of the last <see cref="LoadAll"/>); null if there's none.</summary>
    public static Template? Named(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return _extra.TryGetValue(name, out var x) ? x : _installed.TryGetValue(name, out var t) ? t : null;
    }

    private static void Catalog(IEnumerable<Template> templates, bool installed)
    {
        lock (VariantLock)
        {
            var next = installed ? new Dictionary<string, Template>(StringComparer.Ordinal)
                                 : new Dictionary<string, Template>(_extra, StringComparer.Ordinal);
            foreach (var t in templates) next[t.Name] = t;
            if (installed) _installed = next; else _extra = next;
        }
    }

    /// <summary>Subfolder / <see cref="Template.Variants"/> key for a frame's flip-card layout.</summary>
    public const string FlipVariant = "flip";
    /// <summary>Subfolder / <see cref="Template.Variants"/> key for a frame's sideways (landscape) layout.</summary>
    public const string LandscapeVariant = "landscape";

    /// <summary>
    /// The template a face ACTUALLY renders with. A face that wants landscape (a Battle, a Plane, or one
    /// forced via <see cref="CardModel.Orientation"/>) on a portrait procedural frame gets that frame's
    /// landscape layout (<see cref="TemplateSpec.ToLandscape"/>). Everything else is unchanged: a template
    /// that is already landscape is the user's explicit choice, and a raster custom frame is a fixed image
    /// that can't be re-laid-out (CardValidator tells the user so). Idempotent, so every render path can
    /// call it.
    /// </summary>
    public static Template ResolveFor(CardModel card, Template template)
    {
        // A split card draws each half as the frame's normal (upright) card, side by side, turned sideways.
        if (card.IsSplit || card.IsMeldBack) return template;   // drawn from the frame's normal card

        // A flip card wants its frame's flip layout: a hand-made "flip" variant if the frame ships one,
        // otherwise (procedural frames) a derived one. A picture frame without one stays upright and the
        // card renders as a plain card (CardValidator says why).
        if (card.IsFlip)
        {
            if (template.Spec.IsFlipLayout) return template;
            if (template.Variants.TryGetValue(FlipVariant, out var flip)) return flip;
            return template.Spec.CustomFrame ? template : FlipOf(template) ?? template;
        }

        if (template.Spec.IsLandscape || !card.WantsLandscape) return template;
        if (template.Variants.TryGetValue(LandscapeVariant, out var side)) return side;
        if (template.Spec.CustomFrame) return template;
        return LandscapeOf(template) ?? template;
    }

    /// <summary>The flip-card layout of a procedural template (<see cref="TemplateSpec.ToFlip"/>): the frame
    /// is generated for the top half's layout, then its bottom half is replaced by the top half mirrored, so
    /// both halves match exactly (the approach approved on the picture frames). Cached by content hash in
    /// <c>CardinatorData/cache/flip</c>. Null if it can't be built — the caller falls back to the plain frame.</summary>
    public static Template? FlipOf(Template template)
    {
        var spec = template.Spec.ToFlip();
        var key = spec.ContentHash();
        lock (VariantLock)
        {
            if (FlipVariants.TryGetValue(key, out var hit)) return hit;
            try
            {
                var dir = Path.Combine(AppPaths.DataDir, "cache", "flip");
                Directory.CreateDirectory(dir);
                var framePath = Path.Combine(dir, key[..20] + ".png");

                BitmapImage frame;
                try
                {
                    if (!File.Exists(framePath)) GenerateMirrored(spec, framePath);
                    frame = CustomFrameComposer.LoadBitmap(framePath);
                }
                catch
                {
                    SafeDelete(framePath);
                    GenerateMirrored(spec, framePath);
                    frame = CustomFrameComposer.LoadBitmap(framePath);
                }

                var variant = new Template { Name = template.Name, Spec = spec, FramePath = framePath, FrameImage = frame };
                FlipVariants[key] = variant;
                return variant;
            }
            catch { return null; }
        }
    }

    /// <summary>Generates a frame for <paramref name="spec"/> and replaces its bottom half with its top half
    /// mirrored top-to-bottom. Written atomically.</summary>
    private static void GenerateMirrored(TemplateSpec spec, string framePath)
    {
        var raw = framePath + ".raw.tmp";
        try
        {
            FrameGenerator.Generate(spec, raw);
            var mirrored = MirrorTopHalf(CustomFrameComposer.LoadBitmap(raw));
            var tmp = framePath + ".tmp";
            CardExporter.SavePng(mirrored, tmp);
            File.Move(tmp, framePath, overwrite: true);
        }
        finally { SafeDelete(raw); }
    }

    /// <summary>A copy of <paramref name="src"/> whose bottom half is its top half flipped vertically.</summary>
    internal static BitmapSource MirrorTopHalf(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight, half = h / 2;
        var top = new CroppedBitmap(src, new System.Windows.Int32Rect(0, 0, w, half));
        var flipped = new TransformedBitmap(top, new System.Windows.Media.ScaleTransform(1, -1));
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Placed in device pixels, whatever DPI the source claims.
            dc.DrawImage(top, new System.Windows.Rect(0, 0, w, half));
            dc.DrawImage(flipped, new System.Windows.Rect(0, h - half, w, half));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>The landscape layout of a portrait procedural template, with its frame generated once and
    /// cached by content hash in <c>CardinatorData/cache/landscape</c> — never inside the template's own
    /// folder, so a shared template bundle contains only what the user made. Null if it can't be built
    /// (the caller then falls back to portrait rather than failing the render).</summary>
    public static Template? LandscapeOf(Template template)
    {
        var spec = template.Spec.ToLandscape();
        var key = spec.ContentHash();
        lock (VariantLock)
        {
            if (LandscapeVariants.TryGetValue(key, out var hit)) return hit;
            try
            {
                var dir = Path.Combine(AppPaths.DataDir, "cache", "landscape");
                Directory.CreateDirectory(dir);
                var framePath = Path.Combine(dir, key[..20] + ".png");

                BitmapImage frame;
                try
                {
                    if (!File.Exists(framePath)) GenerateAtomically(spec, framePath);
                    frame = CustomFrameComposer.LoadBitmap(framePath);
                }
                catch
                {
                    // A cache entry is reproducible, so a bad one is simply rebuilt.
                    SafeDelete(framePath);
                    GenerateAtomically(spec, framePath);
                    frame = CustomFrameComposer.LoadBitmap(framePath);
                }

                var variant = new Template { Name = template.Name, Spec = spec, FramePath = framePath, FrameImage = frame };
                LandscapeVariants[key] = variant;
                return variant;
            }
            catch { return null; }
        }
    }

    private static void GenerateAtomically(TemplateSpec spec, string framePath)
    {
        var tmp = framePath + ".tmp";
        FrameGenerator.Generate(spec, tmp);
        File.Move(tmp, framePath, overwrite: true);
    }

    public IReadOnlyList<Template> LoadAll()
    {
        lock (LoadLock)
        {
            return LoadAllCore();
        }
    }

    private static HashSet<string>? _builtInSlugs;

    /// <summary>True if <paramref name="slug"/> is a shipped frame — a procedural built-in or a bundled
    /// raster template. These self-heal on launch, so the UI protects them from deletion.</summary>
    public static bool IsBuiltIn(string slug)
    {
        _builtInSlugs ??= BuildBuiltInSlugSet();
        return _builtInSlugs.Contains(slug);
    }

    /// <summary>Counts how many of <paramref name="cards"/> use the frame named <paramref name="templateName"/>
    /// (case-insensitive) — for the card or a split card's other half. Used to warn before deleting a frame other
    /// cards still reference (L10).</summary>
    public static int CountReferencing(IEnumerable<CardModel> cards, string templateName)
        => cards?.Count(c => FramesUsed(c).Contains(templateName, StringComparer.OrdinalIgnoreCase)) ?? 0;

    /// <summary>Every frame a card is drawn with: its own and, on a split card, its other half's own.</summary>
    public static IEnumerable<string> FramesUsed(CardModel card)
    {
        if (!string.IsNullOrWhiteSpace(card.TemplateName)) yield return card.TemplateName;
        if (card.IsSplit && !string.IsNullOrWhiteSpace(card.HalfTemplateName)) yield return card.HalfTemplateName;
    }

    private static HashSet<string> BuildBuiltInSlugSet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in BuiltInTemplates.All()) set.Add(Slug(spec.Name));
        foreach (var s in SampleTemplates.BundledSlugs()) set.Add(s);
        return set;
    }

    private IReadOnlyList<Template> LoadAllCore()
    {
        try { EnsureDefaults(); } catch { /* best-effort; enumeration below still tries */ }

        var result = new List<Template>(LoadFolder(AppPaths.TemplatesDir));

        // Guarantee the app always has at least the built-in frames to render with.
        if (result.Count == 0)
            result.AddRange(BuildBuiltInsInMemory());

        Catalog(result, installed: true);
        return result;
    }

    /// <summary>Loads every template folder (template.json + frame.png) directly under <paramref name="root"/>,
    /// sorted by name. Does not create or heal the built-ins — used to pull frames from an arbitrary folder
    /// (e.g. a batch of generated frames) without installing them first. Returns an empty list if the folder
    /// is missing or has none.</summary>
    public IReadOnlyList<Template> LoadFrom(string root)
    {
        var result = LoadFolder(root);
        Catalog(result, installed: false);
        return result;
    }

    private static List<Template> LoadFolder(string root)
    {
        var result = new List<Template>();
        foreach (var dir in SafeEnumerateDirs(root))
        {
            var t = LoadOne(dir, name: null);
            if (t == null) continue;

            // Optional hand-made layouts beside it (flip/, landscape/). A broken variant is just skipped —
            // the card then renders with the plain frame, never fails.
            var variants = new Dictionary<string, Template>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in new[] { FlipVariant, LandscapeVariant })
            {
                var vdir = Path.Combine(dir, key);
                if (!Directory.Exists(vdir)) continue;
                var v = LoadOne(vdir, name: t.Name);
                if (v == null) continue;
                // A variant must actually BE that shape, or it would be used for the wrong cards.
                if (key == FlipVariant && !v.Spec.IsFlipLayout) continue;
                if (key == LandscapeVariant && !v.Spec.IsLandscape) continue;
                variants[key] = v;
            }
            result.Add(variants.Count == 0 ? t : new Template
            {
                Name = t.Name, Spec = t.Spec, FramePath = t.FramePath, FrameImage = t.FrameImage, Variants = variants,
            });
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    /// <summary>Loads one template folder (template.json + frame.png), or null if it isn't a usable template.
    /// A variant is named after its parent so a card's TemplateName still matches.</summary>
    private static Template? LoadOne(string dir, string? name)
    {
        var specPath = Path.Combine(dir, "template.json");
        if (!File.Exists(specPath)) return null;

        TemplateSpec spec;
        try { spec = TemplateSpec.Load(specPath); }
        catch { return null; }

        var framePath = Path.Combine(dir, "frame.png");
        var frame = TryLoadFrame(spec, framePath);
        if (frame == null) return null;   // couldn't produce/read a frame; skip rather than crash

        return new Template { Name = name ?? spec.Name, Spec = spec, FramePath = framePath, FrameImage = frame };
    }

    private static void EnsureDefaults()
    {
        // Raster sample templates (bundled frame.png + template.json) — extracted straight to disk.
        SampleTemplates.EnsureExtracted();

        foreach (var spec in BuiltInTemplates.All())
        {
            var dir = Path.Combine(AppPaths.TemplatesDir, Slug(spec.Name));
            var specPath = Path.Combine(dir, "template.json");
            var framePath = Path.Combine(dir, "frame.png");

            // Rewrite a missing OR corrupt built-in spec so a truncated file self-heals.
            bool specOk = File.Exists(specPath) && TryLoadSpec(specPath, out _);
            if (!specOk)
            {
                spec.Save(specPath);
                SafeDelete(framePath);   // spec changed → frame must be regenerated
            }
            EnsureFrame(TemplateSpec.Load(specPath), framePath);
        }
    }

    /// <summary>Regenerates frame.png when it's missing OR when the spec it was baked from has changed
    /// (detected via a content-hash sidecar). This is the guard against a stale frame being composited
    /// with mismatched text regions — the "everything slammed to the edges" class of bug.</summary>
    internal static void EnsureFrame(TemplateSpec spec, string framePath)
    {
        // A user-imported frame image must never be overwritten by a spec-generated one.
        if (spec.CustomFrame)
        {
            // Source + fit knobs -> composite frame.png live (framing stays editable). Regenerates when
            // the source or any fit knob changes (content-hash sidecar).
            var dir = Path.GetDirectoryName(framePath);
            if (CustomFrameComposer.HasSource(spec) && dir != null)
            {
                var srcPath = Path.Combine(dir, spec.FrameSrc);
                if (File.Exists(srcPath))
                {
                    var hp = framePath + ".hash";
                    var wantFit = CustomFrameComposer.FitHash(spec, srcPath);
                    if (File.Exists(framePath) && File.Exists(hp))
                    {
                        try { if (File.ReadAllText(hp).Trim() == wantFit) return; } catch { /* regenerate */ }
                    }
                    var tmp2 = framePath + ".tmp";
                    CustomFrameComposer.Generate(spec, srcPath, tmp2);
                    if (File.Exists(framePath)) File.Delete(framePath);
                    File.Move(tmp2, framePath);
                    try { File.WriteAllText(hp, wantFit); } catch { /* best effort */ }
                    return;
                }
            }
            // No source: keep the baked frame.png as-is; only make a placeholder if it's gone entirely.
            if (!File.Exists(framePath)) FrameGenerator.Generate(spec, framePath);
            return;
        }

        var hashPath = framePath + ".hash";
        var want = spec.ContentHash();
        if (File.Exists(framePath) && File.Exists(hashPath))
        {
            try { if (File.ReadAllText(hashPath).Trim() == want) return; } catch { /* regenerate below */ }
        }

        // Write to a temp file then move into place, so a crash mid-render can never leave a truncated
        // frame.png that still matches its hash (which would then be composited as a stale/corrupt frame).
        var tmp = framePath + ".tmp";
        FrameGenerator.Generate(spec, tmp);
        try
        {
            if (File.Exists(framePath)) File.Delete(framePath);
            File.Move(tmp, framePath);
        }
        catch { SafeDelete(tmp); throw; }
        try { File.WriteAllText(hashPath, want); } catch { /* best-effort; frame still valid */ }
    }

    /// <summary>Loads the frame image, (re)generating it if missing, stale (spec changed), or unreadable.</summary>
    private static BitmapImage? TryLoadFrame(TemplateSpec spec, string framePath)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try { EnsureFrame(spec, framePath); }
            catch { return null; }
            if (File.Exists(framePath))
            {
                try { return LoadBitmap(framePath); }
                catch
                {
                    // A user-imported frame with no FrameSrc is IRREPLACEABLE — frame.png is the only copy of
                    // their image. Deleting it would let EnsureFrame bake a generic placeholder over it on the
                    // next pass (silent, permanent loss from one transient decode/read error). Set it aside
                    // instead and skip the template this run. Procedural and composed frames are reproducible,
                    // so for those the old delete-and-regenerate is still right.
                    if (spec.CustomFrame && !CustomFrameComposer.HasSource(spec))
                    {
                        SafeSetAside(framePath);
                        return null;
                    }
                    SafeDelete(framePath);
                    SafeDelete(framePath + ".hash");   // corrupt PNG → regenerate
                }
            }
        }
        return null;
    }

    /// <summary>Last-resort in-memory built-ins: generate frames into the data dir and load them.</summary>
    private static IEnumerable<Template> BuildBuiltInsInMemory()
    {
        foreach (var spec in BuiltInTemplates.All())
        {
            var dir = Path.Combine(AppPaths.TemplatesDir, Slug(spec.Name));
            var framePath = Path.Combine(dir, "frame.png");
            var frame = TryLoadFrame(spec, framePath);
            if (frame == null) continue;
            yield return new Template { Name = spec.Name, Spec = spec, FramePath = framePath, FrameImage = frame };
        }
    }

    private static bool TryLoadSpec(string path, out TemplateSpec? spec)
    {
        try { spec = TemplateSpec.Load(path); return true; }
        catch { spec = null; return false; }
    }

    private static IEnumerable<string> SafeEnumerateDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    /// <summary>Moves a file that couldn't be read out of the way (keeping its bytes) instead of deleting it,
    /// so an unreadable but irreplaceable user image survives for recovery. Best-effort: if it can't be
    /// moved, the file is left exactly where it is — never deleted.</summary>
    private static void SafeSetAside(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var dest = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            for (int i = 2; File.Exists(dest); i++) dest = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}-{i}";
            File.Move(path, dest);
            SafeDelete(path + ".hash");
        }
        catch { /* leave the file in place — losing it is worse than a skipped template */ }
    }

    // One shared loader (CustomFrameComposer.LoadBitmap) for every frame image, so the file-handle
    // discipline that keeps a corrupt frame recoverable can't drift between two copies of this code.
    private static BitmapImage LoadBitmap(string path) => CustomFrameComposer.LoadBitmap(path);

    private static string Slug(string name) => TextUtil.Slug(name);
}
