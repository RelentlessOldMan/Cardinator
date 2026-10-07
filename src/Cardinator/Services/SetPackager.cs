using System.IO;
using System.IO.Compression;

namespace Cardinator.Services;

/// <summary>
/// Packages a whole set for sharing: one zip holding the project file + its <c>art/</c> folder, plus a
/// <c>frames/</c> folder with a bundle (template.json + frame.png) for every CUSTOM frame the set's cards
/// reference. Built-in frames are skipped — the recipient already has them. This closes the "frames don't
/// travel with a set" gap: the recipient unzips, imports the frames, and opens the project.
/// </summary>
public static class SetPackager
{
    /// <summary>
    /// Writes a shareable zip of the set at <paramref name="projectFilePath"/> to <paramref name="destZipPath"/>.
    /// Includes the project file, the sibling <c>art/</c> folder, and <c>frames/&lt;slug&gt;/</c> for each
    /// referenced non-built-in frame found under <paramref name="templatesDir"/>. The <c>out/</c> folder
    /// (regenerable renders) is deliberately excluded. Returns the number of custom frames bundled.
    /// </summary>
    public static int ExportSetWithFrames(
        string projectFilePath, IEnumerable<string> referencedTemplateNames, string templatesDir, string destZipPath)
        => ExportSetWithFrames(projectFilePath,
            referencedTemplateNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => Path.Combine(templatesDir, TextUtil.Slug(n))),
            destZipPath);

    /// <summary>As above, given the folders of the frames the cards use (a frame renamed in place keeps its old
    /// folder name, so a folder can't be guessed from the frame's name).</summary>
    public static int ExportSetWithFrames(string projectFilePath, IEnumerable<string> frameFolders, string destZipPath)
    {
        var projFolder = Path.GetDirectoryName(Path.GetFullPath(projectFilePath))
                         ?? throw new InvalidOperationException("Project file has no folder.");

        var tmp = destZipPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        int frames;
        try
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                // The project file at the zip root.
                zip.CreateEntryFromFile(projectFilePath, Path.GetFileName(projectFilePath));

                // The art folder (everything the cards point at), preserving relative layout.
                var artDir = Path.Combine(projFolder, "art");
                if (Directory.Exists(artDir))
                    foreach (var f in Directory.EnumerateFiles(artDir, "*", SearchOption.AllDirectories))
                        zip.CreateEntryFromFile(f, ToEntry(Path.GetRelativePath(projFolder, f)));

                // A bundle for each referenced CUSTOM frame (with its flip / sideways layouts).
                frames = 0;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var dir in frameFolders)
                {
                    var slug = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
                    if (string.IsNullOrEmpty(slug) || !seen.Add(slug)) continue;   // one bundle per distinct frame
                    if (TemplateService.IsBuiltIn(slug)) continue;                 // recipient already has shipped frames
                    if (TemplateImporter.AddBundleEntries(zip, dir, $"frames/{slug}/")) frames++;
                }
            }

            File.Move(tmp, destZipPath, overwrite: true);   // atomic swap: a failed move can't lose the old zip
            return frames;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            throw;
        }
    }

    private static string ToEntry(string relativePath) => relativePath.Replace('\\', '/');
}
