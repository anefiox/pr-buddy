namespace PrBuddy.Core.Rules.Config;

/// <summary>PRB201: CI, analyzer or build-wide settings changed. These change the rules for everything else.</summary>
public sealed class BuildConfigChangedRule : RuleBase
{
    public override RuleDescriptor Descriptor { get; } = new("PRB201", "Build or CI configuration changed", Severity.Info, "Config");

    public override IEnumerable<Finding> Analyze(PullRequestContext context) =>
        context.Files
            .Where(f => FileClassifier.IsBuildConfig(f.Path))
            .Select(f => Report(f.Path, null, "Build or CI configuration changed. This affects how every other change gets checked."));
}
