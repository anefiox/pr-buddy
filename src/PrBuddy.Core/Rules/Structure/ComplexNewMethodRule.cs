using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Structure;

/// <summary>
/// PRB013: new methods with high cyclomatic complexity: lots of independent paths through them, so
/// hard to read, review and test. Generated code tends to produce a few huge methods.
///
/// Complexity is 1 plus one per branch point: if, case label, loop, catch, ?:, ??, &amp;&amp;, ||,
/// and switch-expression arm. Local functions and lambdas count toward the method they're in.
/// One finding per file, listing every method over the threshold, so a new file doesn't produce a wall.
/// </summary>
public sealed class ComplexNewMethodRule(int threshold = ComplexNewMethodRule.DefaultThreshold) : RuleBase
{
    /// <summary>
    /// McCabe's classic "worth a look" line is 10-15, but on real code that flags every argument
    /// parser. Calibrated on 451 real commits: 25 is where methods get genuinely hard to follow.
    /// </summary>
    public const int DefaultThreshold = 25;

    public override RuleDescriptor Descriptor { get; } = new("PRB013", "Complex new method", Severity.Info, "Structure");

    public override IEnumerable<Finding> Analyze(PullRequestContext context)
    {
        foreach (var file in context.CSharpFiles)
        {
            var complex = new List<(string Name, int Complexity, int Line)>();
            foreach (var method in AddedMembers.In(file).OfType<BaseMethodDeclarationSyntax>())
            {
                SyntaxNode? body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
                if (body is null) continue;
                var complexity = Complexity(body);
                if (complexity < threshold) continue;

                var name = method switch
                {
                    MethodDeclarationSyntax m => m.Identifier.Text,
                    ConstructorDeclarationSyntax c => c.Identifier.Text,
                    _ => "method",
                };
                complex.Add((AddedMembers.QualifiedName(method, name), complexity, AnalyzedFile.LineOf(method)));
            }
            if (complex.Count == 0) continue;

            var worst = complex.MaxBy(c => c.Complexity);
            yield return complex.Count == 1
                ? Report(file.Path, worst.Line,
                    $"New method '{worst.Name}' has cyclomatic complexity {worst.Complexity} (threshold {threshold}): {worst.Complexity} independent paths to understand and test. Can it be split up?")
                : Report(file.Path, worst.Line,
                    $"{complex.Count} new methods are over complexity {threshold}: " +
                    string.Join(", ", complex.OrderByDescending(c => c.Complexity).Select(c => $"{c.Name} ({c.Complexity}, L{c.Line})")) +
                    ". Can they be split up?");
        }
    }

    public static int Complexity(SyntaxNode body)
    {
        var complexity = 1;
        foreach (var node in body.DescendantNodesAndSelf())
        {
            complexity += node switch
            {
                IfStatementSyntax or WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or ForEachStatementSyntax
                    or CatchClauseSyntax or ConditionalExpressionSyntax or SwitchExpressionArmSyntax => 1,
                CaseSwitchLabelSyntax or CasePatternSwitchLabelSyntax => 1,
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression)
                                             || b.IsKind(SyntaxKind.CoalesceExpression) => 1,
                _ => 0,
            };
        }
        return complexity;
    }
}
