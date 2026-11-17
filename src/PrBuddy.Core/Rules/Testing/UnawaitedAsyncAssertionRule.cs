using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Testing;

/// <summary>
/// PRB109: an async assertion (Assert.ThrowsAsync, x.Should().ThrowAsync(), ...) whose task is
/// dropped. The test finishes before the assertion does, so a failure is never reported to it.
/// NUnit's Assert.ThrowsAsync blocks in versions before 5, so in NUnit files it's info.
/// </summary>
public sealed class UnawaitedAsyncAssertionRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB109", "Async assertion not awaited", Severity.Warning, "Tests");

    static readonly HashSet<string> AsyncAssertions = new(StringComparer.Ordinal)
    {
        "ThrowsAsync", "ThrowsAnyAsync", "ThrowAsync", "ThrowExactlyAsync", "NotThrowAsync", "NotThrowAfterAsync",
        "CompleteWithinAsync", "NotCompleteWithinAsync", "ShouldThrowAsync", "ShouldNotThrowAsync", "AllAsync", "CollectionAsync",
    };

    protected override bool AppliesTo(AnalyzedFile file) => file.IsTestFile;

    /// <summary>
    /// Assert.ThrowsAsync(...), x.Should().ThrowAsync(), act.ShouldThrowAsync(). Not Moq's
    /// Setup(...).ThrowsAsync(ex) or NSubstitute's call.ThrowsAsync(ex): those configure a fake.
    /// </summary>
    static bool IsAssertion(MemberAccessExpressionSyntax access)
    {
        var name = access.Name.Identifier.ValueText;
        if (access.Expression is IdentifierNameSyntax { Identifier.ValueText: "Assert" or "ClassicAssert" } ||
            access.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Assert" })
            return true;
        if (name.StartsWith("Should", StringComparison.Ordinal)) return true; // Shouldly
        return access.Expression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(i =>
            i.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Should" } or
                IdentifierNameSyntax { Identifier.ValueText: "Should" });
    }

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        var nunit = root.DescendantNodes().OfType<UsingDirectiveSyntax>().Any(u => u.Name?.ToString() is "NUnit.Framework");
        foreach (var statement in root.DescendantNodes().OfType<ExpressionStatementSyntax>())
        {
            if (statement.Expression is not InvocationExpressionSyntax call || !file.IsNew(statement) ||
                call.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: var name } access ||
                !AsyncAssertions.Contains(name) || !IsAssertion(access))
                continue;

            var isNUnitAssert = nunit && call.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "Assert" } };
            yield return isNUnitAssert
                ? Report(file, call,
                    $"NUnit's Assert.{name} isn't awaited. Before NUnit 5 it blocks until done, but from NUnit 5 it returns a task, " +
                    "and the test can pass before the assertion fails. Await it, and make the test async Task.", Severity.Info)
                : Report(file, call,
                    $"'{name}' returns a task that nothing awaits, so the test can finish, and pass, before the assertion fails. " +
                    "Await it, and make the test async Task.");
        }
    }
}
