using PrBuddy.Core;
using PrBuddy.Core.Configuration;

namespace PrBuddy.Tests;

public class SuppressionTests
{
    static AnalysisReport Analyze(string code, string path = "src/Cleanup.cs", string? config = null) =>
        PrAnalyzer.Analyze([Pr.File(path, null, code)], config: config is null ? null : PrBuddyConfig.Parse(config, "test"));

    [Fact]
    public void Comment_on_its_own_line_covers_the_next_code_line()
    {
        var report = Analyze("""
            class Cleanup
            {
                void M()
                {
                    // prbuddy-ignore PRB001: temp files; the OS cleans them anyway

                    try { System.IO.File.Delete("x"); } catch { }
                }
            }
            """);

        Assert.DoesNotContain(report.AllFindings, f => f.RuleId == "PRB001");
        var s = Assert.Single(report.Suppressed);
        Assert.Equal("PRB001", s.Finding.RuleId);
        Assert.Equal(7, s.Finding.Line);
        Assert.Equal("temp files; the OS cleans them anyway", s.Reason);
        Assert.Equal(5, s.CommentLine);
    }

    [Fact]
    public void Trailing_comment_covers_its_own_line_only()
    {
        var report = Analyze("""
            class S
            {
                void A(System.Threading.Tasks.Task t) { t.Wait(); } // prbuddy-ignore PRB004: dedicated thread
                void B(System.Threading.Tasks.Task t) { t.Wait(); }
            }
            """);

        Assert.Single(report.Suppressed);
        Assert.Equal(4, report.AllFindings.Single(f => f.RuleId == "PRB004").Line);
    }

    [Fact]
    public void Several_ids_case_insensitive_and_other_comment_styles()
    {
        var parsed = Suppression.Parse("""
            /* prbuddy-ignore prb001, PRB002: both on purpose */
            catch { }
            <!-- prbuddy-ignore PRB202: approved by security -->
            # prbuddy-ignore PRB203: matches the shared ruleset
            """);

        Assert.Equal(3, parsed.Count);
        Assert.Equal(["prb001", "PRB002"], parsed[0].RuleIds);
        Assert.Equal("both on purpose", parsed[0].Reason);
        Assert.Contains(2, parsed[0].CoveredLines);
        Assert.Equal("approved by security", parsed[1].Reason);
        Assert.Equal("matches the shared ruleset", parsed[2].Reason);
    }

    [Fact]
    public void Whole_file_suppression_covers_every_line()
    {
        var report = Analyze("""
            // prbuddy-ignore-file PRB001: this file is a sandbox for parser experiments
            class A { void M() { try { } catch { } } }
            class B { void M() { try { } catch { } } }
            """);

        Assert.Equal(2, report.Suppressed.Count);
        Assert.DoesNotContain(report.AllFindings, f => f.RuleId == "PRB001");
    }

    [Fact]
    public void A_suppression_without_a_reason_does_not_apply()
    {
        var report = Analyze("""
            class A
            {
                // prbuddy-ignore PRB001
                void M() { try { } catch { } }
            }
            """);

        Assert.Empty(report.Suppressed);
        Assert.Contains(report.AllFindings, f => f.RuleId == "PRB001");
        var problem = report.AllFindings.Single(f => f.RuleId == "PRB900");
        Assert.Equal(3, problem.Line);
        Assert.Contains("has no reason", problem.Message);
    }

    [Fact]
    public void A_suppression_naming_an_unknown_rule_does_not_apply()
    {
        var report = Analyze("""
            class A
            {
                // prbuddy-ignore PRB01: typo in the id
                void M() { try { } catch { } }
            }
            """);

        Assert.Contains(report.AllFindings, f => f.RuleId == "PRB001");
        Assert.Contains(report.AllFindings, f => f.RuleId == "PRB900" && f.Message.Contains("unknown rule(s) PRB01"));
    }

    [Fact]
    public void A_new_suppression_that_matches_nothing_is_reported()
    {
        var report = Analyze("""
            class A
            {
                // prbuddy-ignore PRB004: nothing blocking here, oops
                void M() { try { } catch { } }
            }
            """);

        var problem = report.AllFindings.Single(f => f.RuleId == "PRB900");
        Assert.Equal(Severity.Info, problem.Severity);
        Assert.Contains("doesn't match any finding", problem.Message);
    }

    [Fact]
    public void Old_unused_suppressions_are_not_the_prs_problem()
    {
        const string before = """
            class A
            {
                // prbuddy-ignore PRB004: from the old days
                void M() { }
            }
            """;
        var after = before.Replace("void M() { }", "void M() { var x = 1; }");

        var report = PrAnalyzer.Analyze([Pr.File("src/A.cs", before, after)]);
        Assert.DoesNotContain(report.AllFindings, f => f.RuleId == "PRB900");
    }

    [Fact]
    public void Suppressed_findings_do_not_count_toward_fail_on_or_risk()
    {
        const string code = """
            public class A
            {
                // prbuddy-ignore PRB001: fine here
                public void M() { try { } catch { } }
            }
            """;
        var suppressed = Analyze(code);
        var unsuppressed = Analyze(code.Replace("prbuddy-ignore PRB001: fine here", "nothing to see"));

        Assert.DoesNotContain(suppressed.AllFindings, f => f.RuleId == "PRB001");
        Assert.True(suppressed.Files.Single().RiskScore < unsuppressed.Files.Single().RiskScore);
    }

    [Fact]
    public void The_suppression_check_can_be_configured_like_any_rule()
    {
        const string code = """
            class A
            {
                // prbuddy-ignore PRB001
                void M() { try { } catch { } }
            }
            """;

        Assert.DoesNotContain(Analyze(code, config: """{ "rules": { "PRB900": "off" } }""").AllFindings, f => f.RuleId == "PRB900");
        Assert.Equal(Severity.Critical, Analyze(code, config: """{ "rules": { "PRB900": "critical" } }""").AllFindings.Single(f => f.RuleId == "PRB900").Severity);
    }

    [Fact]
    public void Works_in_project_files_too()
    {
        const string before = "<Project>\n  <ItemGroup>\n  </ItemGroup>\n</Project>\n";
        var after = before.Replace("  </ItemGroup>",
            "    <!-- prbuddy-ignore PRB202: our own package, published to the company feed -->\n    <PackageReference Include=\"Contoso.Internal\" Version=\"1.0.0\" />\n  </ItemGroup>");

        var report = PrAnalyzer.Analyze([Pr.File("src/A/A.csproj", before, after)]);
        Assert.Contains(report.Suppressed, s => s.Finding.RuleId == "PRB202");
        Assert.DoesNotContain(report.AllFindings, f => f.RuleId == "PRB202");
    }
}
