using System.Text.Json;
using System.Text.Json.Nodes;
using PrBuddy.Core.Rules;

namespace PrBuddy.Core.Reporting;

/// <summary>
/// The report as SARIF 2.1.0, the standard format for static analysis results. VS Code's SARIF
/// viewer, Visual Studio and GitHub code scanning can all show these findings next to the code.
/// </summary>
public static class SarifReport
{
    public const string InformationUri = "https://github.com/anefiox/pr-buddy";

    public static string Render(AnalysisReport report, IEnumerable<RuleDescriptor> rules)
    {
        var ruleList = rules.ToList();
        var ruleIndex = ruleList.Select((r, i) => (r.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.OrdinalIgnoreCase);

        var driverRules = new JsonArray();
        foreach (var rule in ruleList)
        {
            var doc = RuleDocs.For(rule.Id);
            var entry = new JsonObject
            {
                ["id"] = rule.Id,
                ["name"] = PascalCase(rule.Title),
                ["shortDescription"] = Text(rule.Title),
                ["defaultConfiguration"] = new JsonObject { ["level"] = Level(rule.DefaultSeverity) },
                ["properties"] = new JsonObject { ["category"] = rule.Category },
            };
            if (doc is not null)
            {
                entry["fullDescription"] = Text(doc.Why);
                entry["help"] = Text(doc.Fix);
            }
            driverRules.Add(entry);
        }

        var results = new JsonArray();
        foreach (var finding in report.AllFindings)
            results.Add(Result(finding, ruleIndex, suppression: null));
        foreach (var s in report.Suppressed)
            results.Add(Result(s.Finding, ruleIndex, s.Reason));

        var sarif = new JsonObject
        {
            ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new JsonArray
            {
                new JsonObject
                {
                    ["tool"] = new JsonObject
                    {
                        ["driver"] = new JsonObject
                        {
                            ["name"] = "PR Buddy",
                            ["informationUri"] = InformationUri,
                            ["rules"] = driverRules,
                        },
                    },
                    ["results"] = results,
                },
            },
        };
        return sarif.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    static JsonObject Result(Finding finding, Dictionary<string, int> ruleIndex, string? suppression)
    {
        var result = new JsonObject
        {
            ["ruleId"] = finding.RuleId,
            ["level"] = Level(finding.Severity),
            ["message"] = Text(finding.Message),
        };
        if (ruleIndex.TryGetValue(finding.RuleId, out var index)) result["ruleIndex"] = index;

        // PR-level findings (no file) have no location, which SARIF allows.
        if (finding.Path is { } path)
        {
            var physical = new JsonObject { ["artifactLocation"] = new JsonObject { ["uri"] = Uri(path) } };
            if (finding.Line is { } line) physical["region"] = new JsonObject { ["startLine"] = line };
            result["locations"] = new JsonArray { new JsonObject { ["physicalLocation"] = physical } };
        }

        if (suppression is not null)
            result["suppressions"] = new JsonArray { new JsonObject { ["kind"] = "inSource", ["justification"] = suppression } };
        return result;
    }

    static string Level(Severity severity) => severity switch
    {
        Severity.Critical => "error",
        Severity.Warning => "warning",
        _ => "note",
    };

    static JsonObject Text(string text) => new() { ["text"] = text };

    /// <summary>Relative, forward slashes, each segment escaped: what SARIF consumers resolve against the repo root.</summary>
    static string Uri(string path) => string.Join("/", path.Replace('\\', '/').Split('/').Select(System.Uri.EscapeDataString));

    static string PascalCase(string title) => string.Concat(
        title.Split([' ', '-', '/', '(', ')', '\''], StringSplitOptions.RemoveEmptyEntries)
             .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
