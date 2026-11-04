using PrBuddy.Core.Semantics;

namespace PrBuddy.Core.Rules.Testing;

/// <summary>PRB101: production code changed behavior but no test file was touched.</summary>
public sealed class MissingTestsRule : RuleBase
{
    public override RuleDescriptor Descriptor { get; } = new("PRB101", "Behavior changed without test changes", Severity.Warning, "Tests");

    public override IEnumerable<Finding> Analyze(PullRequestContext context)
    {
        if (context.Files.Any(f => f.IsTestFile)) yield break;

        var changed = context.CSharpFiles
            .Where(f => !f.IsTestFile && f.SemanticDiff is { } diff && diff.Changes.Any(IsBehavioral))
            .ToList();

        if (changed.Count > 0)
            yield return Report(path: null, line: null,
                message: $"{changed.Count} production file(s) change behavior, but no test files were added or changed.");
    }

    static bool IsBehavioral(MemberChange change) =>
        change.Change is MemberChangeKind.BehaviorChanged or MemberChangeKind.SignatureChanged or MemberChangeKind.Added;
}
