using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB042: a call that passes two or more bare null, true or false arguments by position, such as
/// new DeviceInfo(id, null, null, null, rid). Nobody can tell which parameter each one fills, or
/// notice when two get swapped. Naming them (name: null) says it at the call. Value-style APIs
/// (Interlocked.CompareExchange, Compare, Assert.Equal, MethodInfo.Invoke...) are skipped.
/// </summary>
public sealed class UnnamedLiteralArgumentsRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB042", "Unnamed null or bool arguments", Severity.Info, "Code");

    const int Threshold = 2;

    // Calls whose literal arguments are the values themselves, where a name adds nothing.
    static readonly HashSet<string> ValueApis = new(StringComparer.Ordinal)
        { "CompareExchange", "Exchange", "Equal", "NotEqual", "AreEqual", "AreNotEqual", "Equals", "Same", "Add", "Set", "SetValue", "Write", "Return",
          "Compare", "CompareTo", "Invoke", "DynamicInvoke" };

    protected override bool AppliesTo(AnalyzedFile file) => !file.IsTestFile;

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        foreach (var list in root.DescendantNodes().OfType<ArgumentListSyntax>())
        {
            if (!file.IsNew(list) || list.Parent is not (InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax)) continue;
            var bare = list.Arguments.Where(a => a.NameColon is null && a.Expression is LiteralExpressionSyntax l &&
                l.Kind() is SyntaxKind.NullLiteralExpression or SyntaxKind.TrueLiteralExpression or SyntaxKind.FalseLiteralExpression).ToArray();
            if (bare.Length < Threshold) continue;
            if (list.Parent is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: var method } } &&
                ValueApis.Contains(method))
                continue;

            var callee = list.Parent switch
            {
                InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax m } => m.Name.Identifier.ValueText,
                InvocationExpressionSyntax i => i.Expression.ToString(),
                ObjectCreationExpressionSyntax o => "new " + o.Type,
                _ => "this call",
            };
            yield return Report(file, list.Parent,
                $"'{SyntaxHelpers.Truncate(callee, 40)}' gets {bare.Length} unnamed {string.Join("/", bare.Select(b => b.ToString()).Distinct())} arguments. " +
                "Name them (name: value) so readers can see what each one means, and a swap can't slip through.");
        }
    }
}
