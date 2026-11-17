using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace PrBuddy.Core.Rules.Code;

public sealed class DiscardedCancellationRegistrationRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } =
        new("PRB015", "Discarded cancellation registration", Severity.Warning, "Code");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        var calls = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => call.Expression is
                MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Register" or "UnsafeRegister" } or
                MemberBindingExpressionSyntax { Name.Identifier.ValueText: "Register" or "UnsafeRegister" })
            .Select(call => (Call: call, Line: LocalSemantics.AddedLine(file, call)))
            .Where(call => call.Line is not null).ToArray();
        if (calls.Length == 0) yield break;

        var model = LocalSemantics.Create(root.SyntaxTree);
        var tokenType = model.Compilation.GetTypeByMetadataName("System.Threading.CancellationToken");
        foreach (var (call, line) in calls)
        {
            if (model.GetOperation(call) is not IInvocationOperation invocation ||
                !LocalSemantics.Same(invocation.TargetMethod.ContainingType, tokenType) ||
                CannotBeCanceled(invocation.Instance, tokenType) ||
                !IsDiscarded(invocation))
                continue;

            yield return Report(file.Path, line, "The cancellation registration is discarded. " +
                "Its callback can outlive this operation and retain or act on its state. " +
                "Keep the registration and dispose it when the callback is no longer needed.");
        }
    }

    static bool CannotBeCanceled(IOperation? receiver, INamedTypeSymbol? tokenType) => receiver is
        IDefaultValueOperation or IObjectCreationOperation { Arguments.Length: 0 } ||
        receiver is IPropertyReferenceOperation { Property.Name: "None" } property &&
        LocalSemantics.Same(property.Property.ContainingType, tokenType);

    static bool IsDiscarded(IOperation operation)
    {
        while (operation.Parent is IConversionOperation or IConditionalAccessOperation)
            operation = operation.Parent;
        return operation.Parent is IExpressionStatementOperation or
            ISimpleAssignmentOperation { Target: IDiscardOperation } ||
            operation.Parent is IReturnOperation { IsImplicit: true, Parent: IBlockOperation
            {
                Parent: IAnonymousFunctionOperation { Symbol.ReturnsVoid: true }
            }};
    }
}
