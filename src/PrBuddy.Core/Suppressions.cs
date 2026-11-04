using System.Text.RegularExpressions;

namespace PrBuddy.Core;

/// <summary>A finding someone deliberately waved through, and why.</summary>
public sealed record SuppressedFinding(Finding Finding, string Reason, int CommentLine);

/// <summary>
/// One <c>prbuddy-ignore</c> comment. Works in any file type, because it's found by text:
/// <code>
/// // prbuddy-ignore PRB001: cleanup is best-effort
/// catch (IOException) { }
///
/// x.Wait(); // prbuddy-ignore PRB004: runs on a dedicated thread
///
/// &lt;!-- prbuddy-ignore PRB202: approved by security, see #123 --&gt;
///
/// // prbuddy-ignore-file PRB301: generated-style registry, used by reflection
/// </code>
/// A comment on its own line covers the next code line; a comment after code covers that line.
/// A reason is required, so every exception carries its justification into the review.
/// </summary>
public sealed partial record Suppression(int Line, IReadOnlySet<int> CoveredLines, bool WholeFile, IReadOnlyList<string> RuleIds, string? Reason)
{
    public bool Covers(Finding finding) =>
        RuleIds.Contains(finding.RuleId, StringComparer.OrdinalIgnoreCase) &&
        (WholeFile || finding.Line is { } line && CoveredLines.Contains(line));

    public static IReadOnlyList<Suppression> Parse(string? content)
    {
        if (string.IsNullOrEmpty(content)) return [];
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var result = new List<Suppression>();

        for (var i = 0; i < lines.Length; i++)
        {
            var match = Marker().Match(lines[i]);
            if (!match.Success) continue;

            var lineNumber = i + 1;
            var ids = match.Groups["ids"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var reason = match.Groups["reason"].Success ? match.Groups["reason"].Value.Trim() : null;
            var wholeFile = match.Groups["file"].Success;

            var covered = new HashSet<int> { lineNumber };
            var codeBefore = lines[i][..match.Index].Trim();
            if (!wholeFile && IsCommentOnly(codeBefore))
            {
                // Comment on its own line: covers the next line that has code on it.
                for (var j = i + 1; j < lines.Length; j++)
                {
                    var next = lines[j].Trim();
                    if (next.Length == 0 || IsCommentLine(next)) continue;
                    covered.Add(j + 1);
                    break;
                }
            }

            result.Add(new Suppression(lineNumber, covered, wholeFile, ids, string.IsNullOrWhiteSpace(reason) ? null : reason));
        }
        return result;
    }

    /// <summary>What's left of the line before the marker is just a comment opener (//, #, &lt;!--, /*).</summary>
    static bool IsCommentOnly(string before) => before is "" or "//" or "#" or "<!--" or "/*" or "*" or "///" or "--" or ";";

    static bool IsCommentLine(string line) =>
        line.StartsWith("//") || line.StartsWith('#') || line.StartsWith("<!--") || line.StartsWith("/*") || line.StartsWith('*');

    [GeneratedRegex(@"prbuddy-ignore(?<file>-file)?\s+(?<ids>[A-Za-z]+\d+(?:\s*,\s*[A-Za-z]+\d+)*)\s*(?::(?<reason>.*?))?\s*(?:-->|\*/)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Marker();
}
