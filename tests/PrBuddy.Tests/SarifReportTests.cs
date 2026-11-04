using System.Text.Json;
using PrBuddy.Core;
using PrBuddy.Core.Reporting;
using PrBuddy.Core.Rules;

namespace PrBuddy.Tests;

public class SarifReportTests
{
    static JsonElement Render(params ChangedFile[] files)
    {
        var rules = DefaultRules.Create();
        var report = PrAnalyzer.Analyze(files, rules);
        var json = SarifReport.Render(report, rules.Select(r => r.Descriptor).Append(SuppressionCheck.Descriptor));
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    const string Code = """
        public class Cleanup
        {
            public void A() { try { } catch { } }
            // prbuddy-ignore PRB001: best effort, see the README
            public void B() { try { } catch { } }
        }
        """;

    [Fact]
    public void Is_a_sarif_2_1_document_with_every_rule_described()
    {
        var root = Render(Pr.File("src/My Dir/Cleanup.cs", null, Code));

        Assert.Equal("2.1.0", root.GetProperty("version").GetString());
        var driver = root.GetProperty("runs")[0].GetProperty("tool").GetProperty("driver");
        Assert.Equal("PR Buddy", driver.GetProperty("name").GetString());

        var rules = driver.GetProperty("rules").EnumerateArray().ToList();
        Assert.Contains(rules, r => r.GetProperty("id").GetString() == "PRB900");
        var prb001 = rules.Single(r => r.GetProperty("id").GetString() == "PRB001");
        Assert.Equal("EmptyCatchBlock", prb001.GetProperty("name").GetString());
        Assert.Equal("warning", prb001.GetProperty("defaultConfiguration").GetProperty("level").GetString());
        Assert.Contains("failures invisible", prb001.GetProperty("fullDescription").GetProperty("text").GetString());
    }

    [Fact]
    public void Results_have_levels_locations_and_suppressions()
    {
        var root = Render(Pr.File("src/My Dir/Cleanup.cs", null, Code));
        var results = root.GetProperty("runs")[0].GetProperty("results").EnumerateArray().ToList();
        var rules = root.GetProperty("runs")[0].GetProperty("tool").GetProperty("driver").GetProperty("rules");

        var active = results.Single(r => r.GetProperty("ruleId").GetString() == "PRB001" && !r.TryGetProperty("suppressions", out _));
        Assert.Equal("warning", active.GetProperty("level").GetString());
        var location = active.GetProperty("locations")[0].GetProperty("physicalLocation");
        Assert.Equal("src/My%20Dir/Cleanup.cs", location.GetProperty("artifactLocation").GetProperty("uri").GetString());
        Assert.Equal(3, location.GetProperty("region").GetProperty("startLine").GetInt32());
        Assert.Equal("PRB001", rules[active.GetProperty("ruleIndex").GetInt32()].GetProperty("id").GetString());

        var suppressed = results.Single(r => r.TryGetProperty("suppressions", out _));
        var suppression = suppressed.GetProperty("suppressions")[0];
        Assert.Equal("inSource", suppression.GetProperty("kind").GetString());
        Assert.Equal("best effort, see the README", suppression.GetProperty("justification").GetString());
    }

    [Fact]
    public void Pr_level_findings_have_no_location_and_info_is_a_note()
    {
        var root = Render(Pr.File("src/Calc.cs", "class C { int A() => 1; }", "class C { int A() => 2; }"));
        var results = root.GetProperty("runs")[0].GetProperty("results").EnumerateArray().ToList();

        var prb101 = results.Single(r => r.GetProperty("ruleId").GetString() == "PRB101");
        Assert.False(prb101.TryGetProperty("locations", out _));

        var info = Render(Pr.File("src/A.cs", null, "internal class Unused { }"))
            .GetProperty("runs")[0].GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("ruleId").GetString() == "PRB301");
        Assert.Equal("note", info.GetProperty("level").GetString());
    }
}
