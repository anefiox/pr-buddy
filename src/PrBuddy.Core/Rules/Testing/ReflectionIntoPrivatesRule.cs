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
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: var name } ||
                !Lookups.Contains(name) || !file.IsNew(call) ||
                !call.ArgumentList.Arguments.Any(a => a.Expression.DescendantNodesAndSelf()
                    .OfType<IdentifierNameSyntax>().Any(id => id.Identifier.ValueText == "NonPublic")))
                continue;

            var member = call.ArgumentList.Arguments.FirstOrDefault()?.Expression switch
            {
                LiteralExpressionSyntax literal => $"'{literal.Token.ValueText}'",
                InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } } n =>
                    $"'{n.ArgumentList.Arguments.FirstOrDefault()?.ToString()}'",
                _ => "a non-public member",
            };
            yield return Report(file, call,
                $"This test looks up {member} through reflection with BindingFlags.NonPublic. A rename still compiles " +
                "and only breaks when the test runs. Make the member internal and use InternalsVisibleTo, " +
                "or test it through the public API.");
        }
    }
}
