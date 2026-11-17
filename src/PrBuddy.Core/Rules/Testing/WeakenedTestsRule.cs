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
        // Tests often move to another file or a renamed class. One that turns up anywhere in the PR's
        // test files, by name or with the same body, wasn't removed.
        var everyNewTest = context.CSharpFiles.Where(f => f.IsTestFile && f.NewTree is not null)
            .SelectMany(f => TestSyntax.TestMethods(f.NewTree!.GetRoot())).ToList();
        var newNames = everyNewTest.Select(t => t.Identifier.Text).ToHashSet(StringComparer.Ordinal);
        bool MovedElsewhere(MethodDeclarationSyntax old) =>
            newNames.Contains(old.Identifier.Text) || everyNewTest.Any(n => SameBody(old, n));

        foreach (var file in context.CSharpFiles.Where(f => f.IsTestFile && f.OldTree is not null))
        {
            var oldTests = TestSyntax.TestMethods(file.OldTree!.GetRoot()).ToList();
            if (oldTests.Count == 0) continue;

            if (file.NewTree is null)
            {
                var gone = oldTests.Count(t => !MovedElsewhere(t));
                if (gone > 0)
                    yield return Report(file.Path, null, gone == oldTests.Count
                        ? $"Test file deleted, taking {gone} test(s) with it."
                        : $"Test file deleted. {oldTests.Count - gone} of its tests moved elsewhere, but {gone} didn't.");
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
                else if (!newTests.Any(n => SameBody(old, n)) && !MovedElsewhere(old))
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
