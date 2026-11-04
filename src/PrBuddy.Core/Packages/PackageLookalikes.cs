using System.Reflection;

namespace PrBuddy.Core.Packages;

/// <summary>
/// Spots package names one keystroke away from a popular package (Newtonsoft.Jsonn, Serilg),
/// the shape of both typos and typosquatting. Works offline from a list shipped with the tool;
/// refresh it with tools/Update-PopularPackages.ps1.
/// </summary>
public static class PackageLookalikes
{
    /// <summary>Below this length one edit is too big a change to call a typo (Moq vs Mox).</summary>
    const int MinLength = 6;

    static readonly Lazy<IReadOnlyList<string>> Popular = new(Load);
    static readonly Lazy<HashSet<string>> PopularSet = new(() => Popular.Value.ToHashSet(StringComparer.OrdinalIgnoreCase));

    public static IReadOnlyList<string> PopularPackages => Popular.Value;

    /// <summary>The popular package <paramref name="id"/> looks like a typo of, or null.</summary>
    public static string? LookalikeOf(string id) => LookalikeOf(id, Popular.Value, PopularSet.Value);

    public static string? LookalikeOf(string id, IReadOnlyList<string> popular) =>
        LookalikeOf(id, popular, popular.ToHashSet(StringComparer.OrdinalIgnoreCase));

    static string? LookalikeOf(string id, IReadOnlyList<string> popular, HashSet<string> popularSet)
    {
        if (id.Length < MinLength || popularSet.Contains(id)) return null;
        var lower = id.ToLowerInvariant();
        var plain = Plain(lower);

        foreach (var candidate in popular)
        {
            if (candidate.Length < MinLength || Math.Abs(candidate.Length - id.Length) > 2) continue;
            var other = candidate.ToLowerInvariant();
            if (lower[0] != other[0]) continue; // xunit/bunit/TUnit, NUnit/JUnit: different brands, not typos
            if (Plain(other) == plain) return candidate; // Newtonsoft-Json, NewtonsoftJson, Newt0nsoft.Json
            if (Distance(lower, other) == 1 && !DigitsOnly(lower, other) && !InShortSegment(lower, other)) return candidate;
        }
        return null;
    }

    /// <summary>The names match once digits are removed: win7-x64 vs win-x64, NUnitTestAdapter vs NUnit3TestAdapter.</summary>
    static bool DigitsOnly(string a, string b) => OnlyDigitsDiffer(a, b) || NoDigits(a) == NoDigits(b);

    static string NoDigits(string s) => string.Concat(s.Where(c => !char.IsDigit(c)));

    /// <summary>The one edit is inside a short dotted segment, like a culture code (Humanizer.Core.lt vs .pt).</summary>
    static bool InShortSegment(string a, string b)
    {
        var sa = a.Split('.');
        var sb = b.Split('.');
        if (sa.Length != sb.Length) return false;
        var differing = Enumerable.Range(0, sa.Length).Where(i => sa[i] != sb[i]).ToList();
        return differing.Count == 1 && sa[differing[0]].Length <= 3 && sb[differing[0]].Length <= 3;
    }

    /// <summary>Lowercase, separators dropped, lookalike characters folded together.</summary>
    static string Plain(string lower) => string.Concat(lower
        .Where(c => c is not ('.' or '-' or '_'))
        .Select(c => c switch { '0' => 'o', '1' => 'l', '|' => 'l', _ => c }));

    /// <summary>xunit.v3 vs xunit.v2, Net6 vs Net8: different packages, not typos.</summary>
    static bool OnlyDigitsDiffer(string a, string b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i] && !(char.IsDigit(a[i]) && char.IsDigit(b[i]))) return false;
        return true;
    }

    /// <summary>Optimal string alignment distance: insertions, deletions, substitutions and adjacent swaps.</summary>
    static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }
        return d[a.Length, b.Length];
    }

    static IReadOnlyList<string> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PrBuddy.popular-packages.txt")
            ?? throw new InvalidOperationException("popular-packages.txt isn't embedded in PrBuddy.Core.");
        using var reader = new StreamReader(stream);
        var ids = new List<string>();
        while (reader.ReadLine() is { } line)
            if (line.Length > 0 && !line.StartsWith('#')) ids.Add(line.Trim());
        return ids;
    }
}
