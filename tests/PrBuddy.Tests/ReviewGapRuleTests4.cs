using PrBuddy.Core;
using PrBuddy.Core.Rules.Code;
using PrBuddy.Core.Rules.Structure;
using PrBuddy.Core.Rules.Testing;

namespace PrBuddy.Tests;

/// <summary>Fourth round of rules and fixes from comparing PR Buddy with human reviews of open PRs.</summary>
public sealed class ReviewGapRuleTests4
{
    static string Responder(string check) => $$"""
        class Responder
        {
            const string EventStream = "text/event-stream";
            bool IsStream(string mediaType) => {{check}};
        }
        """;

    [Fact]
    public void ReportsContainsNextToAnExactCheckOfTheSameValue()
    {
        var after = Responder("mediaType.Equals(EventStream, StringComparison.OrdinalIgnoreCase) || mediaType.Contains(EventStream, StringComparison.OrdinalIgnoreCase)");
        var finding = Assert.Single(Pr.Run(new LooseMediaTypeMatchRule(), Pr.File("src/Responder.cs", null, after)));
        Assert.Contains("exact check", finding.Message);
    }

    [Fact]
    public void ReportsContainsOnAMediaType() =>
        Assert.Contains("Equals", Assert.Single(Pr.Run(new LooseMediaTypeMatchRule(),
            Pr.File("src/Responder.cs", null, Responder("mediaType.Contains(EventStream)")))).Message);

    [Theory]
    [InlineData("mediaType.Equals(EventStream, StringComparison.OrdinalIgnoreCase)")]
    [InlineData("mediaType.Contains(';')")]
    [InlineData("name.Contains(EventStream)")]
    public void AllowsExactChecksSeparatorsAndOtherValues(string check) =>
        Assert.Empty(Pr.Run(new LooseMediaTypeMatchRule(), Pr.File("src/Responder.cs", null, Responder(check))));

    static string HostTest(string url) => $$"""
        public class ClientTests
        {
            [Fact] public void Connects() { var client = new Client("{{url}}"); Assert.NotNull(client); }
        }
        """;

    [Theory]
    [InlineData("https://api.parse.com/1/", "api.parse.com")]
    [InlineData("<script src='https://cdnjs.cloudflare.com/ajax/libs/x.js'></script>", "cdnjs.cloudflare.com")]
    [InlineData("wss://echo.websocket.org", "echo.websocket.org")]
    public void ReportsRealHostsInTests(string url, string host) =>
        Assert.Contains(host, Assert.Single(Pr.Run(new ExternalHostInTestRule(), Pr.File("tests/ClientTests.cs", null, HostTest(url)))).Message);

    [Theory]
    [InlineData("https://api.example.com/1/")]
    [InlineData("http://localhost:5000/sse")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("https://server.test/x")]
    [InlineData("http://10.0.0.5/api")]
    [InlineData("http://www.w3.org/2001/XMLSchema")]
    public void AllowsReservedLocalAndSchemaHosts(string url) =>
        Assert.Empty(Pr.Run(new ExternalHostInTestRule(), Pr.File("tests/ClientTests.cs", null, HostTest(url))));

    [Fact]
    public void ExternalHostsInProductionCodeAreNotThisRulesBusiness() =>
        Assert.Empty(Pr.Run(new ExternalHostInTestRule(), Pr.File("src/Client.cs", null, HostTest("https://api.parse.com/"))));

    static string Holder(string property, string fieldModifiers = "private") => $$"""
        class Connection
        {
            {{fieldModifiers}} Capture _capture;
            {{property}}
            void Reset() { _capture = new Capture(); }
        }
        """;

    [Theory]
    [InlineData("internal Capture Capture => _capture;", "{ get; private set; }")]
    [InlineData("internal Capture Capture { get => _capture; set => _capture = value; }", "{ get; set; }")]
    [InlineData("internal Capture Capture { get { return _capture; } }", "{ get; private set; }")]
    public void ReportsPropertiesThatOnlyWrapAField(string property, string shape) =>
        Assert.Contains(shape, Assert.Single(Pr.Run(new AutoPropertyCandidateRule(), Pr.File("src/Connection.cs", null, Holder(property)))).Message);

    [Theory]
    [InlineData("internal Capture Capture => _capture ?? new Capture();", "private")]
    [InlineData("internal Capture Capture { get => _capture; set { _capture = value; Changed(); } }", "private")]
    [InlineData("internal Capture Capture => _capture;", "private volatile")]
    [InlineData("internal Capture Capture => _capture; void Swap() => Interlocked.Exchange(ref _capture, null);", "private")]
    public void AllowsLogicVolatileAndRefUse(string property, string fieldModifiers) =>
        Assert.Empty(Pr.Run(new AutoPropertyCandidateRule(), Pr.File("src/Connection.cs", null, Holder(property, fieldModifiers))));

    const string OldResponder = "public class Responder { public Responder(IHeaders headers) { } }";
    const string NewResponder = "public class Responder { public Responder(IHeaders headers, ILoggerFactory loggers) { } }";

    [Fact]
    public void ReportsChangedPublicConstructorInALibrary()
    {
        var finding = Assert.Single(Pr.RunInRepo(new PublicApiBreakRule(),
            new Dictionary<string, string> { ["src/Lib/Lib.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>" },
            Pr.File("src/Lib/Responder.cs", OldResponder, NewResponder)));
        Assert.Contains("Responder.Responder(IHeaders) became Responder.Responder(IHeaders, ILoggerFactory)", finding.Message);
    }

    [Theory]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>")]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>")]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>")]
    public void SkipsAppsAndUnpackedProjects(string project) =>
        Assert.Empty(Pr.RunInRepo(new PublicApiBreakRule(),
            new Dictionary<string, string> { ["src/App/App.csproj"] = project },
            Pr.File("src/App/Responder.cs", OldResponder, NewResponder)));

    [Fact]
    public void ManyBrokenFilesAreReportedOnce()
    {
        var files = Enumerable.Range(1, 5).Select(i => Pr.File($"src/Lib/R{i}.cs",
            OldResponder.Replace("Responder", $"R{i}"), NewResponder.Replace("Responder", $"R{i}"))).ToArray();
        var finding = Assert.Single(Pr.Run(new PublicApiBreakRule(), files));
        Assert.Null(finding.Path);
        Assert.Contains("across 5 files", finding.Message);
    }

    [Fact]
    public void NotImplementedInATestDoubleIsFine()
    {
        const string fake = "class FakeServer : IServer { public void Dispose() => throw new NotImplementedException(); }";
        Assert.Empty(Pr.Run(new PlaceholderRule(), Pr.File("tests/ResponderTests.cs", null, fake)));
        Assert.Single(Pr.Run(new PlaceholderRule(), Pr.File("src/Server.cs", null, fake)));
    }

    [Fact]
    public void AssertionsInHelperMethodsCount()
    {
        const string test = """
            public class SseTests
            {
                [Fact]
                public void Streams() => this.Given(x => GivenAServer()).Then(x => ThenEventsArrive()).BDDfy();
                private void GivenAServer() { }
                private void ThenEventsArrive() => CheckCount();
                private void CheckCount() => _events.Count.ShouldBe(2);
            }
            """;
        Assert.Empty(Pr.Run(new TestWithoutAssertionsRule(), Pr.File("tests/SseTests.cs", null, test)));
        Assert.Single(Pr.Run(new TestWithoutAssertionsRule(),
            Pr.File("tests/SseTests.cs", null, test.Replace("_events.Count.ShouldBe(2)", "Log()"))));
    }
}
