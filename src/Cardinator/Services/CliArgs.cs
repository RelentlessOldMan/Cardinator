using System;
using System.Collections.Generic;
using System.Linq;

namespace Cardinator.Services;

/// <summary>
/// The options each scripting command takes, checked before it runs: a mistyped option (<c>fram=Midnight</c>) was
/// silently ignored and the run reported success, and a missing path made the next <c>key=value</c> option the path
/// (<c>--renderset set.cardinator only=X</c> rendered everything into a folder called "only=X"). Both are usage
/// errors (exit 2) now.
/// </summary>
public static class CliArgs
{
    private sealed record Spec(int Positional, string[] Keys, string[] Flags);

    private static readonly Dictionary<string, Spec> Specs = new(StringComparer.Ordinal)
    {
        ["--render"] = new(2, new[] { "scale", "dpi", "bleed", "q" }, new[] { "nosym" }),
        ["--rendercards"] = new(2, new[] { "scale", "templates" }, new[] { "nosym" }),
        ["--moxfield"] = new(2, new[] { "format" }, Array.Empty<string>()),
        ["--import"] = new(2, new[] { "frame", "art", "name", "templates" }, new[] { "noart", "append" }),
        ["--renderset"] = new(2, new[] { "only", "templates" }, new[] { "sheet", "a4", "nosym" }),
        ["--restyle"] = new(1, new[] { "frame", "art", "titles", "setcode", "number", "symbol", "only", "templates" }, Array.Empty<string>()),
        ["--info"] = new(1, new[] { "templates" }, Array.Empty<string>()),
        ["--listframes"] = new(0, new[] { "templates" }, Array.Empty<string>()),
    };

    /// <summary>What's wrong with <paramref name="args"/> (the whole command line, the command first), or null when
    /// the command isn't one checked here or its arguments are fine.</summary>
    public static string? Problem(string[] args)
    {
        if (args.Length == 0 || !Specs.TryGetValue(args[0], out var spec)) return null;
        string cmd = args[0];
        bool IsKey(string a) => a.IndexOf('=') is > 0 and var i && spec.Keys.Contains(a[..i], StringComparer.OrdinalIgnoreCase);
        bool IsFlag(string a) => spec.Flags.Contains(a, StringComparer.OrdinalIgnoreCase);

        for (int i = 1; i <= spec.Positional && i < args.Length; i++)
            if (IsKey(args[i]) || IsFlag(args[i]))
                return $"{cmd}: “{args[i]}” is an option, but a path belongs there — a path is missing. See docs/CLI.md.";
        foreach (var a in args.Skip(1 + spec.Positional))
            if (!IsKey(a) && !IsFlag(a))
                return $"{cmd}: unknown option “{a}”." + (spec.Keys.Length + spec.Flags.Length == 0
                    ? $" {cmd} takes no options."
                    : $" It takes: {string.Join(", ", spec.Keys.Select(k => k + "=").Concat(spec.Flags))}.");
        return null;
    }
}
