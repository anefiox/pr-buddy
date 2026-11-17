using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB026: a string is encoded to a byte array and the very next check refuses it when it's too
/// long. Untrusted input pays for the whole copy first. Encoding.GetByteCount checks without
/// allocating.
/// </summary>
public sealed class EncodeBeforeLengthCheckRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB026", "Encoded before the length check", Severity.Info, "Code");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        foreach (var block in root.DescendantNodes().OfType<BlockSyntax>())
        {
            var statements = block.Statements;
            for (var i = 0; i + 1 < statements.Count; i++)
            {
                if (statements[i] is not LocalDeclarationStatementSyntax
                    {
                        Declaration.Variables: [{ Initializer.Value: InvocationExpressionSyntax
                        {
                            Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetBytes" } access,
                            ArgumentList.Arguments: [var source],
                        } } variable],
                    } declaration ||
                    !access.Expression.ToString().Contains("Encoding", StringComparison.Ordinal) ||
                    // A renamed file's moved lines aren't "added", but the move is a natural time to fix this.
                    !file.IsNew(declaration) && file.File.Status != ChangeStatus.Renamed ||
                    statements[i + 1] is not IfStatementSyntax check || !RefusesWhenLong(check.Condition, variable.Identifier.ValueText))
                    continue;

                yield return Report(file, declaration,
                    $"'{source}' is encoded in full before the length check on the next line refuses it, so an oversized input " +
                    $"allocates its whole size first. Check {access.Expression}.GetByteCount({source}) before calling GetBytes.");
            }
        }
    }

    static bool RefusesWhenLong(ExpressionSyntax condition, string bytes) =>
        condition.DescendantNodesAndSelf().OfType<BinaryExpressionSyntax>().Any(b =>
            b.Kind() is SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression &&
            b.Left is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax id, Name.Identifier.ValueText: "Length" } &&
            id.Identifier.ValueText == bytes);
}
