using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>
/// Combines the two kinds of live card checks shown in the CHECKS panel: the rule-based
/// <see cref="CardValidator"/> (fields, symbols, geometry) and the pixel-based <see cref="RenderInspector"/>
/// (art window rendered blank, a missing/too-thin border) — the latter catches visual problems a corrupt
/// but still-present art file produces, which geometry alone can't see (M5).
/// </summary>
public static class LiveChecks
{
    /// <summary>Merges rule and pixel issues, de-duplicated by (Code, Message) so the same problem reported
    /// by both passes shows once. Order is preserved: rule issues first, then any new pixel issues.</summary>
    public static IReadOnlyList<ValidationIssue> Merge(
        IEnumerable<ValidationIssue> ruleIssues, IEnumerable<ValidationIssue> pixelIssues)
    {
        var seen = new HashSet<string>();
        var merged = new List<ValidationIssue>();
        foreach (var i in ruleIssues.Concat(pixelIssues))
            if (seen.Add(i.Code + "" + i.Message))
                merged.Add(i);
        return merged;
    }
}
