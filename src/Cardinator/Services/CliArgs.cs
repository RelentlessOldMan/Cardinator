using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Cardinator.Services;

/// <summary>
/// The arguments each command-line mode takes, checked before it runs: a mistyped option (<c>fram=Midnight</c>,
/// <c>--frames out biultin</c>, <c>--qa out ful</c>) was silently ignored or ran a different mode and reported
/// success, a missing path made the next <c>key=value</c> option the path (<c>--renderset set.cardinator only=X</c>
/// rendered everything into a folder called "only=X"), an empty value (<c>only=</c> from an unset script variable)
/// meant "all cards", and a number that didn't parse (<c>scale=abc</c>) quietly became the default. All of those are
/// usage errors (exit 2) now.
/// </summary>
public static class CliArgs
{
    /// <param name="MinPositional">Paths/values that must come first.</param>
    /// <param name="MaxPositional">With the optional ones that may follow them (an art folder, a word mark…).</param>
    private sealed record Spec(int MinPositional, int MaxPositional, string[] Keys, string[] Flags);

    private static readonly string[] None = Array.Empty<string>();

    private static readonly Dictionary<string, Spec> Specs = new(StringComparer.Ordinal)
    {
        ["--selftest"] = new(0, 1, None, None),
        ["--lookup"] = new(1, 2, None, None),
        ["--render"] = new(2, 2, new[] { "scale", "dpi", "bleed", "q" }, new[] { "nosym" }),
        ["--rendercards"] = new(2, 2, new[] { "scale", "templates" }, new[] { "nosym" }),
        ["--search"] = new(2, 3, new[] { "max" }, new[] { "sheet", "a4" }),
        ["--cardback"] = new(1, 2, None, None),
        ["--newtemplate"] = new(2, 2, None, new[] { "fullart" }),
        ["--frames"] = new(1, 1, None, new[] { "nosym", "builtin" }),
        ["--permute"] = new(2, 2, None, None),
        ["--batch"] = new(2, 3, None, None),
        ["--sheet"] = new(2, 3, None, new[] { "a4", "letter" }),
        ["--moxfield"] = new(2, 2, new[] { "format" }, None),
        ["--import"] = new(2, 2, new[] { "frame", "art", "name", "templates" }, new[] { "noart", "append" }),
        ["--renderset"] = new(2, 2, new[] { "only", "templates" }, new[] { "sheet", "a4", "nosym" }),
        ["--restyle"] = new(1, 1, new[] { "frame", "art", "titles", "setcode", "number", "symbol", "only", "templates" }, None),
        ["--info"] = new(1, 1, new[] { "templates" }, None),
        ["--listframes"] = new(0, 0, new[] { "templates" }, None),
        ["--docs"] = new(1, 1, None, None),
        ["--uishot"] = new(1, 1, None, new[] { "layouts" }),
        ["--appshot"] = new(3, 4, None, None),
        ["--editframe"] = new(1, 2, None, None),
    };

    /// <summary>Whole-number options and the values they take.</summary>
    private static readonly Dictionary<string, (int Min, int Max)> Numbers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["scale"] = (1, 6), ["dpi"] = (1, 2400), ["bleed"] = (0, 300), ["q"] = (1, 100), ["max"] = (1, 500),
    };

    /// <summary>What's wrong with <paramref name="args"/> (the whole command line, the command first), or null when
    /// the command isn't one checked here or its arguments are fine.</summary>
    public static string? Problem(string[] args)
    {
        if (args.Length == 0) return null;
        string cmd = args[0];
        if (cmd == "--qa") return QaProblem(args);
        if (!Specs.TryGetValue(cmd, out var spec)) return null;
        bool IsKey(string a) => a.IndexOf('=') is > 0 and var i && spec.Keys.Contains(a[..i], StringComparer.OrdinalIgnoreCase);
        bool IsFlag(string a) => spec.Flags.Contains(a, StringComparer.OrdinalIgnoreCase);
        bool LooksLikeAnOption(string a) => IsKey(a) || IsFlag(a) || a.StartsWith("--", StringComparison.Ordinal);

        for (int i = 1; i <= spec.MinPositional; i++)
        {
            if (i >= args.Length) return $"{cmd}: missing arguments — see docs/CLI.md.";
            if (LooksLikeAnOption(args[i]))
                return $"{cmd}: “{args[i]}” is an option, but a path belongs there — a path is missing. See docs/CLI.md.";
        }
        int next = 1 + spec.MinPositional;
        while (next < args.Length && next <= spec.MaxPositional && !LooksLikeAnOption(args[next])) next++;   // optional ones

        // --search's bare count ("--search q out 20") is the old spelling of max=20.
        if (cmd == "--search" && args.Length > 3 && !LooksLikeAnOption(args[3]) && NumberProblem("max", args[3]) is { } badCount)
            return $"{cmd}: {badCount}";

        foreach (var a in args.Skip(next))
        {
            if (IsFlag(a)) continue;
            if (!IsKey(a))
                return $"{cmd}: unknown option “{a}”." + (spec.Keys.Length + spec.Flags.Length == 0
                    ? $" {cmd} takes no options."
                    : $" It takes: {string.Join(", ", spec.Keys.Select(k => k + "=").Concat(spec.Flags))}.");
            int eq = a.IndexOf('=');
            string key = a[..eq], value = a[(eq + 1)..].Trim().Trim('"').Trim();
            if (value.Length == 0)
                return $"{cmd}: “{a}” has no value — leave the option out, or give it one.";
            if (Numbers.ContainsKey(key) && NumberProblem(key, value) is { } bad) return $"{cmd}: {bad}";
        }
        return null;
    }

    private static string? NumberProblem(string key, string value)
    {
        var (min, max) = Numbers[key];
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max
            ? null
            : $"{key}= takes a whole number from {min} to {max}, not “{value}”.";
    }

    /// <summary><c>--qa &lt;outDir&gt; [full [&lt;case&gt;] [cards]]</c>: anything after the folder but <c>full</c> was ignored
    /// and the basic QA ran instead (<c>--qa out ful</c>).</summary>
    private static string? QaProblem(string[] args)
    {
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal)) return "--qa: missing the output folder — see docs/CLI.md.";
        if (args.Length == 2) return null;
        if (args[2] != "full") return $"--qa: unknown option “{args[2]}” — only “full” can follow the folder.";
        var rest = args.Skip(3).ToList();
        int cases = rest.Count(a => a != "cards");
        if (cases > 1) return "--qa: give at most one case name after full (and, if wanted, cards).";
        return rest.Count(a => a == "cards") > 1 ? "--qa: cards given twice." : null;
    }
}
