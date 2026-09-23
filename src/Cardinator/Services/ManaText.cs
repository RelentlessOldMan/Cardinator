using System.Text.RegularExpressions;

namespace Cardinator.Services;

/// <summary>A piece of card text: either a literal text run or a symbol token like {R} or {T}.</summary>
public readonly record struct TextToken(string Value, bool IsSymbol);

/// <summary>Splits card text into literal runs and {symbol} tokens.</summary>
public static partial class ManaText
{
    [GeneratedRegex(@"\{[^{}]+\}")]
    private static partial Regex SymbolRegex();

    public static IReadOnlyList<TextToken> Tokenize(string? text)
    {
        var result = new List<TextToken>();
        if (string.IsNullOrEmpty(text)) return result;

        int pos = 0;
        foreach (Match m in SymbolRegex().Matches(text))
        {
            if (m.Index > pos)
                result.Add(new TextToken(text[pos..m.Index], false));
            result.Add(new TextToken(m.Value, true));
            pos = m.Index + m.Length;
        }
        if (pos < text.Length)
            result.Add(new TextToken(text[pos..], false));

        return result;
    }

    /// <summary>
    /// Normalizes a loosely-typed mana cost into brace tokens: "2RW" or "2 r w" -> "{2}{R}{W}".
    /// Multi-digit numbers stay together ("10G" -> "{10}{G}"). If the input already uses braces
    /// it's returned unchanged (assumed already tokenized).
    /// </summary>
    public static string NormalizeCost(string? cost)
    {
        if (string.IsNullOrWhiteSpace(cost)) return "";
        var s = cost.Trim();
        if (s.Contains('{')) return s;

        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsDigit(c))
            {
                int j = i;
                while (j < s.Length && char.IsDigit(s[j])) j++;
                sb.Append('{').Append(s[i..j]).Append('}');
                i = j;
            }
            else if (char.IsLetter(c))
            {
                sb.Append('{').Append(char.ToUpperInvariant(c)).Append('}');
                i++;
            }
            else i++;   // skip spaces / punctuation
        }
        return sb.ToString();
    }

    /// <summary>All distinct {symbol} tokens found across the given texts.</summary>
    public static IEnumerable<string> SymbolTokens(params string?[] texts)
        => texts.Where(t => !string.IsNullOrEmpty(t))
                .SelectMany(t => Tokenize(t!))
                .Where(tok => tok.IsSymbol)
                .Select(tok => tok.Value)
                .Distinct();
}
