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
    public required BitmapImage FrameImage { get; init; }

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

    private IReadOnlyList<Template> LoadAllCore()
    {
        try { EnsureDefaults(); } catch { /* best-effort; enumeration below still tries */ }

        var result = new List<Template>();
        foreach (var dir in SafeEnumerateDirs(AppPaths.TemplatesDir))
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

        // Guarantee the app always has at least the built-in frames to render with.
        if (result.Count == 0)
            result.AddRange(BuildBuiltInsInMemory());

        return result;
    }

    private static void EnsureDefaults()
    {
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
            if (!File.Exists(framePath))
                FrameGenerator.Generate(TemplateSpec.Load(specPath), framePath);
        }
    }

    /// <summary>Loads the frame image, regenerating it once if the file is missing or unreadable.</summary>
    private static BitmapImage? TryLoadFrame(TemplateSpec spec, string framePath)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (File.Exists(framePath))
            {
                try { return LoadBitmap(framePath); }
                catch { SafeDelete(framePath); }   // corrupt/truncated PNG → regenerate below
            }
            try { FrameGenerator.Generate(spec, framePath); }
            catch { return null; }
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

    private static BitmapImage LoadBitmap(string path)
    {
        // Decode from an in-memory copy: reading bytes first guarantees the file handle is
        // closed, so a corrupt frame can still be deleted/regenerated (UriSource would keep
        // the file locked when EndInit throws mid-decode).
        var bytes = File.ReadAllBytes(path);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;   // decodes immediately; stream can be discarded
        bmp.StreamSource = new MemoryStream(bytes);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static string Slug(string name) => TextUtil.Slug(name);
}
