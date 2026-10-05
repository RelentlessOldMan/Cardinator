using System.IO;
using System.Text.RegularExpressions;
using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>How serious a validation finding is.</summary>
public enum IssueSeverity { Info, Warning, Error }

/// <summary>A single validation finding, targeted at a card (and optionally a field), so the UI and the
/// batch report can link straight to what needs fixing.</summary>
public sealed record ValidationIssue(IssueSeverity Severity, string Code, string Message, string? Field = null)
{
    public override string ToString() => $"[{Severity}] {Message}";
}

/// <summary>
/// Model- and geometry-level card validation (no rendering required). Detects the classes of problem we
/// otherwise only catch by eyeballing every card: missing/broken art, unknown mana symbols, text regions
/// that fall under the black border, and a footer that overlaps the description panel. Pixel-level checks
/// (border evenness, empty art window) live in <see cref="RenderInspector"/>.
/// </summary>
public static class CardValidator
{
    // Known Scryfall symbol tokens (inner text, no braces): mono, generic, variable, tap/energy,
    // hybrid and Phyrexian. Used to flag typos like {Q1} or {G/Z} that would fall back to a plain pip.
    private static readonly Regex KnownSymbol = new(
        @"^(?:\d+|[WUBRGCS]|[XYZ]|T|Q|E|P|CHAOS|½|∞|" +
        @"(?:2|W|U|B|R|G|C)/(?:W|U|B|R|G|C|P)|" +
        @"(?:W|U|B|R|G|C)/(?:W|U|B|R|G|C)/P|" +   // two-colour Phyrexian, e.g. {G/U/P} (Tamiyo, Compleated)
        @"H[WUBRG]|" +                            // half symbols, e.g. {HW} (Unhinged)
        @"(?:W|U|B|R|G|C)/P)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Checks a two-part card's other half — the content checks that apply to it (name, symbols,
    /// power/toughness; and for a split half, which has its own, its art), labelled so the user knows which half
    /// to fix. Frame and set-level checks belong to the card itself (the half shares them), so they're skipped,
    /// and so is the art of a flip half (it shares the card's). Empty for a card without one.</summary>
    public static IReadOnlyList<ValidationIssue> ValidateOtherHalf(CardModel card, TemplateSpec templateSpec)
    {
        if (card.OtherHalf is not { } half) return System.Array.Empty<ValidationIssue>();
        var skip = new HashSet<string> { "symbol-missing", "symbol-badpath",
            "frame-missing", "dup-name", "dup-collector", "portrait-only-frame", "no-flip-frame",
            "out-of-bounds", "footer-overlap" };
        if (!card.IsSplit) skip.UnionWith(new[] { "no-art", "art-missing", "art-badpath" });
        string label = card.IsSplit ? "Other half: " : "Flipped half: ";
        return Validate(half, templateSpec)
            .Where(i => !skip.Contains(i.Code))
            .Select(i => new ValidationIssue(i.Severity, i.Code, label + i.Message, i.Field))
            .ToList();
    }

    /// <summary>Validate one card against its resolved template. Pass the other cards in the project to
    /// enable cross-card checks (duplicate collector numbers), and the installed template names to flag a
    /// card whose frame isn't installed (a set shared without its custom frames).</summary>
    public static IReadOnlyList<ValidationIssue> Validate(
        CardModel card, TemplateSpec templateSpec, IReadOnlyCollection<CardModel>? project = null,
        IReadOnlyCollection<string>? installedTemplates = null)
    {
        var issues = new List<ValidationIssue>();
        var spec = templateSpec.WithSubBorderApplied();   // same regions the renderer actually draws

        // --- content -------------------------------------------------------
        if (string.IsNullOrWhiteSpace(card.Name))
            issues.Add(new(IssueSeverity.Warning, "no-name", "Card has no name.", nameof(card.Name)));

        // Artwork: distinguish "none set" (info) from "set but the file is gone" (error).
        if (string.IsNullOrWhiteSpace(card.ArtPath))
            issues.Add(new(IssueSeverity.Info, "no-art", "No artwork assigned.", nameof(card.ArtPath)));
        else
        {
            try { if (!File.Exists(Path.GetFullPath(card.ArtPath))) issues.Add(new(IssueSeverity.Error, "art-missing", $"Artwork file not found: {card.ArtPath}", nameof(card.ArtPath))); }
            catch { issues.Add(new(IssueSeverity.Error, "art-badpath", $"Artwork path is invalid: {card.ArtPath}", nameof(card.ArtPath))); }
        }

        // Set symbol: if one is assigned but the file is gone, the card silently falls back to a drawn pip.
        if (!string.IsNullOrWhiteSpace(card.SetSymbolPath))
        {
            try { if (!File.Exists(Path.GetFullPath(card.SetSymbolPath))) issues.Add(new(IssueSeverity.Warning, "symbol-missing", $"Set-symbol image not found: {card.SetSymbolPath}", nameof(card.SetSymbolPath))); }
            catch { issues.Add(new(IssueSeverity.Warning, "symbol-badpath", $"Set-symbol path is invalid: {card.SetSymbolPath}", nameof(card.SetSymbolPath))); }
        }

        // The frame must still be installed — a set shared/moved without its custom frames would otherwise
        // render with an arbitrary substitute and no warning.
        if (installedTemplates != null && !string.IsNullOrWhiteSpace(card.TemplateName)
            && !installedTemplates.Contains(card.TemplateName))
            issues.Add(new(IssueSeverity.Error, "frame-missing", $"Frame \"{card.TemplateName}\" isn't installed — the card will render with a substitute frame.", nameof(card.TemplateName)));

        // Unknown mana symbols in the cost or rules text.
        foreach (var (val, field) in SymbolTokens(card))
            if (!KnownSymbol.IsMatch(val))
                issues.Add(new(IssueSeverity.Warning, "bad-symbol", $"Unrecognized symbol {{{val}}} (will render as a plain pip).", field));

        // Creatures should have both power and toughness (one alone reads as a mistake).
        bool hasP = !string.IsNullOrWhiteSpace(card.Power), hasT = !string.IsNullOrWhiteSpace(card.Toughness);
        if (hasP ^ hasT)
            issues.Add(new(IssueSeverity.Warning, "half-pt", "Only one of power/toughness is set.", nameof(card.Power)));

        // A loyalty value turns ANY card into a planeswalker layout (hiding the power/toughness box). Flag a
        // stray loyalty on a non-planeswalker, which otherwise silently hides P/T.
        bool typeIsPw = (card.TypeLine ?? "").Contains("Planeswalker", System.StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(card.Loyalty) && !typeIsPw)
            issues.Add(new(IssueSeverity.Warning, "loyalty-nonplaneswalker",
                "This card has a loyalty value but isn't a Planeswalker — it renders as one and hides power/toughness.",
                nameof(card.Loyalty)));

        // A Battle/Plane renders sideways by re-laying-out its frame — but an imported frame is a fixed image,
        // so it can't be turned. Say so, rather than leave the user wondering why it stayed upright.
        if (card.WantsLandscape && !templateSpec.IsLandscape && templateSpec.CustomFrame)
            issues.Add(new(IssueSeverity.Info, "portrait-only-frame",
                "This card is normally printed sideways, but this frame is an imported image that can only be "
                + "used upright. Pick a built-in frame for the sideways layout.",
                nameof(card.TemplateName)));

        // A flip card needs a frame that can be laid out as one. Built-in frames always can; an imported
        // picture frame only if it ships a flip version — otherwise only the top half shows.
        if (card.IsFlip && !templateSpec.IsFlipLayout)
            issues.Add(new(IssueSeverity.Warning, "no-flip-frame",
                "This is a flip card, but this frame is an imported image without a flip version, so only the "
                + "upright half is shown. Pick a built-in frame (or one that comes with a flip layout).",
                nameof(card.TemplateName)));

        // Special layouts build their badges by parsing the rules-text syntax; warn when the syntax produced
        // none, so a mistyped planeswalker/saga/class doesn't silently render without its badges. Uses the
        // renderer's OWN parsers so this never drifts from what actually draws.
        if (!string.IsNullOrWhiteSpace(card.RulesText))
        {
            if (typeIsPw && !CardRenderer.ParseAbilities(card.RulesText).Any(r => r.cost != null))
                issues.Add(new(IssueSeverity.Warning, "pw-no-abilities",
                    "Planeswalker abilities need a loyalty cost like \"+1:\" or \"-3:\" — none were found, so no loyalty badges will show.", nameof(card.RulesText)));
            else if (card.IsSaga && !CardRenderer.ParseChapters(card.RulesText).Any(r => r.cost != null))
                issues.Add(new(IssueSeverity.Warning, "saga-no-chapters",
                    "Saga chapters need markers like \"I —\" or \"I, II —\" — none were found, so no chapter badges will show.", nameof(card.RulesText)));
            else if (card.IsClass && !CardRenderer.ParseClassLevels(card.RulesText).Any(r => r.cost != null))
                issues.Add(new(IssueSeverity.Info, "class-no-levels",
                    "Class level-ups use \"{cost}: Level N\" lines — none were found, so every ability shows at the base level.", nameof(card.RulesText)));
        }

        // --- geometry ------------------------------------------------------
        double W = spec.CanvasWidth, H = spec.CanvasHeight;
        double b = System.Math.Max(0, spec.BorderThickness);

        // Text regions must sit inside the printable area (inside the black border). The art window is
        // allowed to run to the edges (it sits under the border on full-art layouts), so it's exempt.
        void Bounds(string name, Region? r)
        {
            if (r == null || (r.W <= 0 && r.H <= 0)) return;
            if (r.X < b - 0.5 || r.Y < b - 0.5 || r.Right > W - b + 0.5 || r.Bottom > H - b + 0.5)
                issues.Add(new(IssueSeverity.Warning, "out-of-bounds", $"The {name} region extends under/over the card border.", name));
        }
        Bounds("title", spec.TitleBar);
        Bounds("type line", spec.TypeBar);
        Bounds("rules text", spec.EffectiveTextBox);

        // Footer overlap: when the footer sits on the frame, it must be below the description panel — not
        // painted on top of it (the "saga footer over the panel" bug).
        var fp = (spec.FooterPlacement ?? "frame").Trim().ToLowerInvariant();
        if (fp == "frame" && spec.CreditBar != null && spec.EffectiveTextBox != null
            && spec.CreditBar.Y < spec.EffectiveTextBox.Bottom - 0.5
            && spec.CreditBar.Right > spec.EffectiveTextBox.X && spec.CreditBar.X < spec.EffectiveTextBox.Right)
            issues.Add(new(IssueSeverity.Warning, "footer-overlap", "The footer overlaps the description panel.", "footer"));

        // --- project-level -------------------------------------------------
        // Compare on the NORMALIZED number so mixed styles still collide: "5", "005" and "005/20" are the
        // same card #5 (otherwise a mix of hand- and auto-numbered cards silently hides real duplicates).
        if (project != null && !string.IsNullOrWhiteSpace(card.CollectorNumber))
        {
            var key = NormalizeCollector(card.CollectorNumber);
            int dupes = project.Count(c => !ReferenceEquals(c, card)
                && !string.IsNullOrWhiteSpace(c.CollectorNumber)
                && NormalizeCollector(c.CollectorNumber) == key);
            if (dupes > 0)
                issues.Add(new(IssueSeverity.Warning, "dup-collector", $"Collector number '{card.CollectorNumber}' is used by {dupes + 1} cards.", nameof(card.CollectorNumber)));
        }

        // Two cards with the same name usually mean an accidental double-import or duplicate (info, not an
        // error — legitimate for tokens/basics, so it's gentle).
        if (project != null && !string.IsNullOrWhiteSpace(card.Name))
        {
            int same = project.Count(c => !ReferenceEquals(c, card)
                && string.Equals((c.Name ?? "").Trim(), card.Name.Trim(), System.StringComparison.OrdinalIgnoreCase));
            if (same > 0)
                issues.Add(new(IssueSeverity.Info, "dup-name", $"{same + 1} cards share the name \"{card.Name}\".", nameof(card.Name)));
        }

        return issues;
    }

    /// <summary>The comparable identity of a collector number: the part before any "/N", with a leading
    /// zero-run stripped and lowercased — so "5", "005" and "005/20" all compare equal, while non-numeric
    /// values (promos like "★") compare by their trimmed text.</summary>
    internal static string NormalizeCollector(string? collector)
    {
        var s = (collector ?? "").Trim();
        if (s.Length == 0) return "";
        var head = s.Split('/')[0].Trim();
        var m = Regex.Match(head, @"^0*(\d+)$");
        return m.Success ? m.Groups[1].Value : head.ToLowerInvariant();
    }

    /// <summary>Every symbol token in the card's mana cost and rules/loyalty text, tagged with its field.</summary>
    private static IEnumerable<(string value, string field)> SymbolTokens(CardModel card)
    {
        // Token values include the braces (e.g. "{2}") — strip them for the known-symbol check.
        foreach (var t in ManaText.Tokenize(ManaText.NormalizeCost(card.ManaCost)))
            if (t.IsSymbol) yield return (t.Value.Trim('{', '}'), nameof(card.ManaCost));
        foreach (var t in ManaText.Tokenize(card.RulesText ?? ""))
            if (t.IsSymbol) yield return (t.Value.Trim('{', '}'), nameof(card.RulesText));
    }
}
