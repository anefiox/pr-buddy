using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB018: a method returns early once a member is set, then sets that member inside a try before
/// later work that can still fail. If the later work throws, the catch keeps the member set, so the
/// guard blocks every retry.
/// </summary>
public sealed class PrematureLoadGuardRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } =
        new("PRB018", "Load guard set before the work finishes", Severity.Warning, "Code");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        foreach (var method in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
        {
            if (method.Body is not { } body || !file.TouchesAddedLines(method)) continue;

            var locals = LocalNames(method);
            var guards = new Dictionary<string, IfStatementSyntax>(StringComparer.Ordinal);
            foreach (var statement in body.Statements.Take(4).OfType<IfStatementSyntax>())
                if (ExitsEarly(statement.Statement))
                    foreach (var name in SetMembers(statement.Condition))
                        if (!locals.Contains(name)) guards.TryAdd(name, statement);
            if (guards.Count == 0) continue;

            foreach (var tryStatement in body.DescendantNodes(LocalSemantics.DescendInto).OfType<TryStatementSyntax>())
            {
                if (tryStatement.Catches.Count == 0) continue;
                var resets = new HashSet<string>(
                    tryStatement.Catches.Select(c => (SyntaxNode)c.Block)
                        .Concat(tryStatement.Finally is { } f ? [f.Block] : [])
                        .SelectMany(Assignments).Select(a => a.Name), StringComparer.Ordinal);

                foreach (var (name, assignment) in Assignments(tryStatement.Block))
                {
                    if (!guards.TryGetValue(name, out var guard) || resets.Contains(name) || ClearsValue(assignment.Right))
                        continue;
                    var statement = assignment.FirstAncestorOrSelf<StatementSyntax>();
                    var later = tryStatement.Block.DescendantNodes(LocalSemantics.DescendInto).OfType<StatementSyntax>()
                        .FirstOrDefault(s => s.SpanStart > assignment.Span.End && s is not BlockSyntax &&
                            !s.Ancestors().Contains(statement) && CanThrow(s));
                    if (later is null) continue;

                    var added = new SyntaxNode[] { assignment, later, guard }
                        .Select(n => LocalSemantics.AddedLine(file, n)).FirstOrDefault(l => l is not null);
                    if (added is null) continue;

                    yield return Report(file, assignment,
                        $"'{name}' is set before the rest of the work in this try (line {AnalyzedFile.LineOf(later)}) " +
                        $"finishes, and the check at line {AnalyzedFile.LineOf(guard)} returns early once it's set. " +
                        "If the later work throws, the catch leaves it set and the load is never retried. " +
                        "Set it after everything succeeds, or reset it in the catch.");
                    break;
                }
            }
        }
    }

    static bool ExitsEarly(StatementSyntax statement) => statement switch
    {
        ReturnStatementSyntax => true,
        BlockSyntax { Statements: [ReturnStatementSyntax] } => true,
        _ => false,
    };

    /// <summary>Members the condition treats as "already done": X is not null, X != null, or a bare bool X.</summary>
    static IEnumerable<string> SetMembers(ExpressionSyntax condition)
    {
        switch (condition)
        {
            case ParenthesizedExpressionSyntax p:
                return SetMembers(p.Expression);
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalOrExpression):
                return SetMembers(b.Left).Concat(SetMembers(b.Right));
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.NotEqualsExpression) && IsNull(b.Right):
                return MemberName(b.Left);
            case IsPatternExpressionSyntax { Pattern: UnaryPatternSyntax { Pattern: ConstantPatternSyntax c } } i
                when IsNull(c.Expression):
                return MemberName(i.Expression);
            case IsPatternExpressionSyntax { Pattern: RecursivePatternSyntax { Designation: null } } i:
                return MemberName(i.Expression);
            case IdentifierNameSyntax or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax }:
                return MemberName(condition);
            default:
                return [];
        }
    }

    static IEnumerable<string> MemberName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax id => [id.Identifier.ValueText],
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: var n } => [n.Identifier.ValueText],
        _ => [],
    };

    static bool IsNull(ExpressionSyntax e) => e.IsKind(SyntaxKind.NullLiteralExpression);

    static bool ClearsValue(ExpressionSyntax value) =>
        value.IsKind(SyntaxKind.NullLiteralExpression) || value.IsKind(SyntaxKind.FalseLiteralExpression) ||
        value.IsKind(SyntaxKind.DefaultLiteralExpression);

    static IEnumerable<(string Name, AssignmentExpressionSyntax Assignment)> Assignments(SyntaxNode scope) =>
        scope.DescendantNodes(LocalSemantics.DescendInto).OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.SimpleAssignmentExpression))
            .SelectMany(a => MemberName(a.Left).Select(n => (n, a)));

    static bool CanThrow(StatementSyntax statement) =>
        statement.DescendantNodesAndSelf(LocalSemantics.DescendInto)
            .Any(n => n is AwaitExpressionSyntax or InvocationExpressionSyntax or ThrowStatementSyntax or ThrowExpressionSyntax);

    static HashSet<string> LocalNames(BaseMethodDeclarationSyntax method) => new(
        method.ParameterList.Parameters.Select(p => p.Identifier.ValueText)
            .Concat(method.DescendantNodes().OfType<VariableDeclaratorSyntax>().Select(v => v.Identifier.ValueText))
            .Concat(method.DescendantNodes().OfType<SingleVariableDesignationSyntax>().Select(v => v.Identifier.ValueText)),
        StringComparer.Ordinal);
}
