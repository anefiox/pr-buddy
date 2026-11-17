using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Testing;

/// <summary>
/// PRB107: a test with two or more Assert.True/False calls that carry no message. When one fails the
/// output is just "Expected: True, Actual: False", with nothing to say which check it was.
/// </summary>
public sealed class BareBooleanAssertsRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } =
        new("PRB107", "Several boolean asserts without messages", Severity.Info, "Tests");

    static readonly HashSet<string> BooleanAsserts = new(StringComparer.Ordinal) { "True", "False", "IsTrue", "IsFalse" };

    protected override bool AppliesTo(AnalyzedFile file) => file.IsTestFile;

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        // One finding per file: the fix is a habit, so a finding per test only repeats itself.
        var tests = new List<(MethodDeclarationSyntax Method, InvocationExpressionSyntax First, int Count)>();
        foreach (var method in TestSyntax.TestMethods(root))
        {
            var bare = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => i.Expression is MemberAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax { Identifier.ValueText: "Assert" or "ClassicAssert" },
                    Name.Identifier.ValueText: var name,
                } && BooleanAsserts.Contains(name) && i.ArgumentList.Arguments.Count == 1)
                .ToArray();
            if (bare.Length >= 2 && bare.Any(file.IsNew)) tests.Add((method, bare.First(file.IsNew), bare.Length));
        }
        if (tests.Count == 0) yield break;

        var (first, at, count) = tests[0];
        var which = tests.Count == 1
            ? $"Test {first.Identifier.ValueText} has {count} Assert.True/False calls"
            : $"{tests.Count} tests here, starting with {first.Identifier.ValueText}, have two or more Assert.True/False calls";
        yield return Report(file, at, which + " without a message. A failure only says the value was false, not which check failed. " +
            "Add a message to each, or assert on the values themselves (Assert.Equal, Assert.Contains).");
    }
}
