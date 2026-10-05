using System.IO;
using System.IO.Compression;
using System.Net.Http;
using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>
/// Creates a custom template (the frame/border that sits on top of the art) from a user-supplied
/// frame image — a transparent PNG whose "window" is where the art shows through. The image is
/// kept as-is (not regenerated), and a starter template.json is written next to it with default
/// regions the user can tune. Works from a local file or a URL.
/// </summary>
public static class TemplateImporter
{
    /// <summary>Creates a template folder from raw frame-image bytes. Returns the template name.</summary>
    public static string CreateFromFrame(string name, byte[] frameBytes, bool fullArt = false)
    {
        if (frameBytes is not { Length: > 0 })
            throw new ArgumentException("The frame image was empty.", nameof(frameBytes));

        var displayName = string.IsNullOrWhiteSpace(name) ? "Custom" : name.Trim();
        var dir = UniqueTemplateDir(TextUtil.Slug(displayName));
        Directory.CreateDirectory(dir);

        var spec = new TemplateSpec { Name = displayName, FullArt = fullArt, CustomFrame = true };
        if (fullArt)
        {
            // Sensible full-art defaults so text lands on the art with a shadow.
            spec.ArtWindow = new Region { X = 0, Y = 0, W = spec.CanvasWidth, H = spec.CanvasHeight };
            foreach (var fnt in new[] { spec.TitleFont, spec.TypeFont, spec.RulesFont, spec.FlavorFont, spec.PtFont, spec.CreditFont })
            { fnt.Color = "#FFFFFF"; fnt.Shadow = true; }
        }
        // Write the user's frame first (atomically), then the spec — so a failure never leaves a spec
        // pointing at a missing frame. CustomFrame=true tells the loader to keep this image as-is.
        IoUtil.AtomicWriteBytes(Path.Combine(dir, "frame.png"), frameBytes);
        spec.Save(Path.Combine(dir, "template.json"));
        return displayName;
    }

    public static string CreateFromFile(string name, string framePath, bool fullArt = false)
    {
        var chosenName = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(framePath) : name;
        return CreateFromFrame(chosenName, File.ReadAllBytes(framePath), fullArt);
    }

    public static async Task<string> CreateFromUrlAsync(string name, string url, bool fullArt = false, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("User-Agent", "Cardinator/1.0");
        using var resp = await http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        bool isImage = (mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false)
                       || ImageIntake.LooksLikeImagePath(url);
        if (!isImage) throw new InvalidOperationException("That link didn't return an image file.");

        var chosenName = string.IsNullOrWhiteSpace(name)
            ? Path.GetFileNameWithoutExtension(url.Split('?')[0]) : name;
        return CreateFromFrame(chosenName, await resp.Content.ReadAsByteArrayAsync(ct), fullArt);
    }

    // --- template bundles (frame.png + template.json together) -----------------

    /// <summary>The extension used for a shareable template bundle (a zip of frame.png + template.json).</summary>
    public const string BundleExtension = ".cardframe";

    /// <summary>Imports a template bundle (a .cardframe/.zip containing template.json + frame.png) into a
    /// new templates/&lt;slug&gt; folder. Both files are kept as-is (CustomFrame is forced on so the imported
    /// frame is never regenerated). Returns the template name.</summary>
    public static string ImportBundle(string bundlePath)
    {
        using var zip = ZipFile.OpenRead(bundlePath);
        var specEntry = FindEntry(zip, "template.json")
            ?? throw new InvalidOperationException("That bundle has no template.json — it isn't a Cardinator template.");
        var frameEntry = FindEntry(zip, "frame.png")
            ?? throw new InvalidOperationException("That bundle has no frame.png — it isn't a Cardinator template.");

        var specJson = ReadEntryText(specEntry);
        var frameBytes = ReadEntryBytes(frameEntry);
        if (frameBytes.Length == 0) throw new InvalidOperationException("The bundle's frame.png was empty.");

        // Parse to validate + get the display name; force CustomFrame so the frame is kept verbatim.
        var spec = TemplateSpec.LoadFromJson(specJson);
        if (string.IsNullOrWhiteSpace(spec.Name)) spec.Name = Path.GetFileNameWithoutExtension(bundlePath);
        spec.CustomFrame = true;

        var dir = UniqueTemplateDir(TextUtil.Slug(spec.Name));
        Directory.CreateDirectory(dir);
        // Frame first, then spec — a failure never leaves a spec pointing at a missing frame.
        IoUtil.AtomicWriteBytes(Path.Combine(dir, "frame.png"), frameBytes);
        spec.Save(Path.Combine(dir, "template.json"));
        return spec.Name;
    }

    /// <summary>True if the path looks like a template bundle we can import (by extension).</summary>
    public static bool IsBundlePath(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(BundleExtension, StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".zip", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Exports a template's folder (template.json + frame.png) as a shareable bundle zip.</summary>
    public static void ExportBundle(string templateDir, string destPath)
    {
        var specPath = Path.Combine(templateDir, "template.json");
        var framePath = Path.Combine(templateDir, "frame.png");
        if (!File.Exists(specPath) || !File.Exists(framePath))
            throw new InvalidOperationException("This template is missing its template.json or frame.png.");

        // Build into a temp file then move into place, so a failure can't leave a half-written bundle.
        var tmp = destPath + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(specPath, "template.json");
            zip.CreateEntryFromFile(framePath, "frame.png");
        }
        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(tmp, destPath);
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string fileName)
        => zip.Entries.FirstOrDefault(e =>
            e.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase)
            || e.FullName.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase)
            // Zips written by some Windows tools use backslashes, so the entry has no Name and the
            // forward-slash test misses it — the bundle then looks like it has no template.json at all.
            || e.FullName.EndsWith("\\" + fileName, StringComparison.OrdinalIgnoreCase));

    private static string ReadEntryText(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Picks a templates/&lt;slug&gt; folder, adding -2, -3… if that slug already exists.</summary>
    public static string UniqueTemplateDir(string slug)
    {
        var baseDir = Path.Combine(AppPaths.TemplatesDir, slug);
        if (!Directory.Exists(baseDir)) return baseDir;
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(AppPaths.TemplatesDir, $"{slug}-{i}");
            if (!Directory.Exists(candidate)) return candidate;
        }
    }
}
