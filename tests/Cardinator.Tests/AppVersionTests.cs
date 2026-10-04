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
