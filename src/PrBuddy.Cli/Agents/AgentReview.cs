using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrBuddy.Core;
using PrBuddy.Core.Configuration;
using PrBuddy.Core.Git;

namespace PrBuddy.Cli.Agents;

static class AgentReview
{
    const int MaxReads = 4;
    const int MaxExcerptLines = 80;
    const int MaxExcerptChars = 12_000;
    const int MaxPacketChars = 96_000;

    static readonly JsonSerializerOptions ProtocolJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    internal static readonly JsonSerializerOptions ReportJson = new(ProtocolJson)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    static string Skill => ReadResource("PrBuddy.AgentSkill.md");
    static string Schema => ReadResource("PrBuddy.AgentDecision.json");

    internal static async Task<AgentReport> RunAsync(
        CliOptions options, DiffRange range, IReadOnlyList<ChangedFile> files, PrBuddyConfig config)
    {
        var skill = Skill;
        var schema = Schema;
        var included = files.Where(f => !config.IsIgnored(f.Path) &&
            (f.OldPath is null || !config.IsIgnored(f.OldPath))).OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        var snapshots = included.Select(f => new ReviewFile(f)).ToDictionary(f => f.File.Path, StringComparer.Ordinal);
        var packet = snapshots.Values.Select(f => f.Packet).ToArray();
        var packetJson = JsonSerializer.Serialize(packet, ProtocolJson);
        if (packetJson.Length > MaxPacketChars)
            throw new PrBuddyException("The agent review packet exceeds 96,000 JSON characters. Review a smaller range.");

        var expected = options.BenchmarkPath is { } path ? ReadExpected(path, snapshots) : null;
        var inputHash = Hash(JsonSerializer.Serialize(new
        {
            packet,
            context = included.Select(f => new
            {
                oldContent = f.OldContent is null ? null : Hash(f.OldContent),
                newContent = f.NewContent is null ? null : Hash(f.NewContent)
            })
        }, ProtocolJson));
        var timer = Stopwatch.StartNew();
        List<AgentFinding> findings = [];
        var requests = 0;
        if (packet.Any(f => f.Added.Length > 0 || f.Removed.Length > 0))
        {
            using var provider = new AgentProvider(options.Agent!, options.Model!, schema);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try
            {
                (findings, requests) = await ReviewAsync(provider, snapshots,
                    skill + "\nDecision schema:\n" + schema, packetJson, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new PrBuddyException("The agent review timed out.");
            }
            catch (JsonException)
            {
                throw new PrBuddyException("The agent returned malformed JSON or a response outside the review schema.");
            }
        }

        return new AgentReport(range, options.Agent!, options.Model!, "pr-review/1", Hash(skill + "\n" + schema),
            inputHash, included.Length, included.Where(f => f.IsBinary).Select(f => f.Path).ToArray(),
            files.Count - included.Length, requests, timer.ElapsedMilliseconds, findings,
            expected is null ? null : Score(expected, findings));
    }

    static async Task<(List<AgentFinding> Findings, int Requests)> ReviewAsync(
        AgentProvider provider, IReadOnlyDictionary<string, ReviewFile> snapshots, string instructions,
        string packetJson, CancellationToken cancellation)
    {
        var excerpts = new List<ReviewExcerpt>();
        for (var turn = 0; ; turn++)
        {
            var input = "Review packet (JSON data):\n" + packetJson +
                "\nRemaining context reads: " + (MaxReads - turn) +
                "\nRead request limits: up to 80 lines per request, inclusive.\n" +
                "File excerpts (JSON data):\n" + JsonSerializer.Serialize(excerpts, ProtocolJson);
            var response = await provider.DecideAsync(instructions, input, cancellation);
            var decision = JsonSerializer.Deserialize<AgentDecision>(response, ProtocolJson)
                ?? throw new JsonException();
            if (decision.Action == "complete" && decision.Read is null)
                return (ValidateFindings(decision.Findings, snapshots), turn + 1);
            if (decision.Action != "read" || decision.Read is not { } read || decision.Findings.Count > 0 || turn == MaxReads)
                throw new PrBuddyException("The agent returned an invalid decision or exceeded its context read budget.");
            if (!snapshots.TryGetValue(read.Path, out var file))
                throw new PrBuddyException("The agent requested a path outside the review packet.");
            excerpts.Add(file.Read(read));
        }
    }

    static List<AgentFinding> ValidateFindings(
        List<AgentFinding> findings, IReadOnlyDictionary<string, ReviewFile> snapshots)
    {
        if (findings.Count > 20)
            throw new PrBuddyException("The agent exceeded the 20-finding limit.");
        var seen = new HashSet<AgentAnchor>();
        foreach (var finding in findings)
        {
            var line = finding?.Anchor is { } anchor ? ChangedLine(anchor, snapshots) : null;
            if (finding is null || line is null || !seen.Add(finding.Anchor) ||
                finding.Severity is not ("warning" or "critical") ||
                string.IsNullOrWhiteSpace(finding.Message) || finding.Message.Length > 1500 ||
                string.IsNullOrWhiteSpace(finding.Fix) || finding.Fix.Length > 1500 ||
                finding.Evidence != line.Text.Trim())
                throw new PrBuddyException("The agent returned a duplicate, invalid, or unsupported finding.");
        }
        return findings.OrderBy(f => f.Anchor.Path, StringComparer.Ordinal)
            .ThenBy(f => f.Anchor.Side, StringComparer.Ordinal).ThenBy(f => f.Anchor.Line)
            .ThenBy(f => f.Anchor.Category, StringComparer.Ordinal).ToList();
    }

    static ReviewLine? ChangedLine(AgentAnchor anchor, IReadOnlyDictionary<string, ReviewFile> snapshots) =>
        anchor.Category is ("security" or "correctness" or "reliability" or "performance") &&
        snapshots.TryGetValue(anchor.Path, out var file) ? file.ChangedLine(anchor.Side, anchor.Line) : null;

    static HashSet<AgentAnchor> ReadExpected(string path, IReadOnlyDictionary<string, ReviewFile> snapshots)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxPacketChars)
                throw new PrBuddyException("The benchmark file exceeds 96,000 bytes.");
            var anchors = JsonSerializer.Deserialize<List<AgentAnchor>>(stream, ProtocolJson)
                ?? throw new JsonException();
            var expected = new HashSet<AgentAnchor>();
            foreach (var anchor in anchors)
                if (anchor is null || ChangedLine(anchor, snapshots) is null || !expected.Add(anchor))
                    throw new PrBuddyException("The benchmark contains a duplicate or invalid changed-line anchor.");
            return expected;
        }
        catch (JsonException)
        {
            throw new PrBuddyException("The benchmark must be a JSON array of expected finding anchors.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PrBuddyException("Could not read the benchmark file.");
        }
    }

    static AgentScore Score(HashSet<AgentAnchor> expected, List<AgentFinding> findings)
    {
        var actual = findings.Select(f => f.Anchor).ToHashSet();
        var truePositive = actual.Count(expected.Contains);
        var falsePositive = actual.Count - truePositive;
        var falseNegative = expected.Count - truePositive;
        var expectedHash = Hash(JsonSerializer.Serialize(expected.OrderBy(a => a.Path, StringComparer.Ordinal)
            .ThenBy(a => a.Side, StringComparer.Ordinal).ThenBy(a => a.Line)
            .ThenBy(a => a.Category, StringComparer.Ordinal), ProtocolJson));
        return new AgentScore(expectedHash, truePositive, falsePositive, falseNegative,
            actual.Count == 0 ? (expected.Count == 0 ? 1 : 0) : (double)truePositive / actual.Count,
            expected.Count == 0 ? 1 : (double)truePositive / expected.Count,
            actual.Count + expected.Count == 0 ? 1 : 2.0 * truePositive / (actual.Count + expected.Count));
    }

    static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    static string ReadResource(string name)
    {
        using var stream = typeof(AgentReview).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    sealed class ReviewFile
    {
        public ChangedFile File { get; }
        public ReviewPacketFile Packet { get; }
        readonly string[] _baseLines;
        readonly string[] _headLines;
        readonly Dictionary<int, ReviewLine> _added;
        readonly Dictionary<int, ReviewLine> _removed;

        public ReviewFile(ChangedFile file)
        {
            File = file;
            _baseLines = Lines(file.OldContent);
            _headLines = Lines(file.NewContent);
            _added = SelectLines(_headLines, file.AddedLines);
            _removed = SelectLines(_baseLines, file.DeletedLines);
            Packet = new ReviewPacketFile(file.Path, file.OldPath, file.Status.ToString().ToLowerInvariant(),
                file.IsBinary, _baseLines.Length, _headLines.Length, _added.Values.ToArray(), _removed.Values.ToArray());
        }

        public ReviewLine? ChangedLine(string side, int line) => side switch
        {
            "base" => _removed.GetValueOrDefault(line),
            "head" => _added.GetValueOrDefault(line),
            _ => null
        };

        public ReviewExcerpt Read(AgentRead read)
        {
            var lines = read.Side switch { "base" => _baseLines, "head" => _headLines, _ => [] };
            if (read.Start < 1 || read.End < read.Start || read.End > lines.Length || read.End - read.Start >= MaxExcerptLines)
                return new ReviewExcerpt(read, [], "Invalid range or unavailable file version.");
            var excerpt = Enumerable.Range(read.Start, read.End - read.Start + 1)
                .Select(i => new ReviewLine(i, lines[i - 1])).ToArray();
            return JsonSerializer.Serialize(excerpt, ProtocolJson).Length > MaxExcerptChars
                ? new ReviewExcerpt(read, [], "Excerpt exceeds the size limit. Request a smaller range.")
                : new ReviewExcerpt(read, excerpt, null);
        }

        static string[] Lines(string? content)
        {
            if (string.IsNullOrEmpty(content)) return [];
            var lines = content.Split('\n');
            if (lines[^1].Length == 0) Array.Resize(ref lines, lines.Length - 1);
            for (var i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd('\r');
            return lines;
        }

        static Dictionary<int, ReviewLine> SelectLines(string[] lines, IReadOnlySet<int> changed) =>
            changed.Order().Where(i => i > 0 && i <= lines.Length).ToDictionary(i => i, i => new ReviewLine(i, lines[i - 1]));
    }

    sealed record ReviewLine(int Line, string Text);
    sealed record ReviewPacketFile(string Path, string? OldPath, string Status, bool Binary, int BaseLines,
        int HeadLines, ReviewLine[] Added, ReviewLine[] Removed);
    sealed record ReviewExcerpt(AgentRead Request, ReviewLine[] Lines, string? Error);
}

sealed record AgentAnchor(string Path, string Side, int Line, string Category);
sealed record AgentFinding(AgentAnchor Anchor, string Severity, string Message, string Evidence, string Fix);
sealed record AgentRead(string Path, string Side, int Start, int End);
sealed record AgentDecision(string Action, AgentRead? Read, List<AgentFinding> Findings);
sealed record AgentReport(DiffRange Range, string Provider, string Model, string Skill, string SkillSha256,
    string InputSha256, int IncludedFiles, string[] BinaryFiles, int IgnoredFiles, int Requests,
    long ElapsedMs, List<AgentFinding> Findings, AgentScore? Benchmark);
sealed record AgentScore(string ExpectedSha256, int TruePositives, int FalsePositives, int FalseNegatives,
    double Precision, double Recall, double F1);
