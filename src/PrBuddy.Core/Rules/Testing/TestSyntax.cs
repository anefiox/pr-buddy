using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Testing;

/// <summary>Recognizes tests and assertions across xUnit, NUnit, MSTest, FluentAssertions, Shouldly, Moq and NSubstitute.</summary>
public static class TestSyntax
{
    static readonly HashSet<string> TestAttributes = new(StringComparer.Ordinal)
    {
        "Fact", "Theory", "SkippableFact", "SkippableTheory", "Test", "TestCase", "TestCaseSource", "TestMethod", "DataTestMethod",
    };

    static readonly HashSet<string> AssertClasses = new(StringComparer.Ordinal)
    {
        "Assert", "CollectionAssert", "StringAssert", "ClassicAssert", "FileAssert", "DirectoryAssert",
    };

    static readonly HashSet<string> MockVerifications = new(StringComparer.Ordinal)
    {
        "Received", "DidNotReceive", "ReceivedWithAnyArgs", "DidNotReceiveWithAnyArgs",
    };

    public static bool IsTestMethod(MethodDeclarationSyntax method) =>
        Attributes(method).Any(a => TestAttributes.Contains(SyntaxHelpers.AttributeName(a)));

    public static IEnumerable<MethodDeclarationSyntax> TestMethods(SyntaxNode root) =>
        root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(IsTestMethod);

    public static string KeyOf(MethodDeclarationSyntax method) =>
        $"{method.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text}.{method.Identifier.Text}";

    /// <summary>[ExpectedException] is an assertion even though there's no Assert call.</summary>
    public static bool ExpectsException(MethodDeclarationSyntax method) =>
        Attributes(method).Any(a => SyntaxHelpers.AttributeName(a) == "ExpectedException");

    public static (int Real, int Trivial) CountAssertions(MethodDeclarationSyntax method)
    {
        int real = 0, trivial = 0;
        foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!IsAssertion(invocation)) continue;
            if (IsTrivial(invocation)) trivial++;
            else real++;
        }
        return (real, trivial);
    }

    /// <summary>
    /// Assert.*, x.Should()..., x.ShouldBe(...), mock.Verify(...), sub.Received(), plus helpers whose
    /// names start with Assert/Verify/Expect, which covers most hand-rolled assertion helpers.
    /// </summary>
    public static bool IsAssertion(InvocationExpressionSyntax invocation)
    {
        var (receiver, name) = Describe(invocation);
        if (receiver is not null && AssertClasses.Contains(receiver)) return true;
        return name.StartsWith("Should", StringComparison.Ordinal)
            || name.StartsWith("Assert", StringComparison.Ordinal)
            || name.StartsWith("Verify", StringComparison.Ordinal)
            || name.StartsWith("Expect", StringComparison.Ordinal)
            || MockVerifications.Contains(name);
    }

    /// <summary>Assertions that can't fail: Assert.True(true), Assert.Equal(x, x) and friends.</summary>
    public static bool IsTrivial(InvocationExpressionSyntax invocation)
    {
        var (receiver, name) = Describe(invocation);
        if (receiver is null || !AssertClasses.Contains(receiver)) return false;

        var args = invocation.ArgumentList.Arguments;
        if (args.Count == 0) return false;
        var first = args[0].Expression;

        return name switch
        {
            "True" or "IsTrue" => first.IsKind(SyntaxKind.TrueLiteralExpression),
            "That" => args.Count == 1 && first.IsKind(SyntaxKind.TrueLiteralExpression),
            "False" or "IsFalse" => first.IsKind(SyntaxKind.FalseLiteralExpression),
            "Equal" or "AreEqual" or "StrictEqual" or "Same" or "AreSame" =>
                args.Count >= 2 && first.IsEquivalentTo(args[1].Expression, topLevel: false),
            _ => false,
        };
    }

    static (string? Receiver, string Name) Describe(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax access => (ReceiverName(access.Expression), access.Name.Identifier.Text),
        SimpleNameSyntax name => (null, name.Identifier.Text),
        _ => (null, ""),
    };

    static string? ReceiverName(ExpressionSyntax expression) => expression switch
    {
        SimpleNameSyntax s => s.Identifier.Text,
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text, // NUnit.Framework.Assert → Assert
        _ => null,
    };

    static IEnumerable<AttributeSyntax> Attributes(MethodDeclarationSyntax method) =>
        method.AttributeLists.SelectMany(l => l.Attributes);
}
