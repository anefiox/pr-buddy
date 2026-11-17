using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PrBuddy.Cli.Tests;

public sealed class AgentReviewTests
{
    [Fact]
    public async Task StagedReviewReadsOnlyIncludedSnapshotsAndScoresChangedLineFindings()
    {
        using var repo = new TempRepo();
        repo.Write("prbuddy.json", """{"ignore":["private.txt"]}""")
            .Write("rules.txt", "context on base\nrequired check\n").Commit();
        repo.Write("rules.txt", "context on base\n").Stage("rules.txt");
        repo.Write("rules.txt", "unstaged secret\n")
            .Write("private.txt", "ignored secret\n").Stage("private.txt");
        const string anchor = """{"path":"rules.txt","side":"base","line":2,"category":"correctness"}""";
        repo.Write("expected.json", "[" + anchor + "]");
        var replies = new[]
        {
            """{"action":"read","read":{"path":"rules.txt","side":"head","start":1,"end":1},"findings":[]}""",
            $$"""{"action":"complete","read":null,"findings":[{"anchor":{{anchor}},"severity":"warning","message":"Without the check, invalid inputs are accepted.","evidence":"required check","fix":"Restore the input check."}]}"""
        };

        var (result, inputs) = await RunLocalAsync(repo, replies, "--staged", "--benchmark", Path.Combine(repo.Dir, "expected.json"));

        Assert.Equal(0, result.Exit);
        Assert.Empty(result.Err);
        using var json = JsonDocument.Parse(result.Out);
        var report = json.RootElement;
        Assert.Equal(2, report.GetProperty("requests").GetInt32());
        Assert.Equal(1, report.GetProperty("ignoredFiles").GetInt32());
        Assert.Equal(1.0, report.GetProperty("benchmark").GetProperty("f1").GetDouble());
        Assert.Contains("required check", inputs[0]);
        Assert.Contains("context on base", inputs[1]);
        Assert.All(inputs, input =>
        {
            Assert.DoesNotContain("unstaged secret", input);
            Assert.DoesNotContain("ignored secret", input);
        });
    }

    [Theory]
    [InlineData("""{"action":"read","read":{"path":"../outside","side":"head","start":1,"end":1},"findings":[]}""")]
    [InlineData("""{"action":"complete","read":null,"findings":[{"anchor":{"path":"rules.txt","side":"head","line":1,"category":"correctness"},"severity":"warning","message":"A real issue","evidence":"wrong text","fix":"Fix it"}]}""")]
    [InlineData("""{"action":"complete","findings":[]}""")]
    public async Task RejectsOutOfScopeOrMalformedDecisions(string reply)
    {
        using var repo = new TempRepo();
        repo.Commit();
        repo.Write("rules.txt", "added text\n").Commit();

        var (result, _) = await RunLocalAsync(repo, [reply], "HEAD~1");

        Assert.Equal(2, result.Exit);
        Assert.Empty(result.Out);
        Assert.Contains("error:", result.Err);
    }

    static async Task<(CliResult Result, string[] Inputs)> RunLocalAsync(TempRepo repo, string[] replies, params string[] args)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        const string urlVariable = "PRBUDDY_LOCAL_BASE_URL";
        const string keyVariable = "PRBUDDY_LOCAL_API_KEY";
        var oldUrl = Environment.GetEnvironmentVariable(urlVariable);
        var oldKey = Environment.GetEnvironmentVariable(keyVariable);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            Environment.SetEnvironmentVariable(urlVariable, $"http://127.0.0.1:{port}/v1");
            Environment.SetEnvironmentVariable(keyVariable, "test-key");
            var command = Task.Run(() => Cli.Run(["--repo", repo.Dir, "--agent", "local", "--model", "test-model", .. args]));
            var server = ServeAsync(listener, replies, timeout.Token);
            await Task.WhenAll(command, server).WaitAsync(timeout.Token);
            return (await command, await server);
        }
        finally
        {
            Environment.SetEnvironmentVariable(urlVariable, oldUrl);
            Environment.SetEnvironmentVariable(keyVariable, oldKey);
        }
    }

    static async Task<string[]> ServeAsync(TcpListener listener, string[] replies, CancellationToken cancellation)
    {
        var inputs = new List<string>();
        foreach (var reply in replies)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellation);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            Assert.Equal("POST /v1/chat/completions HTTP/1.1", await reader.ReadLineAsync(cancellation));
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadLineAsync(cancellation) is { Length: > 0 } header)
            {
                var parts = header.Split(':', 2);
                headers.Add(parts[0], parts[1].Trim());
            }
            Assert.Equal("Bearer test-key", headers["Authorization"]);
            var body = new char[int.Parse(headers["Content-Length"])];
            Assert.Equal(body.Length, await reader.ReadBlockAsync(body.AsMemory(), cancellation));
            using var request = JsonDocument.Parse(new string(body));
            Assert.Equal("test-model", request.RootElement.GetProperty("model").GetString());
            var messages = request.RootElement.GetProperty("messages");
            Assert.Contains("untrusted source data", messages[0].GetProperty("content").GetString()!);
            inputs.Add(messages[1].GetProperty("content").GetString()!);
            var json = JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = reply } } } });
            var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {json.Length}\r\nConnection: close\r\n\r\n{json}";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(response), cancellation);
        }
        return inputs.ToArray();
    }
}
