using PrBuddy.Core;
using PrBuddy.Core.Configuration;

namespace PrBuddy.Tests;

public class ConfigTests
{
    const string EmptyCatch = "class A { void M() { try { } catch { } } }";

    [Fact]
    public void Parses_every_setting()
    {
        var config = PrBuddyConfig.Parse("""
            {
              // comments and trailing commas are fine
              "$schema": "ignored",
              "ignore": ["**/Migrations/**"],
              "failOn": "warning",
              "rules": {
                "PRB008": "off",
                "PRB004": "info",
                "PRB006": { "threshold": 5, "severity": "warning" },
                "PRB001": { "enabled": false },
              },
            }
            """, "test");

        Assert.Equal(Severity.Warning, config.FailOn);
        Assert.False(config.For("PRB008").Enabled);
        Assert.False(config.For("prb001").Enabled); // ids are case-insensitive
        Assert.Equal(Severity.Info, config.For("PRB004").Severity);
        Assert.Equal(5, config.GetInt("PRB006", "threshold", 3));
        Assert.Equal(3, config.GetInt("PRB002", "threshold", 3));
        Assert.True(config.IsIgnored("src/Data/Migrations/0001_Init.cs"));
    }

    [Theory]
    [InlineData("[]", "must contain a JSON object")]
    [InlineData("{ not json", "isn't valid JSON")]
    [InlineData("""{ "rulez": {} }""", "unknown setting \"rulez\"")]
    [InlineData("""{ "failOn": "loud" }""", "info, warning or critical")]
    [InlineData("""{ "ignore": "docs/**" }""", "array of glob patterns")]
    [InlineData("""{ "rules": { "PRB001": 3 } }""", "\"off\", a severity, or an object")]
    public void Bad_config_gives_a_clear_error(string json, string expected)
    {
        var ex = Assert.Throws<PrBuddyException>(() => PrBuddyConfig.Parse(json, "prbuddy.json"));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Unknown_rule_id_is_reported()
    {
        var config = PrBuddyConfig.Parse("""{ "rules": { "PRB01": "off" } }""", "test");
        var ex = Assert.Throws<PrBuddyException>(() => PrAnalyzer.Analyze([], config: config));
        Assert.Contains("PRB01", ex.Message);
    }

    [Fact]
    public void Disabled_rules_do_not_run()
    {
        var config = PrBuddyConfig.Parse("""{ "rules": { "PRB001": "off" } }""", "test");
        var report = PrAnalyzer.Analyze([Pr.File("src/A.cs", null, EmptyCatch)], config: config);
        Assert.DoesNotContain(report.AllFindings, f => f.RuleId == "PRB001");
    }

    [Fact]
    public void Severity_override_applies_to_every_finding_of_the_rule()
    {
        var config = PrBuddyConfig.Parse("""{ "rules": { "PRB001": "critical" } }""", "test");
        var report = PrAnalyzer.Analyze([Pr.File("src/A.cs", null, EmptyCatch)], config: config);
        Assert.Equal(Severity.Critical, report.AllFindings.Single(f => f.RuleId == "PRB001").Severity);
    }

    [Fact]
    public void Ignored_files_are_left_out_and_listed()
    {
        var config = PrBuddyConfig.Parse("""{ "ignore": ["**/Migrations/**"] }""", "test");
        var report = PrAnalyzer.Analyze(
        [
            Pr.File("src/Data/Migrations/0001_Init.cs", null, EmptyCatch),
            Pr.File("src/A.cs", null, "class A { }"),
        ], config: config);

        Assert.Equal(["src/A.cs"], report.Files.Select(f => f.Path));
        Assert.Equal(["src/Data/Migrations/0001_Init.cs"], report.IgnoredPaths);
        Assert.DoesNotContain(report.AllFindings, f => f.RuleId == "PRB001");
    }

    [Fact]
    public void Rule_options_reach_the_rule()
    {
        const string code = "class S { string M(string? a, string? b, string? c) => a! + b! + c!; }";
        var strict = PrAnalyzer.Analyze([Pr.File("src/S.cs", null, code)]);
        var relaxed = PrAnalyzer.Analyze([Pr.File("src/S.cs", null, code)],
            config: PrBuddyConfig.Parse("""{ "rules": { "PRB006": { "threshold": 10 } } }""", "test"));

        Assert.Contains(strict.AllFindings, f => f.RuleId == "PRB006");
        Assert.DoesNotContain(relaxed.AllFindings, f => f.RuleId == "PRB006");
    }

    [Fact]
    public void Share_options_are_validated()
    {
        var good = PrBuddyConfig.Parse("""{ "rules": { "PRB302": { "similarity": 0.9 } } }""", "test");
        Assert.Equal(0.9, good.GetShare("PRB302", "similarity", 0.8));
        Assert.Equal(0.8, good.GetShare("PRB303", "focusShare", 0.8));

        foreach (var bad in new[] { "1.5", "0", "\"high\"" })
        {
            var config = PrBuddyConfig.Parse($$"""{ "rules": { "PRB302": { "similarity": {{bad}} } } }""", "custom.json");
            var ex = Assert.Throws<PrBuddyException>(() => config.GetShare("PRB302", "similarity", 0.8));
            Assert.Contains("custom.json: PRB302.similarity must be a number above 0 and at most 1", ex.Message);
        }
    }

    [Fact]
    public void Duplicate_thresholds_reach_the_rule()
    {
        const string original = """
            public static class Text
            {
                public static string Slugify(string input)
                {
                    var builder = new System.Text.StringBuilder();
                    foreach (var c in input.Trim().ToLowerInvariant())
                    {
                        if (char.IsLetterOrDigit(c)) builder.Append(c);
                        else if (char.IsWhiteSpace(c) || c == '-') builder.Append('-');
                    }
                    return builder.ToString().Trim('-');
                }
            }
            """;
        var copy = original.Replace("class Text", "class Urls").Replace("Slugify", "ToPart").Replace("ToLowerInvariant", "ToUpperInvariant");
        var files = new[] { Pr.File("src/Text.cs", original, original), Pr.File("src/Urls.cs", null, copy) };

        var defaults = PrAnalyzer.Analyze(files);
        var strict = PrAnalyzer.Analyze(files, config: PrBuddyConfig.Parse("""{ "rules": { "PRB302": { "similarity": 0.99 } } }""", "test"));
        var loud = PrAnalyzer.Analyze(files, config: PrBuddyConfig.Parse("""{ "rules": { "PRB302": { "copySimilarity": 0.8 } } }""", "test"));

        Assert.Equal(Severity.Info, defaults.AllFindings.Single(f => f.RuleId == "PRB302").Severity);
        Assert.DoesNotContain(strict.AllFindings, f => f.RuleId == "PRB302");
        Assert.Equal(Severity.Warning, loud.AllFindings.Single(f => f.RuleId == "PRB302").Severity);
    }

    [Theory]
    [InlineData("**/Migrations/**", "src/Data/Migrations/0001.cs", true)]
    [InlineData("**/Migrations/**", "Migrations/0001.cs", true)]
    [InlineData("**/Migrations/**", "src/MigrationsHelper.cs", false)]
    [InlineData("docs/**", "docs/a/b.md", true)]
    [InlineData("docs/", "docs/a.md", true)]
    [InlineData("docs/**", "src/docs/a.md", false)]
    [InlineData("*.Designer.cs", "src/Forms/Main.Designer.cs", true)]
    [InlineData("src/*.cs", "src/A.cs", true)]
    [InlineData("src/*.cs", "src/sub/A.cs", false)]
    [InlineData("src/?.cs", "src/A.cs", true)]
    public void Glob_matches_like_gitignore(string pattern, string path, bool expected) =>
        Assert.Equal(expected, new Glob(pattern).IsMatch(path));
}
