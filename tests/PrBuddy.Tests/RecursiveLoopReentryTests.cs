using PrBuddy.Core;
using PrBuddy.Core.Rules.Code;

namespace PrBuddy.Tests;

public sealed class RecursiveLoopReentryTests
{
    const string WithReentry = """
        class Traversal
        {
            int Walk(int address, int depth = 0)
            {
                while (true) { return RunStep(address, depth); }
            }

            int RunStep(int address, int depth)
            {
                while (address > 0)
                {
                    var next = ExternalDecoder.Read(address);
                    return Walk(next.Target, depth: depth + 1);
                }
                return 0;
            }
        }
        """;

    [Fact]
    public void ReportsOneCycleAtTheRecursiveBranchEvenWithAnUnresolvedArgument()
    {
        var before = WithReentry.Replace("return Walk(next.Target, depth: depth + 1);", "return address - 1;");
        var finding = Assert.Single(Review(before, WithReentry));
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal(13, finding.Line);
        Assert.Contains("The loop in 'Walk' calls 'RunStep'", finding.Message);
    }

    [Fact]
    public void AnchorsOnNewDispatchWhenTheHelperAlreadyCallsBack()
    {
        var before = WithReentry.Replace("return RunStep(address, depth);", "return address;");
        Assert.Equal(5, Assert.Single(Review(before, WithReentry)).Line);
    }

    [Fact]
    public void IgnoresExistingCycleWhenAnArgumentChanges()
    {
        var after = WithReentry.Replace("depth + 1", "depth + 2");
        Assert.Empty(Review(WithReentry, after));
    }

    [Theory]
    [InlineData("return Walk(address.ToString());")]
    [InlineData("return new Traversal().Walk(address);")]
    [InlineData("System.Func<int> deferred = () => Walk(address); return 0;")]
    [InlineData("return RunStep(address - 1);")]
    public void IgnoresOtherOverloadsInstancesDeferredCallsAndSelfRecursion(string body)
    {
        var after = $$"""
            class Traversal
            {
                int Walk(string text) => text.Length;
                int Walk(int address)
                {
                    while (true) { return RunStep(address); }
                }
                int RunStep(int address) { {{body}} }
            }
            """;

        Assert.Empty(Review(null, after));
    }

    static IReadOnlyList<Finding> Review(string? before, string after) =>
        Pr.Run(new RecursiveLoopReentryRule(), Pr.File("src/Traversal.cs", before, after));
}
