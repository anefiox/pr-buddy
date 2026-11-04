using PrBuddy.Core;
using PrBuddy.Core.Semantics;

namespace PrBuddy.Tests;

public class CrossFileMoveTests
{
    // A big, complex, private-and-unused-looking method: if the move weren't recognized,
    // PRB013 (complex new method), PRB301 (unused) and PRB101 (no tests) would all fire on it.
    static readonly string Parser = "    private static int ParseLevel(string s)\n    {\n"
        + string.Concat(Enumerable.Range(0, 30).Select(i => $"        if (s == \"level{i}\") return {i};\n"))
        + "        return -1;\n    }\n";

    const string Run = "    public int Run(string s) => ParseLevel(s) * 2;\n";

    static AnalysisReport SplitFile()
    {
        var before = "public static class Tool\n{\n" + Run + Parser + "}\n";
        var afterOld = "public static partial class Tool\n{\n" + Run + "}\n";
        // Same method, reformatted and in another file of the same partial class.
        var afterNew = "public static partial class Tool\n{\n" + Parser.Replace("    {\n", "    {\n        // moved here to keep Tool.cs short\n") + "}\n";

        return PrAnalyzer.Analyze(
        [
            Pr.File("src/Tool.cs", before, afterOld),
            Pr.File("src/Tool.Parsing.cs", null, afterNew),
        ]);
    }

    [Fact]
    public void A_method_moved_to_another_file_is_a_move_on_both_sides()
    {
        var report = SplitFile();
        var newSide = report.Files.Single(f => f.Path == "src/Tool.Parsing.cs").SemanticDiff!.Changes
            .Single(c => c.DisplayName.Contains("ParseLevel"));
        var oldSide = report.Files.Single(f => f.Path == "src/Tool.cs").SemanticDiff!.Changes
            .Single(c => c.DisplayName.Contains("ParseLevel"));

        Assert.Equal(MemberChangeKind.Moved, newSide.Change);
        Assert.Equal("src/Tool.cs", newSide.MovedPath);
        Assert.NotNull(newSide.Line);
        Assert.Equal(MemberChangeKind.Moved, oldSide.Change);
        Assert.Equal("src/Tool.Parsing.cs", oldSide.MovedPath);
        Assert.Null(oldSide.Line);
        Assert.Equal("from src/Tool.cs", PrBuddy.Core.Reporting.ReportText.MoveText(newSide));
        Assert.Equal("to src/Tool.Parsing.cs", PrBuddy.Core.Reporting.ReportText.MoveText(oldSide));
    }

    [Fact]
    public void Moved_code_is_not_treated_as_new_code()
    {
        var report = SplitFile();
        var found = string.Join("\n", report.AllFindings.Select(f => $"{f.RuleId} {f.Path}:{f.Line} {f.Message}"));
        foreach (var rule in new[] { "PRB013", "PRB301", "PRB101" })
            Assert.False(report.AllFindings.Any(f => f.RuleId == rule), found);
    }

    [Fact]
    public void Changed_while_moving_is_not_a_move()
    {
        var before = "public static class Tool\n{\n" + Run + Parser + "}\n";
        var afterOld = "public static partial class Tool\n{\n" + Run + "}\n";
        var edited = "public static partial class Tool\n{\n" + Parser.Replace("return -1;", "return 0;") + "}\n";

        var report = PrAnalyzer.Analyze([Pr.File("src/Tool.cs", before, afterOld), Pr.File("src/Tool.Parsing.cs", null, edited)]);
        var newSide = report.Files.Single(f => f.Path == "src/Tool.Parsing.cs").SemanticDiff!.Changes.Single(c => c.DisplayName.Contains("ParseLevel"));
        Assert.Equal(MemberChangeKind.Added, newSide.Change);
        Assert.Contains(report.AllFindings, f => f.RuleId == "PRB013"); // so it's still checked as new code
    }

    [Fact]
    public void A_whole_class_moved_to_its_own_file_is_all_moves()
    {
        const string helper = """
            public class Formatter
            {
                public string Format(decimal amount) => amount.ToString("C", System.Globalization.CultureInfo.InvariantCulture);
                public string Percent(decimal share) => (share * 100m).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";
            }
            """;
        const string main = "public class Program { public static void Main() { } }\n";

        var report = PrAnalyzer.Analyze(
        [
            Pr.File("src/Program.cs", main + helper, main),
            Pr.File("src/Formatter.cs", null, helper),
        ]);

        var moved = report.Files.Single(f => f.Path == "src/Formatter.cs").SemanticDiff!.Changes;
        Assert.All(moved, c => Assert.Equal(MemberChangeKind.Moved, c.Change));
        Assert.Equal(3, moved.Count); // the class header and both methods
    }

    [Fact]
    public void Making_a_type_partial_is_not_a_behavior_change_but_other_header_changes_are()
    {
        static MemberChangeKind HeaderChange(string before, string after) =>
            Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(before) is var o && Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(after) is var n
                ? SemanticDiffer.Compare("A.cs", o, n).Changes.Single(c => c.MemberKind == "class").Change
                : default;

        Assert.Equal(MemberChangeKind.CosmeticOnly, HeaderChange("public class A { }", "public partial class A { }"));
        Assert.Equal(MemberChangeKind.SignatureChanged, HeaderChange("public class A { }", "public sealed class A { }"));
        Assert.Equal(MemberChangeKind.SignatureChanged, HeaderChange("public class A { }", "public partial class A : System.IDisposable { public void Dispose() { } }"));
    }

    [Fact]
    public void Tiny_identical_members_are_not_called_moves()
    {
        var report = PrAnalyzer.Analyze(
        [
            Pr.File("src/A.cs", "public class A { public int Id { get; set; } }", "public class A { }"),
            Pr.File("src/B.cs", "public class B { }", "public class B { public int Id { get; set; } }"),
        ]);

        Assert.Equal(MemberChangeKind.Added, report.Files.Single(f => f.Path == "src/B.cs").SemanticDiff!.Changes.Single().Change);
    }

    [Fact]
    public void The_hidden_tokens_never_reach_the_json_output()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(SplitFile());
        Assert.DoesNotContain("\"Tokens\"", json);
        Assert.Contains("\"MovedPath\":\"src/Tool.cs\"", json);
    }
}
