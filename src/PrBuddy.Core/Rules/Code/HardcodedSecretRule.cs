using System.Text.RegularExpressions;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB010: a credential in an added line of any text file (C#, appsettings.json, YAML, scripts...).
///
/// Only formats with a distinctive shape are matched (an AWS key always starts with AKIA, a GitHub
/// token with ghp_), plus connection strings and private keys, so plain long strings don't trip it.
/// The secret itself never appears in the report: only its kind and first few characters.
/// Placeholders ("changeme", "${DB_PASSWORD}", "&lt;your-key&gt;") and published example keys are skipped.
/// </summary>
public sealed partial class HardcodedSecretRule : RuleBase
{
    public override RuleDescriptor Descriptor { get; } = new("PRB010", "Hard-coded secret", Severity.Critical, "Security");

    static readonly (string Kind, Regex Pattern)[] Patterns =
    [
        ("AWS access key", new(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.Compiled)),
        ("GitHub token", new(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{60,})\b", RegexOptions.Compiled)),
        ("Slack token", new(@"\bxox[baprs]-[A-Za-z0-9-]{10,}\b", RegexOptions.Compiled)),
        ("Stripe live key", new(@"\b(?:sk|rk)_live_[A-Za-z0-9]{20,}\b", RegexOptions.Compiled)),
        ("Google API key", new(@"\bAIza[0-9A-Za-z_\-]{35}\b", RegexOptions.Compiled)),
        ("OpenAI or Anthropic API key", new(@"\bsk-(?:ant-|proj-)[A-Za-z0-9_\-]{20,}", RegexOptions.Compiled)),
        ("private key", new(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED |PGP )?PRIVATE KEY(?: BLOCK)?-----", RegexOptions.Compiled)),
        ("Azure storage account key", new(@"AccountKey=[A-Za-z0-9+/]{40,}={0,2}", RegexOptions.Compiled)),
        ("JSON Web Token", new(@"\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}", RegexOptions.Compiled)),
        ("password in a connection string", new(@"(?i)\b(?:password|pwd)\s*=\s*(?<value>[^;'""\s]{4,})", RegexOptions.Compiled)),
    ];

    public override IEnumerable<Finding> Analyze(PullRequestContext context)
    {
        foreach (var file in context.Files)
        {
            if (file.File.IsBinary || file.File.NewContent is null || file.IsGenerated) continue;
            foreach (var (line, text) in file.AddedLineTexts())
            {
                foreach (var (kind, pattern) in Patterns)
                {
                    var match = pattern.Match(text);
                    if (!match.Success) continue;
                    var secret = match.Groups["value"].Success ? match.Groups["value"].Value : match.Value;
                    if (IsPlaceholder(secret)) continue;

                    // Test fixtures hold fake credentials on purpose, but a real one there leaks just the same.
                    var severity = file.IsTestFile ? Severity.Warning : Severity.Critical;
                    yield return Report(file.Path, line,
                        $"Looks like a hard-coded {kind} ({Redact(secret)}). Move it to configuration or a secret store, and rotate it if it was ever real: it's in git history now.",
                        severity);
                    break; // one finding per line is enough
                }
            }
        }
    }

    /// <summary>Show just enough to find it: "AKIA…" never the whole thing.</summary>
    static string Redact(string secret) => secret.Length <= 8 ? "…" : secret[..4] + "…";

    public static bool IsPlaceholder(string value)
    {
        var v = value.Trim().Trim('"', '\'');
        return v.Contains("EXAMPLE", StringComparison.OrdinalIgnoreCase)      // AWS docs: AKIAIOSFODNN7EXAMPLE
            || Variable().IsMatch(v)                                            // ${X}, $(X), %X%, {X}, {{X}}
            || Placeholder().IsMatch(v)
            || v.Distinct().Count() <= 2;                                       // xxxxxxxx, ********
    }

    [GeneratedRegex(@"^(?:\$\{[^}]+\}|\$\([^)]+\)|%[^%]+%|\{\{?[^}]+\}?\}|#\{[^}]+\}|<[^>]+>|\$[A-Za-z_][A-Za-z0-9_]*)$")]
    private static partial Regex Variable();

    [GeneratedRegex(@"(?i)^(?:changeme|change[-_]?me|password|passw0rd|secret|your[-_]?\w*|placeholder|redacted|dummy|fake|test|example|sample|todo|none|null|empty)$|your[-_]|<|>")]
    private static partial Regex Placeholder();
}
