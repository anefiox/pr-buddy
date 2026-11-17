using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Testing;

/// <summary>
/// PRB105: test code that looks up a non-public member by name through reflection. A rename or
/// signature change still compiles and only fails when the test runs, and the test is pinned to
/// implementation details.
/// </summary>
public sealed class ReflectionIntoPrivatesRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } =
        new("PRB105", "Test reaches a private member through reflection", Severity.Info, "Tests");

    static readonly HashSet<string> Lookups = new(StringComparer.Ordinal)
        { "GetMethod", "GetField", "GetProperty", "GetMember", "GetConstructor", "GetEvent", "GetNestedType", "InvokeMember" };

    protected override bool AppliesTo(AnalyzedFile file) => file.IsTestFile;

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        // One finding per file: the fix (make it internal, or go through the public API) is the same each time.
        var lookups = new List<(InvocationExpressionSyntax Call, string Member)>();
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: var name } ||
                !Lookups.Contains(name) || !file.IsNew(call) ||
                !call.ArgumentList.Arguments.Any(a => a.Expression.DescendantNodesAndSelf()
                    .OfType<IdentifierNameSyntax>().Any(id => id.Identifier.ValueText == "NonPublic")))
                continue;

            lookups.Add((call, call.ArgumentList.Arguments.FirstOrDefault()?.Expression switch
            {
                LiteralExpressionSyntax literal => $"'{literal.Token.ValueText}'",
                InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } } n =>
                    $"'{n.ArgumentList.Arguments.FirstOrDefault()?.ToString()}'",
                _ => "a non-public member",
            }));
        }
        if (lookups.Count == 0) yield break;

        var members = lookups.Select(l => l.Member).Distinct().ToArray();
        var what = lookups.Count == 1
            ? $"This test looks up {members[0]}"
            : $"Tests here look up non-public members {lookups.Count} times ({string.Join(", ", members.Take(4))}{(members.Length > 4 ? ", ..." : "")})";
        yield return Report(file, lookups[0].Call,
            $"{what} through reflection with BindingFlags.NonPublic. A rename still compiles " +
            "and only breaks when the test runs. Make the member internal and use InternalsVisibleTo, " +
            "or test it through the public API.");
    }
}
