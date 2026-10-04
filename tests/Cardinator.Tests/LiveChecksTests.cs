using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>M5: merging rule-based and pixel-based checks for the live CHECKS panel.</summary>
public class LiveChecksTests
{
    private static ValidationIssue I(string code, string msg) => new(IssueSeverity.Error, code, msg);

    [Fact]
    public void Merge_KeepsRuleThenPixel_InOrder()
    {
        var rule = new[] { I("a", "Alpha"), I("b", "Bravo") };
        var pixel = new[] { I("art-blank", "Art is blank") };

        var merged = LiveChecks.Merge(rule, pixel);

        Assert.Equal(3, merged.Count);
        Assert.Equal("a", merged[0].Code);
        Assert.Equal("b", merged[1].Code);
        Assert.Equal("art-blank", merged[2].Code);
    }

    [Fact]
    public void Merge_DedupesSameCodeAndMessage()
    {
        var rule = new[] { I("art-blank", "Art window is blank") };
        var pixel = new[] { I("art-blank", "Art window is blank") };   // exact dup from both passes

        var merged = LiveChecks.Merge(rule, pixel);

        Assert.Single(merged);
    }

    [Fact]
    public void Merge_KeepsSameCodeWithDifferentMessages()
    {
        // e.g. border-thin reported for two different sides must both survive.
        var pixel = new[] { I("border-thin", "top too thin"), I("border-thin", "bottom too thin") };

        var merged = LiveChecks.Merge(System.Array.Empty<ValidationIssue>(), pixel);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merge_HandlesEmpties()
    {
        Assert.Empty(LiveChecks.Merge(System.Array.Empty<ValidationIssue>(), System.Array.Empty<ValidationIssue>()));
    }
}
