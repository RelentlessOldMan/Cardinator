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
            if (issues.Count > 0) result.Add(new CardIssues(card, issues));
        }
        return result;
    }
}
