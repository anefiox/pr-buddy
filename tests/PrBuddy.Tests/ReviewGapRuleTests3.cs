using PrBuddy.Core;
using PrBuddy.Core.Rules.Code;
using PrBuddy.Core.Rules.Structure;
using PrBuddy.Core.Rules.Testing;

namespace PrBuddy.Tests;

/// <summary>Third round of rules and fixes from comparing PR Buddy with human reviews of open PRs.</summary>
public sealed class ReviewGapRuleTests3
{
    static string Controller(string method) => $$"""
        using System.Threading.Tasks;
        public class UpdateController : ControllerBase
        {
            {{method}}
        }
        """;

    [Fact]
    public void ReportsUnusedBoundRequestParameterOnAPublicAction()
    {
        var after = Controller("public Task<IActionResult> Status(long id, [FromQuery] StatusRequest request) => Send(id);");
        var finding = Assert.Single(Pr.Run(new UnusedParameterRule(), Pr.File("src/UpdateController.cs", null, after)));
        Assert.Contains("[FromQuery]", finding.Message);
        Assert.Contains("'request'", finding.Message);
    }

    [Fact]
    public void ReportsUnusedParameterOfAPrivateMethod() =>
        Assert.Single(Pr.Run(new UnusedParameterRule(),
            Pr.File("src/UpdateController.cs", null, Controller("private int Total(int a, int b) { return a * 2; }"))));

    [Theory]
    [InlineData("public int Total(int a, int b) => a;")]
    [InlineData("protected override int Total(int a, int b) => a;")]
    [InlineData("private void OnClick(object sender, EventArgs e) { Run(); }")]
    [InlineData("private int Total(int a, int _) => a;")]
    [InlineData("private int Total(int a, int b) => throw new NotSupportedException();")]
    [InlineData("private int Total(int a, int b) => a + b;")]
    public void AllowsPublicApiOverridesHandlersDiscardsAndUsedParameters(string method) =>
        Assert.Empty(Pr.Run(new UnusedParameterRule(), Pr.File("src/UpdateController.cs", null, Controller(method))));

    static string Body(string statements) => $"class C {{ void M(bool ok) {{ {statements} }} }}";

    [Fact]
    public void ReportsStraySemicolonAfterABlock()
    {
        var finding = Assert.Single(Pr.Run(new StraySemicolonRule(), Pr.File("src/C.cs", null, Body("if (ok) { Run(); };"))));
        Assert.Equal(Severity.Info, finding.Severity);
    }

    [Fact]
    public void WarnsWhenTheSemicolonIsTheWholeBody()
    {
        var finding = Assert.Single(Pr.Run(new StraySemicolonRule(), Pr.File("src/C.cs", null, Body("if (ok); Run();"))));
        Assert.Equal(Severity.Warning, finding.Severity);
    }

    [Fact]
    public void AllowsForLoopHeaders() =>
        Assert.Empty(Pr.Run(new StraySemicolonRule(), Pr.File("src/C.cs", null, Body("for (;;) { break; }"))));

    [Theory]
    [InlineData("src/CoCApprovalsConfiguration.cs", "public class CocApprovalsConfiguration { }", "differs only in case")]
    [InlineData("tests/LockedFieldsValidationTests.cs", "public class LockedFieldsQueryValidatorTests { }", "Rename one")]
    public void ReportsNewFilesNamedDifferentlyFromTheirType(string path, string code, string expected) =>
        Assert.Contains(expected, Assert.Single(Pr.Run(new FileNameMismatchRule(), Pr.File(path, null, code))).Message);

    [Theory]
    [InlineData("src/Widget.cs", "public class Widget { }")]
    [InlineData("src/Widget.Designer.cs", "public class Widget { }")]
    [InlineData("src/Widgets.cs", "public partial class Widget { }")]
    [InlineData("src/Widgets.cs", "public class Widget { } public class Gadget { }")]
    [InlineData("src/Result.cs", "public class Result<T> { }")]
    public void AllowsMatchingSuffixedPartialAndMultiTypeFiles(string path, string code) =>
        Assert.Empty(Pr.Run(new FileNameMismatchRule(), Pr.File(path, null, code)));

    [Fact]
    public void IgnoresMismatchesThatWereAlreadyThere()
    {
        const string code = "public class CocApprovalsConfiguration { int X; }";
        Assert.Empty(Pr.Run(new FileNameMismatchRule(), Pr.File("src/CoCApprovalsConfiguration.cs", "public class CocApprovalsConfiguration { }", code)));
    }

    [Theory]
    [InlineData("void Stop(Apprenticeship apprenticeshipId) { }", "'apprenticeship'")]
    [InlineData("void Ship(Domain.Order? orderId) { }", "'order'")]
    public void ReportsObjectsNamedLikeIds(string method, string expected) =>
        Assert.Contains(expected, Assert.Single(Pr.Run(new MisleadingIdNameRule(),
            Pr.File("src/Handler.cs", null, $"class Handler {{ {method} }}"))).Message);

