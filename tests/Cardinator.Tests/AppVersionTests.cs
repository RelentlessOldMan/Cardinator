using System.Text.RegularExpressions;
using Cardinator;

namespace Cardinator.Tests;

/// <summary>
/// Guards the single-source-of-truth version: the app must DERIVE its displayed version from the assembly
/// (set by &lt;Version&gt; in the csproj), never from a hardcoded literal. This caught a real drift where a
/// stale "Cardinator 1.3" literal was shown while the csproj said 1.1.3 — confusing for bug reports.
/// </summary>
public class AppVersionTests
{
    [Fact]
    public void VersionNumber_IsDerivedFromAssembly_NotHardcoded()
    {
        var asm = typeof(App).Assembly.GetName().Version!;
        Assert.Equal($"{asm.Major}.{asm.Minor}.{asm.Build}", App.VersionNumber());
    }

    /// <summary>The version the app shows is the csproj's &lt;Version&gt; — read from the csproj itself, so the test
    /// isn't just comparing the assembly with itself (an AssemblyVersion override, or a stale build, would pass that).</summary>
    [Fact]
    public void VersionNumber_IsTheCsprojVersion()
    {
        var csproj = Path.Combine(RepoRoot(), "src", "Cardinator", "Cardinator.csproj");
        var declared = System.Xml.Linq.XDocument.Load(csproj).Descendants("Version").Select(e => e.Value.Trim()).First(v => v.Length > 0);
        Assert.Equal(declared, App.VersionNumber());
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "src", "Cardinator", "Cardinator.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("repo root");
    }

    [Fact]
    public void VersionNumber_IsThreePartNumeric()
        => Assert.Matches(@"^\d+\.\d+\.\d+$", App.VersionNumber());

    [Fact]
    public void Version_IsAppNamePlusNumber()
        => Assert.Equal("Cardinator " + App.VersionNumber(), App.Version);

    [Fact]
    public void Version_StaysOnTheOnePointLine_NeverAutoMajorBumped()
        => Assert.StartsWith("1.", App.VersionNumber());   // major bump (2.0) is the user's decision alone
}
