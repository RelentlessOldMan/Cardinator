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
    /// Multi-digit numbers stay together ("10G" -> "{10}{G}"), a slash makes a hybrid or Phyrexian symbol
    /// ("W/U" -> "{W/U}", "2/W" -> "{2/W}", "G/P" -> "{G/P}"), and anything already in braces is kept as written —
    /// so a half-typed "2{G}" keeps its 2. A cost that is all braces comes back unchanged.
    /// </summary>
    public static string NormalizeCost(string? cost)
    {
        if (string.IsNullOrWhiteSpace(cost)) return "";
        var s = cost.Trim();

        var sb = new System.Text.StringBuilder();
        int i = 0;
        // One loose symbol part at i: a run of digits or a single letter (upper-cased); "" if there's none.
        string Part(ref int at)
        {
            if (at >= s.Length) return "";
            if (char.IsDigit(s[at]))
            {
                int j = at;
                while (j < s.Length && char.IsDigit(s[j])) j++;
                var digits = s[at..j];
                at = j;
                return digits;
            }
            if (char.IsLetter(s[at])) return char.ToUpperInvariant(s[at++]).ToString();
            return "";
        }
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '{')
            {
                int close = s.IndexOf('}', i + 1);
                if (close < 0) close = s.Length;   // an unclosed brace: the rest is its symbol
                var inner = s[(i + 1)..close].Trim();
                if (inner.Length > 0) sb.Append('{').Append(inner).Append('}');
                i = close + 1;
                continue;
            }
            var part = Part(ref i);
            if (part.Length == 0) { i++; continue; }   // skip spaces / punctuation
            // "W/U", "2/W", "G/P", even "G/U/P": the parts joined by slashes are one symbol.
            while (i + 1 < s.Length && s[i] == '/')
            {
                int at = i + 1;
                var next = Part(ref at);
                if (next.Length == 0) break;
                part += "/" + next;
                i = at;
            }
            sb.Append('{').Append(part).Append('}');
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
