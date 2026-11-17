using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB028: a media type or content type compared with Contains, or an exact comparison OR'ed with a
/// Contains of the same value. "text/event-stream" also matches "x-text/event-stream-ish", and the
/// Contains makes the exact check pointless.
/// </summary>
public sealed class LooseMediaTypeMatchRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB028", "Loose substring match", Severity.Info, "Code");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        var reported = new HashSet<SyntaxNode>();
        foreach (var or in root.DescendantNodes().OfType<BinaryExpressionSyntax>().Where(b => b.IsKind(SyntaxKind.LogicalOrExpression)))
        {
            if (!file.IsNew(or)) continue;
            var calls = Operands(or).OfType<InvocationExpressionSyntax>().Select(Describe).Where(c => c is not null).Select(c => c!.Value).ToArray();
            foreach (var contains in calls.Where(c => c.Method == "Contains"))
            {
                if (!calls.Any(c => c.Method is "Equals" or "StartsWith" && c.Receiver == contains.Receiver && c.Argument == contains.Argument)) continue;
                reported.Add(contains.Call);
                yield return Report(file, contains.Call,
                    $"'{contains.Receiver}.Contains({contains.Argument})' accepts every value the exact check next to it does, and more, " +
                    "so the exact check never decides anything. Keep the exact comparison and drop the Contains, unless partial matches are really wanted.");
            }
        }

        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            // Contains(';') or Contains("/") looks for a separator, not a type.
            if (reported.Contains(call) || !file.IsNew(call) || Describe(call) is not { Method: "Contains" } c ||
                !LooksLikeMediaType(c.Receiver) || c.Argument.StartsWith('\'') || c.Argument.Length <= 3 && c.Argument.StartsWith('"'))
                continue;
            yield return Report(file, call,
                $"'{c.Receiver}' is matched with Contains, which also accepts types that merely include {c.Argument}. " +
                "Compare the media type (without parameters) with Equals and StringComparison.OrdinalIgnoreCase.");
        }
    }

    static bool LooksLikeMediaType(string receiver) =>
        receiver.EndsWith("MediaType", StringComparison.Ordinal) || receiver.EndsWith("ContentType", StringComparison.Ordinal) ||
        receiver.EndsWith("mediaType", StringComparison.Ordinal) || receiver.EndsWith("contentType", StringComparison.Ordinal);

    static IEnumerable<ExpressionSyntax> Operands(BinaryExpressionSyntax or)
    {
        foreach (var side in new[] { or.Left, or.Right })
        {
            var e = side is ParenthesizedExpressionSyntax p ? p.Expression : side;
            if (e is BinaryExpressionSyntax inner && inner.IsKind(SyntaxKind.LogicalOrExpression))
                foreach (var nested in Operands(inner)) yield return nested;
            else yield return e;
        }
    }

    static (InvocationExpressionSyntax Call, string Receiver, string Method, string Argument)? Describe(InvocationExpressionSyntax call) =>
        call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: var method } access &&
        call.ArgumentList.Arguments.Count >= 1 && method is "Contains" or "Equals" or "StartsWith"
            ? (call, access.Expression.ToString(), method, call.ArgumentList.Arguments[0].ToString())
            : null;
}
