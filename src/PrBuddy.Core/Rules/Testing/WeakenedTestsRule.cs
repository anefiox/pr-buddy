using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Testing;

/// <summary>
/// PRB102: existing tests that lost assertions or were deleted. The classic way to make a failing
/// test pass is to change the test.
/// </summary>
public sealed class WeakenedTestsRule : RuleBase
{
    public override RuleDescriptor Descriptor { get; } = new("PRB102", "Tests weakened or removed", Severity.Warning, "Tests");

    public override IEnumerable<Finding> Analyze(PullRequestContext context)
    {
        foreach (var file in context.CSharpFiles.Where(f => f.IsTestFile && f.OldTree is not null))
        {
            var oldTests = TestSyntax.TestMethods(file.OldTree!.GetRoot()).ToList();
            if (oldTests.Count == 0) continue;

            if (file.NewTree is null)
            {
                yield return Report(file.Path, null, $"Test file deleted, taking {oldTests.Count} test(s) with it.");
                continue;
            }

            var newTests = TestSyntax.TestMethods(file.NewTree.GetRoot()).ToList();
            var newByKey = newTests.GroupBy(TestSyntax.KeyOf).ToDictionary(g => g.Key, g => g.First());

            foreach (var old in oldTests)
            {
                var key = TestSyntax.KeyOf(old);
                if (newByKey.TryGetValue(key, out var current))
                {
                    var before = TestSyntax.CountAssertions(old).Real;
                    var after = TestSyntax.CountAssertions(current).Real;
                    if (after < before)
                        yield return Report(file.Path, AnalyzedFile.LineOf(current.Identifier),
                            $"{key} went from {before} assertion(s) to {after}. Was the test weakened to make it pass?");
                }
                else if (!newTests.Any(n => SameBody(old, n)))
                {
                    yield return Report(file.Path, null, $"Test {key} was removed.");
                }
            }
        }
    }

    static bool SameBody(MethodDeclarationSyntax a, MethodDeclarationSyntax b) =>
        Body(a) is { } x && Body(b) is { } y && x.IsEquivalentTo(y, topLevel: false);

    static SyntaxNode? Body(MethodDeclarationSyntax method) => (SyntaxNode?)method.Body ?? method.ExpressionBody;
}
