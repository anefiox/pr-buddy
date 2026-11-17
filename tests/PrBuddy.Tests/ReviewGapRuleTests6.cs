using PrBuddy.Core;
using PrBuddy.Core.Rules.Code;
using PrBuddy.Core.Rules.Testing;

namespace PrBuddy.Tests;

/// <summary>Sixth round of rules and fixes from comparing PR Buddy with human reviews of open PRs.</summary>
public sealed class ReviewGapRuleTests6
{
    const string MigrationPath = "src/Ordering/Migrations/20260607000000_RemovePaymentData.cs";
    const string Migration = "namespace Ordering.Migrations { public partial class RemovePaymentData : Migration { protected override void Up(MigrationBuilder b) { } } }";

    [Fact]
    public void ReportsMigrationWithoutDesignerOrAttribute()
    {
        var finding = Assert.Single(Pr.Run(new UndiscoverableMigrationRule(), Pr.File(MigrationPath, null, Migration)));
        Assert.Contains("20260607000000_RemovePaymentData.Designer.cs", finding.Message);
    }

    [Fact]
    public void AllowsMigrationWithItsDesignerFile() =>
        Assert.Empty(Pr.Run(new UndiscoverableMigrationRule(),
            Pr.File(MigrationPath, null, Migration),
            Pr.File(MigrationPath.Replace(".cs", ".Designer.cs"), null, "[DbContext(typeof(OrderingContext))] [Migration(\"20260607000000_RemovePaymentData\")] partial class RemovePaymentData { }")));

    [Fact]
    public void AllowsMigrationThatCarriesTheAttributeItself() =>
        Assert.Empty(Pr.Run(new UndiscoverableMigrationRule(),
            Pr.File(MigrationPath, null, Migration.Replace("public partial class", "[Migration(\"20260607000000_RemovePaymentData\")] public partial class"))));

    [Fact]
    public void FindsTheDesignerElsewhereInTheRepo() =>
        Assert.Empty(Pr.RunInRepo(new UndiscoverableMigrationRule(),
            new Dictionary<string, string> { [MigrationPath.Replace(".cs", ".Designer.cs")] = "partial class RemovePaymentData { }" },
            Pr.File(MigrationPath, null, Migration)));

    static string AsyncTest(string body, string usings = "using Xunit;") => $$"""
        {{usings}}
        public class HandlerTests
        {
            [Fact]
            public void Fails() { {{body}} }
        }
        """;

    [Theory]
    [InlineData("Assert.ThrowsAsync<InvalidOperationException>(() => handler.SendAsync());")]
    [InlineData("act.Should().ThrowAsync<InvalidOperationException>();")]
    [InlineData("act.ShouldThrowAsync<InvalidOperationException>();")]
    public void ReportsUnawaitedAsyncAssertions(string body)
    {
        var finding = Assert.Single(Pr.Run(new UnawaitedAsyncAssertionRule(), Pr.File("tests/HandlerTests.cs", null, AsyncTest(body))));
        Assert.Equal(Severity.Warning, finding.Severity);
    }

    [Fact]
    public void NUnitAssertIsInfo()
    {
        var code = AsyncTest("Assert.ThrowsAsync<InvalidOperationException>(async () => await handler.SendAsync());", "using NUnit.Framework;");
        var finding = Assert.Single(Pr.Run(new UnawaitedAsyncAssertionRule(), Pr.File("tests/HandlerTests.cs", null, code)));
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("NUnit 5", finding.Message);
    }

    [Theory]
    [InlineData("await Assert.ThrowsAsync<InvalidOperationException>(() => handler.SendAsync());")]
    [InlineData("observer.Setup(o => o.FailAsync()).ThrowsAsync(new InvalidOperationException());")]
    [InlineData("facade.ListSubscriptions(Arg.Any<Options>()).ThrowsAsync(new StripeException());")]
    [InlineData("var task = Assert.ThrowsAsync<InvalidOperationException>(() => handler.SendAsync()); await task;")]
    public void AllowsAwaitedAssertionsAndMockSetups(string body) =>
        Assert.Empty(Pr.Run(new UnawaitedAsyncAssertionRule(), Pr.File("tests/HandlerTests.cs", null, AsyncTest(body))));

    [Fact]
    public void ReflectionLookupsAreReportedOncePerFile()
    {
        const string code = """
            using System.Reflection;
            public class ExecutionTests
            {
                [Fact] public void A() { var f = typeof(Execution).GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance); }
                [Fact] public void B() { var m = typeof(Execution).GetMethod("SendAsync", BindingFlags.NonPublic | BindingFlags.Instance); }
            }
            """;
        var finding = Assert.Single(Pr.Run(new ReflectionIntoPrivatesRule(), Pr.File("tests/ExecutionTests.cs", null, code)));
        Assert.Contains("2 times ('_connection', 'SendAsync')", finding.Message);
    }
}
