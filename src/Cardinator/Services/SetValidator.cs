using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>
/// Runs <see cref="CardValidator"/> across a whole set and returns only the cards that have a real problem
/// (Warning or Error — Info is filtered out), so the user can check an entire project at once and jump to
/// what needs fixing instead of clicking through every card.
/// </summary>
public static class SetValidator
{
    public sealed record CardIssues(CardModel Card, IReadOnlyList<ValidationIssue> Issues);

    /// <summary>Validates every card against its resolved template spec. <paramref name="resolveSpec"/> maps a
    /// card to the spec it renders with; <paramref name="installedTemplates"/> enables the missing-frame check.
    /// Returns one entry per card that has at least one Warning/Error, in list order.</summary>
    public static IReadOnlyList<CardIssues> ValidateAll(
        IReadOnlyList<CardModel> cards,
        Func<CardModel, TemplateSpec> resolveSpec,
        IReadOnlyCollection<string>? installedTemplates = null)
    {
        var result = new List<CardIssues>();
        foreach (var card in cards)
        {
            var issues = CardValidator.Validate(card, resolveSpec(card), cards, installedTemplates)
                .Where(i => i.Severity != IssueSeverity.Info)
                .ToList();

            // A double-faced card's BACK is rendered and exported like any other face, so check it too —
            // otherwise missing back art or a bad symbol on the back passes "Check all cards" silently.
            // Issues are labelled so the user knows which side to fix. Duplicate-name/collector checks run
            // against the fronts only (the back isn't its own card in the set).
            if (card.BackFace is { } back)
            {
                var backIssues = CardValidator.Validate(back, resolveSpec(back), cards, installedTemplates)
                    .Where(i => i.Severity != IssueSeverity.Info && i.Code != "dup-name" && i.Code != "dup-collector")
                    .Select(i => new ValidationIssue(i.Severity, i.Code, "Back face: " + i.Message));
                issues.AddRange(backIssues);
            }

            // A two-part card's other half (flip or split) is printed too — check its content the same way.
            issues.AddRange(CardValidator.ValidateOtherHalf(card, resolveSpec(card))
                .Where(i => i.Severity != IssueSeverity.Info));

            if (issues.Count > 0) result.Add(new CardIssues(card, issues));
        }
        return result;
    }
}
