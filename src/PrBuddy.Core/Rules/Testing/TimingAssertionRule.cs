using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Testing;

/// <summary>
/// PRB106: a test asserts on measured wall-clock time. Shared CI machines pause, throttle and
/// time-slice, so the bound either flakes or is set so loose it catches nothing.
/// </summary>
public sealed class TimingAssertionRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB106", "Test asserts on elapsed time", Severity.Warning, "Tests");

    static readonly HashSet<string> ElapsedMembers = new(StringComparer.Ordinal)
        { "Elapsed", "ElapsedMilliseconds", "ElapsedTicks", "GetElapsedTime" };

    protected override bool AppliesTo(AnalyzedFile file) => file.IsTestFile;

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        foreach (var method in TestSyntax.TestMethods(root))
        {
            if (!file.TouchesAddedLines(method)) continue;
            var timers = TimerLocals(method);

            foreach (var assertion in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (!TestSyntax.IsAssertion(assertion) || !file.IsNew(assertion)) continue;
                var timing = assertion.ArgumentList.Arguments
                    .Take(assertion.ArgumentList.Arguments.Count == 1 ? 1 : 3)
                    .Where(a => a.Expression is not InterpolatedStringExpressionSyntax and not LiteralExpressionSyntax)
                    .SelectMany(a => a.Expression.DescendantNodesAndSelf())
                    .FirstOrDefault(n => IsElapsed(n, timers));
                if (assertion.Expression is MemberAccessExpressionSyntax { Expression: var receiver } && timing is null)
                    timing = receiver.DescendantNodesAndSelf().FirstOrDefault(n => IsElapsed(n, timers)); // elapsed.Should().BeLessThan(...)
                if (timing is null) continue;

                yield return Report(file, assertion,
                    $"Test {method.Identifier.ValueText} asserts on measured time ({SyntaxHelpers.Truncate(timing.ToString(), 40)}). " +
                    "On a busy CI machine this either flakes or needs a bound too loose to catch a regression. " +
                    "Measure it in a benchmark, or assert on the behavior (no wait was started, the timeout passed in) instead.");
                break;
            }
        }
    }

    /// <summary>Stopwatch locals, so elapsed.TotalMilliseconds and DateTime differences count too.</summary>
    static HashSet<string> TimerLocals(MethodDeclarationSyntax method) => new(
        method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Initializer?.Value.ToString() is { } init &&
                (init.Contains("Stopwatch.", StringComparison.Ordinal) || init.Contains(".Elapsed", StringComparison.Ordinal) ||
                 init.Contains("DateTime.UtcNow -", StringComparison.Ordinal) || init.Contains("DateTime.Now -", StringComparison.Ordinal)))
            .Select(v => v.Identifier.ValueText),
        StringComparer.Ordinal);

    static bool IsElapsed(SyntaxNode node, HashSet<string> timers) => node switch
    {
        MemberAccessExpressionSyntax m => ElapsedMembers.Contains(m.Name.Identifier.ValueText) ||
            m.Expression is IdentifierNameSyntax id && timers.Contains(id.Identifier.ValueText) &&
            m.Name.Identifier.ValueText.StartsWith("Total", StringComparison.Ordinal),
        BinaryExpressionSyntax b when b.OperatorToken.ValueText == "-" =>
            b.Left.ToString().Contains("DateTime.", StringComparison.Ordinal) && b.Right is not LiteralExpressionSyntax,
        _ => false,
    };
}
