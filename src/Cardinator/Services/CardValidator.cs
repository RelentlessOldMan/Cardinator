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
        @"(?:W|U|B|R|G|C)/P)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Validate one card against its resolved template. Pass the other cards in the project to
    /// enable cross-card checks (duplicate collector numbers).</summary>
    public static IReadOnlyList<ValidationIssue> Validate(
        CardModel card, TemplateSpec templateSpec, IReadOnlyCollection<CardModel>? project = null)
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

        // Unknown mana symbols in the cost or rules text.
        foreach (var (val, field) in SymbolTokens(card))
            if (!KnownSymbol.IsMatch(val))
                issues.Add(new(IssueSeverity.Warning, "bad-symbol", $"Unrecognized symbol {{{val}}} (will render as a plain pip).", field));

        // Creatures should have both power and toughness (one alone reads as a mistake).
        bool hasP = !string.IsNullOrWhiteSpace(card.Power), hasT = !string.IsNullOrWhiteSpace(card.Toughness);
        if (hasP ^ hasT)
            issues.Add(new(IssueSeverity.Warning, "half-pt", "Only one of power/toughness is set.", nameof(card.Power)));

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
        if (project != null && !string.IsNullOrWhiteSpace(card.CollectorNumber))
        {
            int dupes = project.Count(c => !ReferenceEquals(c, card)
                && string.Equals((c.CollectorNumber ?? "").Trim(), card.CollectorNumber.Trim(), System.StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(c.CollectorNumber));
            if (dupes > 0)
                issues.Add(new(IssueSeverity.Warning, "dup-collector", $"Collector number '{card.CollectorNumber}' is used by {dupes + 1} cards.", nameof(card.CollectorNumber)));
        }

        return issues;
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
