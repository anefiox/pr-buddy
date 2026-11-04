using PrBuddy.Core;
using PrBuddy.Core.Git;
using PrBuddy.Core.Reporting;

namespace PrBuddy.Tests;

public class MarkdownReportTests
{
    static readonly DiffRange Range = new("main", "HEAD", new string('a', 40), new string('b', 40));
    const string LinkBase = "https://github.com/o/r/blob/abc";

    static string Render(params ChangedFile[] files) =>
        MarkdownReport.Render(Range, PrAnalyzer.Analyze(files), LinkBase, "prbuddy.json @ aaaaaaa (base)");

    [Fact]
    public void Starts_with_the_marker_the_action_looks_for()
    {
        var md = Render(Pr.File("src/A.cs", null, "class A { }"));
        Assert.StartsWith(MarkdownReport.Marker, md);
    }

    [Fact]
    public void Links_files_and_lines()
    {
        var md = Render(Pr.File("src/My Dir/A.cs", null, "class A\n{\n    void M() { try { } catch { } }\n}"));

        Assert.Contains("[`src/My Dir/A.cs`](https://github.com/o/r/blob/abc/src/My%20Dir/A.cs)", md);
        Assert.Contains("[L3](https://github.com/o/r/blob/abc/src/My%20Dir/A.cs#L3)", md);
        Assert.Contains("**PRB001**", md);
    }

    [Fact]
    public void Escapes_html_in_messages()
    {
        const string before = "<Project>\n  <PropertyGroup>\n  </PropertyGroup>\n</Project>\n";
        var after = before.Replace("  </PropertyGroup>", "    <NoWarn>CS1591</NoWarn>\n  </PropertyGroup>");

        var md = Render(Pr.File("src/A/A.csproj", before, after));

        Assert.Contains("&lt;NoWarn&gt;", md);
        Assert.DoesNotContain("<NoWarn>", md);
    }

    [Fact]
    public void Cosmetic_files_go_in_the_collapsed_skim_list()
    {
        var md = Render(
            Pr.File("src/Tidy.cs", "class Tidy { int A() => 1; }", "class Tidy\n{\n    int A() => 1;\n}"),
            Pr.File("src/Real.cs", "class Real { int A() => 1; }", "class Real { int A() => 2; }"));

        Assert.Contains("<details><summary>Skim only (1)</summary>", md);
        Assert.Contains("| 1 |", md);
        Assert.Contains("1 behavior", md);
    }

    [Fact]
    public void Stays_under_the_comment_limit()
    {
        // 400 files, each with a real change and a finding.
        var files = Enumerable.Range(0, 400)
            .Select(i => Pr.File($"src/Feature{i}/VeryLongFileNameToEatCharacters{i}.cs",
                $"class C{i} {{ void M() {{ }} }}",
                $"class C{i} {{ void M() {{ try {{ Work(); }} catch {{ }} }} void Work() {{ }} }}"))
            .ToArray();

        var md = Render(files);

        Assert.True(md.Length <= MarkdownReport.MaxLength + 2_000, $"length was {md.Length}");
        Assert.Contains("left out to fit GitHub's comment limit", md);
    }
}