    [Theory]
    [InlineData("void Stop(long apprenticeshipId) { }")]
    [InlineData("void Stop(Guid apprenticeshipId) { }")]
    [InlineData("void Stop(ApprenticeshipId apprenticeshipId) { }")]
    [InlineData("void Stop(Customer orderId) { }")]
    [InlineData("void Stop(List<long> apprenticeshipIds) { }")]
    public void AllowsIdTypesAndUnrelatedNames(string method) =>
        Assert.Empty(Pr.Run(new MisleadingIdNameRule(), Pr.File("src/Handler.cs", null, $"class Handler {{ {method} }}")));

    static string Parser(string check) => $$"""
        using System.Text;
        class Header
        {
            const int Max = 16384;
            bool TryParse(string header)
            {
                var bytes = Encoding.UTF8.GetBytes(header);
                {{check}}
                return true;
            }
        }
        """;

    [Fact]
    public void ReportsEncodingBeforeTheLengthCheck()
    {
        var finding = Assert.Single(Pr.Run(new EncodeBeforeLengthCheckRule(),
            Pr.File("src/Header.cs", null, Parser("if (bytes.Length > Max) return false;"))));
        Assert.Contains("Encoding.UTF8.GetByteCount(header)", finding.Message);
    }

    [Theory]
    [InlineData("if (bytes.Length == 0) return false;")]
    [InlineData("Use(bytes);")]
    public void AllowsOtherUsesOfTheBytes(string check) =>
        Assert.Empty(Pr.Run(new EncodeBeforeLengthCheckRule(), Pr.File("src/Header.cs", null, Parser(check))));

    static string Task(string innerCatch, string outerCatch = "catch (Exception ex) { return ex.Handle(options); }") => $$"""
        using System;
        static class RenameTask
        {
            static Result Run(Options options)
            {
                try
                {
                    try { Connect(); }
                    {{innerCatch}}
                    return Rename();
                }
                {{outerCatch}}
            }
        }
        """;

    [Fact]
    public void ReportsInnerCatchCallingTheOuterHandler()
    {
        var after = Task("catch (Exception ex) { return new ArgumentException($\"init: {ex}\", ex).Handle(options); }");
        var finding = Assert.Single(Pr.Run(new DoubleErrorHandlingRule(), Pr.File("src/RenameTask.cs", null, after)));
        Assert.Contains("'Handle'", finding.Message);
    }

    [Theory]
    [InlineData("catch (Exception ex) { throw new ArgumentException($\"init: {ex}\", ex); }", "catch (Exception ex) { return ex.Handle(options); }")]
    [InlineData("catch (Exception ex) { return ex.Handle(options); }", "catch (IOException ex) { return ex.Handle(options); }")]
    [InlineData("catch (Exception ex) { logger.LogError(ex, \"init\"); throw; }", "catch (Exception ex) { logger.LogError(ex, \"run\"); return Failed(); }")]
    public void AllowsRethrowNarrowOuterCatchesAndLogging(string inner, string outer) =>
        Assert.Empty(Pr.Run(new DoubleErrorHandlingRule(), Pr.File("src/RenameTask.cs", null, Task(inner, outer))));

    [Fact]
    public void TestsMovedToARenamedClassInAnotherFileAreNotRemoved()
    {
        const string oldTest = "public class CallerHeaderTests { [Fact] public void Parse_Works() { Assert.Equal(1, Parse()); } }";
        const string newTest = "public class HeaderTests { [Fact] public void Parse_Works() { Assert.Equal(1, Parse(\"x\")); } }";
        Assert.Empty(Pr.Run(new WeakenedTestsRule(),
            Pr.File("tests/Common/CallerHeaderTests.cs", oldTest, null),
            Pr.File("tests/Context/HeaderTests.cs", null, newTest)));
        Assert.Contains("taking 1 test", Assert.Single(Pr.Run(new WeakenedTestsRule(),
            Pr.File("tests/Common/CallerHeaderTests.cs", oldTest, null))).Message);
    }

    [Theory]
    [InlineData("Assert.Equal(new Money(5), new Money(5));", false)]
    [InlineData("Assert.Equal(Make(), Make());", false)]
    [InlineData("var x = 1; Assert.Equal(x, x);", true)]
    public void EqualityOfTwoBuiltValuesIsARealAssertion(string body, bool trivial)
    {
        var code = $"public class MoneyTests {{ [Fact] public void Equal_ByValue() {{ {body} }} }}";
        var findings = Pr.Run(new TestWithoutAssertionsRule(), Pr.File("tests/MoneyTests.cs", null, code));
        Assert.Equal(trivial, findings.Count == 1);
    }

    [Fact]
    public void AssemblyWideSuppressionsAreReportedTogether()
    {
        const string code = """
            using System.Diagnostics.CodeAnalysis;
            [assembly: SuppressMessage("StyleCop.CSharp.ReadabilityRules", "SA1101:PrefixLocalCallsWithThis", Justification = "x")]
            [assembly: SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1200:UsingDirectivesMustBePlacedCorrectly", Justification = "x")]
            """;
        var finding = Assert.Single(Pr.Run(new SuppressedWarningsRule(), Pr.File("src/GlobalSuppressions.cs", null, code)));
        Assert.Contains("SA1101, SA1200", finding.Message);
    }
}
