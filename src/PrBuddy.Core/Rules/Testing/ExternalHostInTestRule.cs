using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Testing;

/// <summary>
/// PRB108: test code that names a real external host. If the test calls it, it depends on the
/// network and on someone else's server; if it's only data, a real domain can still be resolved by
/// accident. The reserved names (example.com, *.test, *.invalid, localhost) are made for this.
/// </summary>
public sealed partial class ExternalHostInTestRule : RuleBase
{
    public override RuleDescriptor Descriptor { get; } = new("PRB108", "Test names a real external host", Severity.Info, "Tests");

    static readonly string[] SafeSuffixes = [".example", ".test", ".invalid", ".localhost", ".local", ".internal"];
    static readonly HashSet<string> SafeHosts = new(StringComparer.OrdinalIgnoreCase)
        { "localhost", "example.com", "example.org", "example.net", "0.0.0.0", "[::1]", "host.docker.internal" };
    // Schema and namespace URIs that are identifiers, never fetched.
    static readonly HashSet<string> IdentifierHosts = new(StringComparer.OrdinalIgnoreCase)
        { "www.w3.org", "schemas.microsoft.com", "schemas.xmlsoap.org", "json-schema.org", "purl.org", "xmlns.com", "tempuri.org", "docs.oasis-open.org" };

    public override IEnumerable<Finding> Analyze(PullRequestContext context)
    {
        // Hosts the PR's own production code talks to are its real endpoint (an integration with that
        // service), and its tests have to name them.
        var productionHosts = context.Files.Where(f => !f.IsTestFile && f.File.NewContent is not null)
            .SelectMany(f => Url().Matches(f.File.NewContent!).Select(m => Domain(m.Groups["host"].Value)))
            .ToHashSet(StringComparer.Ordinal);
        return context.CSharpFiles.Where(f => f.IsTestFile && f.NewTree is not null)
            .SelectMany(f => AnalyzeFile(f, f.NewTree!.GetRoot(), productionHosts));
    }

    static IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root, HashSet<string> productionHosts)
    {
        var hosts = new List<(SyntaxToken Token, string Host)>();
        foreach (var token in root.DescendantTokens())
        {
            if (!token.IsKind(SyntaxKind.StringLiteralToken) && !token.IsKind(SyntaxKind.InterpolatedStringTextToken) &&
                !token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken) && !token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken) ||
                !file.IsNew(token))
                continue;
            foreach (Match match in Url().Matches(token.ValueText))
            {
                var host = match.Groups["host"].Value.ToLowerInvariant();
                if (IsSafe(host) || productionHosts.Contains(Domain(host))) continue;
                hosts.Add((token, host));
            }
        }
        if (hosts.Count == 0) yield break;

        var distinct = hosts.Select(h => h.Host).Distinct().ToArray();
        yield return Finding(file.Path, AnalyzedFile.LineOf(hosts[0].Token.Parent!),
            $"Test code names real external host{(distinct.Length == 1 ? "" : "s")} {string.Join(", ", distinct.Take(4))}" +
            $"{(distinct.Length > 4 ? ", ..." : "")}. A test that reaches it depends on the network and someone else's server; " +
            "one that only uses it as data can still resolve it by accident. Use example.com, a .test name or localhost, " +
            "or ship the dependency with the test.");
    }

    static Finding Finding(string path, int line, string message) => new("PRB108", Severity.Info, message, path, line);

    /// <summary>api.mdblist.com and www.mdblist.com both belong to mdblist.com.</summary>
    static string Domain(string host)
    {
        var labels = host.ToLowerInvariant().Split('.');
        return labels.Length <= 2 ? host.ToLowerInvariant() : string.Join('.', labels[^2..]);
    }

    static bool IsSafe(string host) =>
        SafeHosts.Contains(host) || IdentifierHosts.Contains(host) || SafeSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal)) ||
        host.EndsWith(".example.com", StringComparison.Ordinal) || host.EndsWith(".example.org", StringComparison.Ordinal) ||
        System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip) && (System.Net.IPAddress.IsLoopback(ip) || IsPrivate(ip)) ||
        !host.Contains('.') || host.Contains('{');

    static bool IsPrivate(System.Net.IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31 ||
            b[0] == 192 && b[1] == 0 && b[2] == 2 || b[0] == 198 && b[1] == 51 && b[2] == 100 || b[0] == 203 && b[1] == 0 && b[2] == 113);
    }

    [GeneratedRegex(@"\b(?:https?|wss?|ftp)://(?:[^/\s@'""]+@)?(?<host>\[[0-9a-fA-F:]+\]|[A-Za-z0-9.-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex Url();
}
