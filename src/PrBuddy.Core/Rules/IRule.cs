using Microsoft.CodeAnalysis;

namespace PrBuddy.Core.Rules;

public sealed record RuleDescriptor(string Id, string Title, Severity DefaultSeverity, string Category);

/// <summary>A deterministic check over the whole PR. Implement this for cross-file rules.</summary>
public interface IRule
{
    RuleDescriptor Descriptor { get; }
    IEnumerable<Finding> Analyze(PullRequestContext context);
}

public abstract class RuleBase : IRule
{
    public abstract RuleDescriptor Descriptor { get; }
    public abstract IEnumerable<Finding> Analyze(PullRequestContext context);

    protected Finding Report(string? path, int? line, string message, Severity? severity = null) =>
        new(Descriptor.Id, severity ?? Descriptor.DefaultSeverity, message, path, line);

    protected Finding Report(AnalyzedFile file, SyntaxNode node, string message, Severity? severity = null) =>
        Report(file.Path, AnalyzedFile.LineOf(node), message, severity);
}

/// <summary>
/// Base for rules that look at the new version of each C# file. Implementations should only report
/// code the PR added (use <see cref="AnalyzedFile.IsNew(SyntaxNode)"/>), so old debt stays out of the way.
/// </summary>
public abstract class AddedCodeRule : RuleBase
{
    public sealed override IEnumerable<Finding> Analyze(PullRequestContext context) =>
        context.CSharpFiles
            .Where(f => f.NewTree is not null && AppliesTo(f))
            .SelectMany(f => AnalyzeFile(f, f.NewTree!.GetRoot()));

    protected virtual bool AppliesTo(AnalyzedFile file) => true;

    protected abstract IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root);
}
