using PrBuddy.Core;
using PrBuddy.Core.Rules;

namespace PrBuddy.Tests;

public class PrAnalyzerTests
{
    [Fact]
    public void Real_changes_come_first_and_cosmetic_files_go_to_skim()
    {
        var cosmetic = Pr.File("src/Tidy.cs", "class Tidy { int A() => 1; }", "class Tidy\n{\n    int A() => 1;\n}");
        var risky = Pr.File("src/Risky.cs",
            "public class Risky { public int A() => 1; }",
            "public class Risky { public int A() { try { return 2; } catch { } return 0; } }");
        var generated = Pr.File("src/Model.g.cs", "class M { }", "class M { int X; }");

        var report = PrAnalyzer.Analyze([cosmetic, risky, generated]);

        Assert.Equal(["src/Risky.cs"], report.ReviewFiles.Select(f => f.Path));
        Assert.Equal(["src/Model.g.cs", "src/Tidy.cs"], report.SkimFiles.Select(f => f.Path).Order());
        Assert.Contains(report.ReviewFiles.Single().Findings, f => f.RuleId == "PRB001");
    }

    [Fact]
    public void A_crashing_rule_becomes_a_finding_instead_of_an_exception()
    {
        var report = PrAnalyzer.Analyze([Pr.File("src/A.cs", null, "class A { }")], [new CrashingRule()]);

        var finding = Assert.Single(report.PullRequestFindings);
        Assert.Equal("TEST", finding.RuleId);
        Assert.Contains("boom", finding.Message);
    }

    [Fact]
    public void Rule_ids_are_unique()
    {
        var ids = DefaultRules.Create().Select(r => r.Descriptor.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    sealed class CrashingRule : RuleBase
    {
        public override RuleDescriptor Descriptor { get; } = new("TEST", "Crashes", Severity.Info, "Test");
        public override IEnumerable<Finding> Analyze(PullRequestContext context) => throw new InvalidOperationException("boom");
    }
}
