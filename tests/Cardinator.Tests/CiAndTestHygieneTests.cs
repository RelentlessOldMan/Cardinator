using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// Review 1, Tier 3 (1.6.16): the shipped sample frames reach users on an upgrade without ever overwriting a frame
/// they changed. 1.6.18: a window test lets go of its window, so the suite stays small enough for a CI runner. In the
/// STA-window collection because it re-runs the one-per-session extraction and builds a window.
/// </summary>
[Collection("STAWindows")]
public class CiAndTestHygieneTests
{
    private const string Rel = "pcc_sealed_gate/template.json";

    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    private static (string file, string manifest) Paths()
    {
        SampleTemplates.EnsureExtracted();
        return (Path.Combine(AppPaths.TemplatesDir, "pcc_sealed_gate", "template.json"),
                Path.Combine(AppPaths.TemplatesDir, SampleTemplates.ManifestName));
    }

    private static Dictionary<string, string> Manifest(string path)
        => File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))! : new();

    [Fact]
    public void AnUpgrade_RefreshesASampleFrameAnEarlierVersionWrote()
    {
        var (file, manifestPath) = Paths();
        var shipped = File.ReadAllBytes(file);
        try
        {
            // As an earlier version left it: different bytes, recorded as written by the app.
            var old = Encoding.UTF8.GetBytes("{ \"name\": \"Sealed Gate (an older version)\" }");
            File.WriteAllBytes(file, old);
            var m = Manifest(manifestPath);
            m[Rel] = Hash(old);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(m));

            SampleTemplates.ResetForTests();
            SampleTemplates.EnsureExtracted();
            Assert.Equal(Hash(shipped), Hash(File.ReadAllBytes(file)));
            Assert.Equal(Hash(shipped), Manifest(manifestPath)[Rel]);
        }
        finally { File.WriteAllBytes(file, shipped); }
    }

    [Fact]
    public void AWindowTest_LetsGoOfItsWindow_WhenItEnds()
    {
        WeakReference? window = null;
        Exception? failed = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = System.Windows.Application.Current ?? new System.Windows.Application();
                if (app.Resources.MergedDictionaries.Count == 0)
                    app.Resources.MergedDictionaries.Add((System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(
                        new Uri("/Cardinator;component/Theme.xaml", UriKind.Relative)));
                window = new WeakReference(new Cardinator.MainWindow { SuppressClosePrompt = true });
            }
            catch (Exception ex) { failed = ex; }
            finally { TestHelpers.EndUiThread(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failed != null) throw failed;

        for (int i = 0; i < 5 && window!.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Assert.False(window!.IsAlive, "the window outlived its test — every window test would keep ~200 MB for the whole run");
    }

    [Fact]
    public void AnUpgrade_NeverOverwritesASampleFrameTheUserChanged()
    {
        var (file, manifestPath) = Paths();
        var shipped = File.ReadAllBytes(file);
        try
        {
            Assert.Equal(Hash(shipped), Manifest(manifestPath)[Rel]);   // recorded as ours on extraction
            var mine = Encoding.UTF8.GetBytes("{ \"name\": \"Sealed Gate\", \"note\": \"my own tweaks\" }");
            File.WriteAllBytes(file, mine);   // edited by the user since

            SampleTemplates.ResetForTests();
            SampleTemplates.EnsureExtracted();
            Assert.Equal(Hash(mine), Hash(File.ReadAllBytes(file)));
        }
        finally { File.WriteAllBytes(file, shipped); }
    }
}
