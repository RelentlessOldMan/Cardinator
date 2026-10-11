using System.IO;
using System.IO.Compression;

namespace Cardinator.Services;

/// <summary>
/// Packages a whole set for sharing: one zip holding the project file + its <c>art/</c> folder, plus a
/// <c>frames/</c> folder with a bundle (template.json + frame.png) for every CUSTOM frame the set's cards
/// reference. Built-in frames are skipped — the recipient already has them. This closes the "frames don't
/// travel with a set" gap: the recipient opens (or drops) the zip, and <see cref="UnpackSharedSet"/> unpacks the set,
/// installs its frames and hands back the project to open.
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

    /// <summary>True when the zip is a shared set (a project file at its root), not just frames.</summary>
    public static bool IsSharedSet(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return ProjectEntry(zip) != null;
        }
        catch { return false; }
    }

    /// <summary>The set's project file: a .cardinator at the zip's root, or a set saved as .json (Share packs the file
    /// under its own name) — a root .json holding cards, never a frame's template.json.</summary>
    private static ZipArchiveEntry? ProjectEntry(ZipArchive zip)
    {
        var atRoot = zip.Entries.Where(e => e.Name.Length > 0 && !e.FullName.Replace('\\', '/').TrimStart('/').Contains('/')).ToList();
        return atRoot.FirstOrDefault(e => e.Name.EndsWith(".cardinator", StringComparison.OrdinalIgnoreCase))
            ?? atRoot.FirstOrDefault(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                          && !e.Name.Equals("template.json", StringComparison.OrdinalIgnoreCase) && HoldsCards(e));
    }

    private static bool HoldsCards(ZipArchiveEntry e)
    {
        try
        {
            if (e.Length > 64L << 20) return false;
            using var stream = e.Open();
            using var doc = System.Text.Json.JsonDocument.Parse(stream);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.EnumerateObject().Any(p => p.Name.Equals("cards", StringComparison.OrdinalIgnoreCase)
                                                              && p.Value.ValueKind == System.Text.Json.JsonValueKind.Array);
        }
        catch { return false; }
    }

    /// <summary>The most a shared set may unpack: anything bigger isn't a set of cards (a zip bomb, or the wrong zip).</summary>
    internal const long MaxUnpackedBytes = 4L << 30;
    internal const int MaxUnpackedFiles = 20000;

    /// <summary>
    /// Opens a shared set: unpacks its project file and <c>art/</c> into a new folder under <paramref name="parentDir"/>
    /// (named after the set, "(2)" if that's taken — never over another set), installs its frames (one already installed
    /// and identical is reused), and points the cards at any frame that had to take a new name because a different frame
    /// of that name was installed here. Returns the unpacked project file. Nothing outside the new folder is written
    /// from the zip, whatever its entries say.
    /// </summary>
    public static string UnpackSharedSet(string zipPath, string parentDir)
    {
        string projectPath;
        bool hasFrames;
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var projEntry = ProjectEntry(zip) ?? throw new InvalidOperationException("That zip has no set in it (no .cardinator file).");
            var setName = TextUtil.SafeFileName(Path.GetFileNameWithoutExtension(projEntry.Name));
            if (string.IsNullOrWhiteSpace(setName)) setName = "Shared set";
            var dest = Path.Combine(parentDir, setName);
            for (int n = 2; Directory.Exists(dest) || File.Exists(dest); n++) dest = Path.Combine(parentDir, $"{setName} ({n})");
            var wanted = zip.Entries.Where(e =>
            {
                var name = e.FullName.Replace('\\', '/').TrimStart('/');
                return !name.EndsWith('/') && (e == projEntry || name.StartsWith("art/", StringComparison.OrdinalIgnoreCase));
            }).ToList();
            if (wanted.Count > MaxUnpackedFiles || wanted.Sum(e => e.Length) > MaxUnpackedBytes)
                throw new InvalidOperationException("That zip is far too big to be a shared set.");

            Directory.CreateDirectory(dest);
            var root = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            projectPath = "";
            try
            {
                foreach (var e in wanted)
                {
                    var name = e.FullName.Replace('\\', '/').TrimStart('/');
                    var target = Path.GetFullPath(Path.Combine(root, name));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // "../" and the like stay out
                    if (e == projEntry) projectPath = target;
                    if (File.Exists(target)) continue;   // a second entry for the same file ("a.png" and "A.png"): the first wins
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    e.ExtractToFile(target, overwrite: false);
                }
                if (projectPath.Length == 0) throw new InvalidOperationException("That zip's set file can't be unpacked.");
            }
            catch
            {
                // Don't leave a half-unpacked set behind: the folder is new, so nothing else is in it.
                try { Directory.Delete(dest, recursive: true); } catch { }
                throw;
            }
            hasFrames = zip.Entries.Any(e => e.Name.Equals("template.json", StringComparison.OrdinalIgnoreCase));
        }

        if (!hasFrames) return projectPath;
        var renamed = TemplateImporter.ImportBundlesMapped(zipPath)
            .Where(m => !string.Equals(m.Original, m.Installed, StringComparison.Ordinal))
            .GroupBy(m => m.Original, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Installed, StringComparer.OrdinalIgnoreCase);
        if (renamed.Count > 0) PointAtFrames(projectPath, renamed);
        return projectPath;
    }

    /// <summary>Renames the frames a just-unpacked set's cards use (and its house frame). A set from a newer version is
    /// left as it is: rewriting it here could drop what that version saved.</summary>
    private static void PointAtFrames(string projectPath, IReadOnlyDictionary<string, string> renamed)
    {
        var project = Models.CardProject.Load(projectPath);
        if (project.IsFromNewerVersion) return;
        string Map(string name) => renamed.TryGetValue(name ?? "", out var to) ? to : name ?? "";
        void Fix(Models.CardModel? c)
        {
            if (c == null) return;
            c.TemplateName = Map(c.TemplateName);
            c.HalfTemplateName = Map(c.HalfTemplateName);
            Fix(c.BackFace);
            Fix(c.OtherHalf);
        }
        foreach (var c in project.Cards) Fix(c);
        project.DefaultTemplate = Map(project.DefaultTemplate);
        project.Save(projectPath);
    }
}
