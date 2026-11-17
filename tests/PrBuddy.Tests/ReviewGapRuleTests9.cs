using PrBuddy.Core.Rules.Code;

namespace PrBuddy.Tests;

/// <summary>Ninth round of rules from comparing PR Buddy with human reviews of open PRs.</summary>
public sealed class ReviewGapRuleTests9
{
    const string Service = """
        using System.Threading;
        public interface IJsonRpcService
        {
            ValueTask<Response> SendRequestAsync(Request request, Context context, CancellationToken cancellationToken);
        }
        """;

    static string Callers(int count, string token = "CancellationToken.None") =>
        "class Tests { async Task Run(IJsonRpcService s) { " +
        string.Concat(Enumerable.Repeat($"await s.SendRequestAsync(r, c, {token}); ", count)) + "} }";

    [Theory]
    [InlineData("CancellationToken.None")]
    [InlineData("default")]
    [InlineData("default(CancellationToken)")]
    public void ReportsTokenThatCallersKeepFillingWithNothing(string token)
    {
        var finding = Assert.Single(Pr.Run(new TokenWithoutDefaultRule(),
            Pr.File("src/IJsonRpcService.cs", null, Service), Pr.File("tests/RpcTests.cs", null, Callers(3, token))));
        Assert.Contains("3 calls in this PR", finding.Message);
        Assert.Contains("CancellationToken cancellationToken = default", finding.Message);
    }

    [Fact]
    public void AllowsTokensThatCallersPassForReal() =>
        Assert.Empty(Pr.Run(new TokenWithoutDefaultRule(),
            Pr.File("src/IJsonRpcService.cs", null, Service), Pr.File("tests/RpcTests.cs", null, Callers(5, "cts.Token"))));

    [Fact]
    public void AllowsFewEmptyCallsAndExistingDefaults()
    {
        Assert.Empty(Pr.Run(new TokenWithoutDefaultRule(),
            Pr.File("src/IJsonRpcService.cs", null, Service), Pr.File("tests/RpcTests.cs", null, Callers(2))));
        Assert.Empty(Pr.Run(new TokenWithoutDefaultRule(),
            Pr.File("src/IJsonRpcService.cs", null, Service.Replace("cancellationToken);", "cancellationToken = default);")),
            Pr.File("tests/RpcTests.cs", null, Callers(5))));
    }

    static string Call(string statement) => $"class Watcher {{ void Build(string id, string rid) {{ {statement} }} }}";

    [Theory]
    [InlineData("var device = new DeviceInfo(id, null, null, null, rid);", "3 unnamed null")]
    [InlineData("Start(id, true, false);", "true/false")]
    public void ReportsUnnamedLiteralArguments(string statement, string expected) =>
        Assert.Contains(expected, Assert.Single(Pr.Run(new UnnamedLiteralArgumentsRule(), Pr.File("src/Watcher.cs", null, Call(statement)))).Message);

    [Theory]
    [InlineData("var device = new DeviceInfo(id, name: null, model: null, rid);")]
    [InlineData("var device = new DeviceInfo(id, null, rid);")]
    [InlineData("Interlocked.CompareExchange(ref _flag, true, false);")]
    [InlineData("method.Invoke(null, null);")]
    public void AllowsNamedSingleAndValueStyleCalls(string statement) =>
        Assert.Empty(Pr.Run(new UnnamedLiteralArgumentsRule(), Pr.File("src/Watcher.cs", null, Call(statement))));

    [Fact]
    public void UnnamedLiteralsInTestsAreFine() =>
        Assert.Empty(Pr.Run(new UnnamedLiteralArgumentsRule(), Pr.File("tests/WatcherTests.cs", null, Call("Start(id, true, false);"))));
}
