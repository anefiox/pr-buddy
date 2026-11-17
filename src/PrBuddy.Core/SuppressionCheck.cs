// prbuddy-ignore-file PRB033: this code analyses pull requests, so "the PR" is its subject, not history
using PrBuddy.Core.Rules;

namespace PrBuddy.Core;

/// <summary>
/// Applies prbuddy-ignore comments to the findings, and reports the comments themselves when they
/// don't work: no reason, an id that isn't a rule, or (for comments this PR adds) nothing to suppress.
/// </summary>
public static class SuppressionCheck
{
    public static RuleDescriptor Descriptor { get; } = new("PRB900", "Broken or unused prbuddy-ignore comment", Severity.Warning, "Suppressions");

    public static (List<Finding> Kept, List<SuppressedFinding> Suppressed, List<Finding> Problems) Apply(
        IReadOnlyList<AnalyzedFile> files, IReadOnlyList<Finding> findings, IReadOnlySet<string> knownIds)
    {
        var kept = new List<Finding>();
        var suppressed = new List<SuppressedFinding>();
        var problems = new List<Finding>();

        var byPath = files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var suppressionsByPath = new Dictionary<string, IReadOnlyList<Suppression>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var all = Suppression.Parse(file.File.NewContent);
            var usable = new List<Suppression>();
            foreach (var s in all)
            {
                var unknown = s.RuleIds.Where(id => !knownIds.Contains(id)).ToList();
                if (s.Reason is null)
                    problems.Add(Problem(file.Path, s.Line, $"prbuddy-ignore {string.Join(", ", s.RuleIds)} has no reason, so it's not applied. Add one after a colon: prbuddy-ignore {s.RuleIds[0]}: why this is fine."));
                else if (unknown.Count > 0)
                    problems.Add(Problem(file.Path, s.Line, $"prbuddy-ignore names unknown rule(s) {string.Join(", ", unknown)}, so it's not applied. Run prbuddy --list-rules."));
                else
                    usable.Add(s);
            }
            suppressionsByPath[file.Path] = usable;
        }

        var used = new HashSet<Suppression>();
        foreach (var finding in findings)
        {
            var match = finding.Path is { } path && suppressionsByPath.TryGetValue(path, out var list)
                ? list.FirstOrDefault(s => s.Covers(finding))
                : null;
            if (match is null)
            {
                kept.Add(finding);
                continue;
            }
            used.Add(match);
            suppressed.Add(new SuppressedFinding(finding, match.Reason!, match.Line));
        }

        // A suppression this PR adds that matches nothing is a typo'd id, a wrong line, or a leftover.
        foreach (var (path, list) in suppressionsByPath)
            foreach (var s in list.Where(s => !used.Contains(s) && byPath[path].IsAddedLine(s.Line)))
                problems.Add(Problem(path, s.Line, $"prbuddy-ignore {string.Join(", ", s.RuleIds)} doesn't match any finding. Remove it, or check the rule id and which line it's on.", Severity.Info));

        return (kept, suppressed, problems);
    }

    static Finding Problem(string path, int line, string message, Severity severity = Severity.Warning) =>
        new(Descriptor.Id, severity, message, path, line);
}
