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
            if (real == 0 && HelpersAssert(method)) continue;
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

    /// <summary>
    /// BDD-style and arrange/act/assert helpers keep the assertions in methods the test calls:
    /// .Then(x => ThenTheEventsArrive()). Follow calls to methods of the same type, two levels deep.
    /// </summary>
    static bool HelpersAssert(MethodDeclarationSyntax test)
    {
        if (test.Parent is not TypeDeclarationSyntax type) return false;
        var methods = type.Members.OfType<MethodDeclarationSyntax>()
            .GroupBy(m => m.Identifier.ValueText).ToDictionary(g => g.Key, g => g.ToArray());
        var seen = new HashSet<string>(StringComparer.Ordinal) { test.Identifier.ValueText };
        var frontier = new List<MethodDeclarationSyntax> { test };
        for (var depth = 0; depth < 2 && frontier.Count > 0; depth++)
        {
            var next = new List<MethodDeclarationSyntax>();
            foreach (var name in frontier.SelectMany(CalledNames).Where(seen.Add))
                if (methods.TryGetValue(name, out var helpers))
                    next.AddRange(helpers);
            if (next.Any(h => TestSyntax.CountAssertions(h).Real > 0)) return true;
            frontier = next;
        }
        return false;
    }

    static IEnumerable<string> CalledNames(MethodDeclarationSyntax method) =>
        method.DescendantNodes().OfType<InvocationExpressionSyntax>().Select(i => i.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: var n } => n.Identifier.ValueText,
            GenericNameSyntax g => g.Identifier.ValueText,
            _ => null,
        }).OfType<string>();
}
