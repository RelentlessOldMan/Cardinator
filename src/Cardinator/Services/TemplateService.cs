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
    /// (case-insensitive). Used to warn before deleting a frame other cards still reference (L10).</summary>
    public static int CountReferencing(IEnumerable<CardModel> cards, string templateName)
        => cards?.Count(c => string.Equals(c.TemplateName, templateName, StringComparison.OrdinalIgnoreCase)) ?? 0;

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

        var result = new List<Template>(LoadFrom(AppPaths.TemplatesDir));

        // Guarantee the app always has at least the built-in frames to render with.
        if (result.Count == 0)
            result.AddRange(BuildBuiltInsInMemory());

        return result;
    }

    /// <summary>Loads every template folder (template.json + frame.png) directly under <paramref name="root"/>,
    /// sorted by name. Does not create or heal the built-ins — used to pull frames from an arbitrary folder
    /// (e.g. a batch of generated frames) without installing them first. Returns an empty list if the folder
    /// is missing or has none.</summary>
    public IReadOnlyList<Template> LoadFrom(string root)
    {
        var result = new List<Template>();
        foreach (var dir in SafeEnumerateDirs(root))
        {
            var specPath = Path.Combine(dir, "template.json");
            if (!File.Exists(specPath)) continue;

            TemplateSpec spec;
            try { spec = TemplateSpec.Load(specPath); }
            catch { continue; }

            var framePath = Path.Combine(dir, "frame.png");
            var frame = TryLoadFrame(spec, framePath);
            if (frame == null) continue;   // couldn't produce/read a frame; skip rather than crash

            result.Add(new Template
            {
                Name = spec.Name,
                Spec = spec,
                FramePath = framePath,
                FrameImage = frame,
            });
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
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
