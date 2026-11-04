using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Testing;

/// <summary>PRB104: new or changed tests that can't fail: no assertions, or only Assert.True(true)-style ones.</summary>
public sealed class TestWithoutAssertionsRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB104", "Test that can't fail", Severity.Warning, "Tests");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        foreach (var method in TestSyntax.TestMethods(root))
        {
            if (!file.TouchesAddedLines(method) || TestSyntax.ExpectsException(method)) continue;

            var name = method.Identifier.Text;
            var (real, trivial) = TestSyntax.CountAssertions(method);
            if (real == 0)
            {
                yield return Report(file.Path, AnalyzedFile.LineOf(method.Identifier), trivial > 0
                    ? $"Test {name} only has assertions that always pass."
                    : $"Test {name} doesn't assert anything, so it passes unless something throws.");
                continue;
            }

            foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                if (file.IsNew(invocation) && TestSyntax.IsAssertion(invocation) && TestSyntax.IsTrivial(invocation))
                    yield return Report(file, invocation, $"This assertion always passes: {SyntaxHelpers.Truncate(invocation.ToString())}");
        }
    }
}
