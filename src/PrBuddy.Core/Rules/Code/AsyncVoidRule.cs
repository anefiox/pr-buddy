using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>PRB003: async void outside event handlers.</summary>
public sealed class AsyncVoidRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB003", "async void method", Severity.Warning, "Async");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes())
        {
            var finding = node switch
            {
                MethodDeclarationSyntax m => Check(file, m.Modifiers, m.ReturnType, m.ParameterList, m.Identifier.Text),
                LocalFunctionStatementSyntax l => Check(file, l.Modifiers, l.ReturnType, l.ParameterList, l.Identifier.Text),
                _ => null,
            };
            if (finding is not null) yield return finding;
        }
    }

    Finding? Check(AnalyzedFile file, SyntaxTokenList modifiers, TypeSyntax returnType, ParameterListSyntax parameters, string name)
    {
        var index = modifiers.IndexOf(SyntaxKind.AsyncKeyword);
        if (index < 0 || !file.IsNew(modifiers[index])) return null;
        if (returnType is not PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword }) return null;
        if (IsEventHandler(parameters)) return null;

        return Report(file.Path, AnalyzedFile.LineOf(modifiers[index]),
            $"'async void {name}' can't be awaited, and an exception inside it crashes the process. Return Task instead.");
    }

    static bool IsEventHandler(ParameterListSyntax parameters) =>
        parameters.Parameters.Count == 2 &&
        parameters.Parameters[1].Type?.ToString().EndsWith("EventArgs", StringComparison.Ordinal) == true;
}
