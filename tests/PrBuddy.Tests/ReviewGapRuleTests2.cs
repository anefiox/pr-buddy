using PrBuddy.Core;
using PrBuddy.Core.Rules.Code;
using PrBuddy.Core.Rules.Config;
using PrBuddy.Core.Rules.Testing;

namespace PrBuddy.Tests;

/// <summary>Second round of rules from comparing PR Buddy with human reviews of open PRs.</summary>
public sealed class ReviewGapRuleTests2
{
    static string TimingTest(string body, string directive = "") => $$"""
        {{directive}}
        using System;
        using System.Diagnostics;
        using Xunit;
        public class HandleTests
        {
            [Fact]
            public void Check_IsFast()
            {
                {{body}}
            }
        }
        {{(directive.Length > 0 ? "#endif" : "")}}
        """;

    [Theory]
    [InlineData("var sw = Stopwatch.StartNew(); Run(); Assert.True(sw.ElapsedMilliseconds < 100, \"slow\");")]
    [InlineData("var sw = Stopwatch.StartNew(); Run(); var elapsed = sw.Elapsed; Assert.InRange(elapsed.TotalMilliseconds, 0, 100);")]
    [InlineData("var start = DateTime.UtcNow; Run(); Assert.True(DateTime.UtcNow - start < TimeSpan.FromSeconds(1));")]
    public void ReportsAssertionsOnMeasuredTime(string body)
    {
        var finding = Assert.Single(Pr.Run(new TimingAssertionRule(), Pr.File("tests/HandleTests.cs", null, TimingTest(body))));
        Assert.Equal("PRB106", finding.RuleId);
    }

    [Fact]
    public void SeesTestsBehindIfNet()
    {
        var body = "var sw = Stopwatch.StartNew(); Run(); Assert.True(sw.ElapsedMilliseconds < 100);";
        Assert.Single(Pr.Run(new TimingAssertionRule(), Pr.File("tests/HandleTests.cs", null, TimingTest(body, "#if NET"))));
    }

    [Theory]
    [InlineData("var deadline = Stopwatch.StartNew(); while (deadline.Elapsed < TimeSpan.FromSeconds(5)) Run(); Assert.Equal(1, Count);")]
    [InlineData("var sw = Stopwatch.StartNew(); Run(); Assert.True(Done, $\"took {sw.ElapsedMilliseconds} ms\");")]
    public void AllowsDeadlineLoopsAndTimeInMessages(string body) =>
        Assert.Empty(Pr.Run(new TimingAssertionRule(), Pr.File("tests/HandleTests.cs", null, TimingTest(body))));

    static string BoolTests(params string[] bodies) => $$"""
        using Xunit;
        public class MachineTests
        {
            {{string.Join("\n", bodies.Select((b, i) => $"[Fact] public void Test{i}() {{ {b} }}"))}}
        }
        """;

    [Fact]
    public void ReportsBareBooleanAssertsOncePerFile()
    {
        var after = BoolTests("Assert.True(a); Assert.False(b);", "Assert.True(c); Assert.True(d); Assert.True(e);");
        var finding = Assert.Single(Pr.Run(new BareBooleanAssertsRule(), Pr.File("tests/MachineTests.cs", null, after)));
        Assert.Contains("2 tests here", finding.Message);
    }

    [Theory]
    [InlineData("Assert.True(a);")]
    [InlineData("Assert.True(a, \"opened\"); Assert.False(b, \"was not open\");")]
    [InlineData("Assert.True(a); Assert.Equal(1, b);")]
    public void AllowsOneBareAssertOrMessages(string body) =>
        Assert.Empty(Pr.Run(new BareBooleanAssertsRule(), Pr.File("tests/MachineTests.cs", null, BoolTests(body))));

    static string Pump(string added) => $$"""
        using System.Threading.Tasks;
        class Pump
        {
            public async Task StartAsync() { await Task.Yield(); }
            public async Task StopAsync() { await Task.Yield(); }
            {{added}}
        }
        """;

    [Fact]
    public void ReportsMissingAsyncSuffixWhenTheTypeUsesIt()
    {
        var finding = Assert.Single(Pr.Run(new AsyncSuffixRule(),
            Pr.File("src/Pump.cs", Pump(""), Pump("private async Task RemoveCompleted() { await Task.Yield(); }"))));
        Assert.Contains("'RemoveCompletedAsync'", finding.Message);
    }

    [Theory]
    [InlineData("private async Task RemoveCompletedAsync() { await Task.Yield(); }")]
    [InlineData("public Task Handle() => Task.CompletedTask;")]
    [InlineData("protected override Task Execute() => Task.CompletedTask;")]
    [InlineData("private void Remove() { }")]
    public void AllowsSuffixedPublicOverridesAndSyncMethods(string added) =>
        Assert.Empty(Pr.Run(new AsyncSuffixRule(), Pr.File("src/Pump.cs", Pump(""), Pump(added))));

    [Fact]
    public void IgnoresTypesWithoutTheConvention()
    {
        const string after = "using System.Threading.Tasks; class C { private async Task Load() { await Task.Yield(); } }";
        Assert.Empty(Pr.Run(new AsyncSuffixRule(), Pr.File("src/C.cs", null, after)));
    }

    const string Workflow = """
        jobs:
          test:
            steps:
              - uses: actions/checkout@08eba0b27e820071cde6df949e0beb9ba4906955 # v4.3.0
              - run: dotnet test
        """;

    [Theory]
    [InlineData("        env:\n          AMQP_IMAGE: cloudamqp/lavinmq:2.10.4", "cloudamqp/lavinmq:2.10.4")]
    [InlineData("    container: postgres:16", "postgres:16")]
    [InlineData("      - uses: dorny/test-reporter@v3", "dorny/test-reporter@v3")]
    [InlineData("      - uses: docker://alpine:3.20", "alpine:3.20")]
    public void ReportsUnpinnedImagesAndActionsAsWarningsWhenTheFilePins(string added, string reference)
    {
        var finding = Assert.Single(Pr.Run(new UnpinnedWorkflowDependencyRule(),
            Pr.File(".github/workflows/test.yml", Workflow, Workflow + "\n" + added)));
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains(reference, finding.Message);
    }

    [Theory]
    [InlineData("      - uses: ./.github/actions/setup")]
    [InlineData("      - uses: dorny/test-reporter@a43b3a5f7366b97d083190328d2c652e1a8b6aa2 # v3.0.0")]
    [InlineData("    container: postgres@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("    container: ${{ matrix.image }}")]
    [InlineData("      # image: postgres:16")]
    public void AllowsPinnedLocalAndExpressionReferences(string added) =>
        Assert.Empty(Pr.Run(new UnpinnedWorkflowDependencyRule(),
            Pr.File(".github/workflows/test.yml", Workflow, Workflow + "\n" + added)));

    [Fact]
    public void UnpinnedActionInAFileThatPinsNothingIsInfo()
    {
        const string after = "jobs:\n  b:\n    steps:\n      - uses: actions/checkout@v4\n";
        var finding = Assert.Single(Pr.Run(new UnpinnedWorkflowDependencyRule(), Pr.File(".github/workflows/ci.yml", null, after)));
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Empty(Pr.Run(new UnpinnedWorkflowDependencyRule(), Pr.File("docs/ci.yml", null, after)));
    }
}
