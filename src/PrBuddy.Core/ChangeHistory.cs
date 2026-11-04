namespace PrBuddy.Core;

/// <summary>Which C# files changed together in recent commits before the PR. Used to tell strays from usual partners.</summary>
public sealed class ChangeHistory(IReadOnlyList<IReadOnlySet<string>> commits)
{
    public static ChangeHistory Empty { get; } = new([]);

    /// <summary>One entry per past commit: the C# paths it changed.</summary>
    public IReadOnlyList<IReadOnlySet<string>> Commits { get; } = commits;

    /// <summary>
    /// How many different other areas this one has changed alongside. A high number means a shared
    /// library that features routinely touch, even brand-new features with no history of their own.
    /// </summary>
    public int DistinctPartners(string area, Func<string, string> areaOf) =>
        Commits
            .Select(paths => paths.Select(areaOf).ToHashSet(StringComparer.Ordinal))
            .Where(areas => areas.Contains(area))
            .SelectMany(areas => areas)
            .Where(other => other != area)
            .Distinct(StringComparer.Ordinal)
            .Count();

    /// <summary>How many past commits changed files in both areas.</summary>
    public int CoChanges(string areaA, string areaB, Func<string, string> areaOf) =>
        Commits.Count(paths =>
        {
            var areas = paths.Select(areaOf).ToHashSet(StringComparer.Ordinal);
            return areas.Contains(areaA) && areas.Contains(areaB);
        });
}
