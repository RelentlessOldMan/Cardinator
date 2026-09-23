using System.IO;
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

        var spec = new TemplateSpec { Name = displayName, FullArt = fullArt };
        if (fullArt)
        {
            // Sensible full-art defaults so text lands on the art with a shadow.
            spec.ArtWindow = new Region { X = 0, Y = 0, W = spec.CanvasWidth, H = spec.CanvasHeight };
            foreach (var fnt in new[] { spec.TitleFont, spec.TypeFont, spec.RulesFont, spec.FlavorFont, spec.PtFont, spec.CreditFont })
            { fnt.Color = "#FFFFFF"; fnt.Shadow = true; }
        }
        spec.Save(Path.Combine(dir, "template.json"));

        // Keep the user's own frame image — TemplateService only regenerates when it can't load it.
        File.WriteAllBytes(Path.Combine(dir, "frame.png"), frameBytes);
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

    /// <summary>Picks a templates/&lt;slug&gt; folder, adding -2, -3… if that slug already exists.</summary>
    private static string UniqueTemplateDir(string slug)
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
