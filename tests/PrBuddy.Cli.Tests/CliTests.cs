using System.Text.Json;

namespace PrBuddy.Cli.Tests;

/// <summary>Runs the real command against real git repositories.</summary>
public sealed class CliTests : IDisposable
{
    const string CleanClass = """
        namespace Shop;

        public class Cart
        {
            public int Count(int[] items)
            {
                return items.Length;
            }
        }
        """;

    const string SloppyClass = """
        namespace Shop;

        public class Cart
        {
            public int Count(int[] items)
            {
                try
                {
                    return items.Length;
                }
                catch
                {
                }
                return 0;
            }
        }
        """;

    readonly TempRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    /// <summary>main has the clean class; "feature" has <paramref name="featureContent"/>.</summary>
    TempRepo Pr(string featureContent, string path = "src/Shop/Cart.cs")
    {
        _repo.Write(path, CleanClass).Commit("initial");
        _repo.Branch("feature").Write(path, featureContent).Commit("feature work");
        return _repo;
    }

    CliResult Run(params string[] args) => Cli.Run([.. args, "--repo", _repo.Dir]);

    // ---------- options and exit codes ----------

    [Fact]
    public void Help_prints_usage()
    {
        var result = Cli.Run("--help");
        Assert.Equal(0, result.Exit);
        Assert.Contains("Usage: prbuddy", result.Out);
    }

    [Theory]
    [InlineData(new[] { "--bogus" }, "Unknown option '--bogus'")]
    [InlineData(new[] { "--json", "--markdown" }, "Pick one of --json, --markdown and --sarif")]
    [InlineData(new[] { "--fail-on", "loud" }, "--fail-on expects info, warning or critical")]
    [InlineData(new[] { "--fail-on", "7" }, "--fail-on expects info, warning or critical")]
    [InlineData(new[] { "--repo" }, "--repo needs a value")]
    [InlineData(new[] { "a", "b", "c" }, "at most two refs")]
    public void Bad_arguments_exit_2_with_a_message_on_stderr(string[] args, string expected)
    {
        var result = Cli.Run(args);
        Assert.Equal(2, result.Exit);
        Assert.Contains(expected, result.Err);
        Assert.Empty(result.Out);
    }

    [Fact]
    public void Not_a_repository_is_a_friendly_error()
    {
        var empty = Directory.CreateTempSubdirectory("prbuddy-norepo-").FullName;
        try
        {
            var result = Cli.Run("--repo", empty);
            Assert.Equal(2, result.Exit);
            Assert.Contains("No git repository", result.Err);
        }
        finally
        {
            Directory.Delete(empty);
        }
    }

    [Fact]
    public void Unknown_ref_is_a_friendly_error()
    {
        Pr(CleanClass);
        var result = Run("does-not-exist", "feature");
        Assert.Equal(2, result.Exit);
        Assert.Contains("Couldn't resolve 'does-not-exist'", result.Err);
    }

    [Fact]
    public void Version_prints_the_package_version()
    {
        var result = Cli.Run("--version");
        Assert.Equal(0, result.Exit);
        Assert.Equal("prbuddy 0.1.0", result.Out.Trim());
    }

    [Fact]
    public void List_rules_shows_every_rule()
    {
        var result = Cli.Run("--list-rules");
        Assert.Equal(0, result.Exit);
        foreach (var id in new[] { "PRB001", "PRB008", "PRB101", "PRB104", "PRB201", "PRB203" })
            Assert.Contains(id, result.Console);
    }

    [Fact]
    public void Explain_shows_why_what_to_do_and_options()
    {
        var result = Cli.Run("--explain", "prb302");
        Assert.Equal(0, result.Exit);
        Assert.Contains("PRB302 Duplicated code", result.Console);
        Assert.Contains("Why it matters", result.Console);
        Assert.Contains("What to do", result.Console);
        Assert.Contains("copySimilarity", result.Console);
        Assert.Contains("// prbuddy-ignore PRB302: why this is fine here", result.Console);
    }

    [Fact]
    public void Explain_an_unknown_rule_is_an_error()
    {
        var result = Cli.Run("--explain", "PRB999");
        Assert.Equal(2, result.Exit);
        Assert.Contains("There's no rule 'PRB999'", result.Err);
    }

    [Fact]
    public void Fail_on_controls_the_exit_code()
    {
        Pr(SloppyClass);
        Assert.Equal(0, Run("main", "feature").Exit);
        Assert.Equal(1, Run("main", "feature", "--fail-on", "warning").Exit);
        Assert.Equal(1, Run("main", "feature", "--fail-on", "WARNING").Exit);
        Assert.Equal(0, Run("main", "feature", "--fail-on", "critical").Exit);
    }

    [Fact]
    public void Clean_pr_passes_even_at_info()
    {
        Pr(CleanClass.Replace("return items.Length;", "return items.Length + 0;"));
        // A behavior change with no test changes is PRB101 (a warning), so add a test too.
        _repo.Write("tests/Shop.Tests/CartTests.cs", "public class CartTests { [Fact] public void C() => Assert.Equal(0, new Shop.Cart().Count([])); }").Commit();

        var result = Run("main", "feature", "--fail-on", "info");
        Assert.True(result.Exit == 0, result.Console);
    }

    // ---------- init ----------

    [Fact]
    public void Init_writes_a_starter_config_at_the_repo_root_and_never_overwrites()
    {
        _repo.Write("src/A.cs", "class A { }").Commit();
        var path = Path.Combine(_repo.Dir, "prbuddy.json");

        var result = Cli.Run("init", "--repo", Path.Combine(_repo.Dir, "src")); // from a subfolder
        Assert.Equal(0, result.Exit);
        Assert.True(File.Exists(path));
        Assert.Contains("changes nothing until you edit it", result.Out);

        File.WriteAllText(path, "{ \"failOn\": \"info\" }");
        var again = Cli.Run("init", "--repo", _repo.Dir);
        Assert.Equal(2, again.Exit);
        Assert.Contains("already exists. Use --force", again.Err);
        Assert.Equal("{ \"failOn\": \"info\" }", File.ReadAllText(path)); // untouched

        Assert.Equal(0, Cli.Run("init", "--force", "--repo", _repo.Dir).Exit);
        Assert.Contains("PR Buddy settings", File.ReadAllText(path));
    }

    [Fact]
    public void Init_output_works_as_a_config()
    {
        Pr(SloppyClass);
        Cli.Run("init", "--repo", _repo.Dir);
        var result = Run("main", "feature", "--config", Path.Combine(_repo.Dir, "prbuddy.json"));
        Assert.Equal(0, result.Exit);
        Assert.Contains("PRB001", result.Console); // nothing switched off
    }

    [Theory]
    [InlineData(new[] { "init", "main" }, "init doesn't take refs")]
    [InlineData(new[] { "main", "--force" }, "--force only applies to prbuddy init")]
    public void Init_argument_mistakes_are_explained(string[] args, string expected)
    {
        var result = Cli.Run(args);
        Assert.Equal(2, result.Exit);
        Assert.Contains(expected, result.Err);
    }

    // ---------- --only / --skip ----------

    [Fact]
    public void Only_and_skip_choose_which_rules_run()
    {
        Pr(SloppyClass); // PRB001 (empty catch) and PRB101 (no tests)

        var all = Run("main", "feature").Console;
        Assert.Contains("PRB001", all);
        Assert.Contains("PRB101", all);

        var only = Run("main", "feature", "--only", "prb001").Console;
        Assert.Contains("PRB001", only);
        Assert.DoesNotContain("PRB101", only);

        var skip = Run("main", "feature", "--skip", "PRB001, PRB101").Console;
        Assert.DoesNotContain("PRB001", skip);
        Assert.DoesNotContain("PRB101", skip);
    }

    [Fact]
    public void Only_keeps_the_config_severities()
    {
        _repo.Write("prbuddy.json", """{ "rules": { "PRB001": "critical" } }""");
        Pr(SloppyClass);
        Assert.Equal(1, Run("main", "feature", "--only", "PRB001", "--fail-on", "critical").Exit);
    }

    [Theory]
    [InlineData(new[] { "--only", "PRB999" }, "Unknown rule(s): PRB999")]
    [InlineData(new[] { "--only", "PRB001", "--skip", "PRB002" }, "Use --only or --skip, not both")]
    public void Rule_filter_mistakes_are_explained(string[] args, string expected)
    {
        Pr(SloppyClass);
        var result = Run(["main", "feature", .. args]);
        Assert.Equal(2, result.Exit);
        Assert.Contains(expected, result.Err);
    }

    // ---------- moves end to end ----------

    [Fact]
    public void A_file_split_reads_as_moves()
    {
        var parser = "    private static int Level(string s)\n    {\n"
            + string.Concat(Enumerable.Range(0, 30).Select(i => $"        if (s == \"l{i}\") return {i};\n")) + "        return -1;\n    }\n";
        _repo.Write("src/Tool.cs", "public static class Tool\n{\n    public static int Run(string s) => Level(s);\n" + parser + "}\n").Commit("initial");
        _repo.Branch("feature")
            .Write("src/Tool.cs", "public static partial class Tool\n{\n    public static int Run(string s) => Level(s);\n}\n")
            .Write("src/Tool.Parsing.cs", "public static partial class Tool\n{\n" + parser + "}\n")
            .Commit("split");

        var result = Run("main", "feature");
        Assert.Contains("moved     Tool.Level(string) from src/Tool.cs", result.Console);
        Assert.Contains("to src/Tool.Parsing.cs", result.Console);
        Assert.DoesNotContain("PRB013", result.Console);
        Assert.DoesNotContain("PRB101", result.Console);
    }

    // ---------- refs ----------

    [Fact]
    public void Defaults_to_main_and_HEAD()
    {
        Pr(SloppyClass);
        var result = Run();
        Assert.Equal(0, result.Exit);
        Assert.Contains("main...HEAD", result.Console);
        Assert.Contains("PRB001", result.Console);
    }

    [Fact]
    public void Falls_back_to_master()
    {
        using var repo = new TempRepo(defaultBranch: "master");
        repo.Write("a.cs", "class A { }").Commit();
        repo.Branch("feature").Write("a.cs", "class A { int X; }").Commit();

        var result = Cli.Run("--repo", repo.Dir);
        Assert.Equal(0, result.Exit);
        Assert.Contains("master...HEAD", result.Console);
    }

    [Fact]
    public void Without_main_or_master_asks_for_a_base()
    {
        using var repo = new TempRepo(defaultBranch: "trunk");
        repo.Write("a.cs", "class A { }").Commit();

        var result = Cli.Run("--repo", repo.Dir);
        Assert.Equal(2, result.Exit);
        Assert.Contains("Pass the base branch explicitly", result.Err);
    }

    [Fact]
    public void Accepts_commit_shas_and_revision_expressions()
    {
        var first = _repo.Write("a.cs", "class A { }").Commit();
        var second = _repo.Write("a.cs", "class A { void M() { try { } catch { } } }").Commit();

        Assert.Contains("PRB001", Run(first, second).Console);
        Assert.Contains("PRB001", Run("HEAD~1", "HEAD").Console);
    }

    [Fact]
    public void Works_from_a_subdirectory()
    {
        Pr(SloppyClass);
        var result = Cli.Run("main", "feature", "--repo", Path.Combine(_repo.Dir, "src", "Shop"));
        Assert.Equal(0, result.Exit);
        Assert.Contains("src/Shop/Cart.cs", result.Console);
    }

    [Fact]
    public void Changes_on_main_after_branching_are_not_counted()
    {
        Pr(SloppyClass);
        _repo.Checkout("main").Write("src/Other.cs", "class Other { void M() { try { } catch { } } }").Commit("moved on");

        var result = Run("main", "feature");
        Assert.Contains("src/Shop/Cart.cs", result.Console);
        Assert.DoesNotContain("src/Other.cs", result.Console);
    }

    [Fact]
    public void No_changes_says_so()
    {
        _repo.Write("a.cs", "class A { }").Commit();
        _repo.Branch("feature");

        var result = Run("main", "feature");
        Assert.Equal(0, result.Exit);
        Assert.Contains("No changes to review", result.Console);
    }

    // ---------- console output ----------

    [Fact]
    public void Console_report_shows_reading_order_details_and_findings()
    {
        Pr(SloppyClass);
        var result = Run("main", "feature");

        Assert.Contains("Reading order", result.Console);
        Assert.Contains("1 behavior (1 public API)", result.Console);
        Assert.Contains("Cart.Count(int[])", result.Console);
        Assert.Contains("PRB001 L11", result.Console);
        Assert.Contains("PRB101", result.Console); // no tests touched
        Assert.Contains("default rules", result.Console);
    }

    [Fact]
    public void Reformatting_only_goes_to_skim()
    {
        Pr(CleanClass.Replace("    ", "\t").Replace("return items.Length;", "return items.Length; // why not"));
        var result = Run("main", "feature");

        Assert.Contains("Skim only", result.Console);
        Assert.Contains("Nothing needs a close read", result.Console);
        Assert.Contains("0 real member changes", result.Console);
    }

    [Fact]
    public void Paths_with_markup_characters_do_not_break_the_console()
    {
        Pr(SloppyClass, path: "src/[Shop]/Cart.cs");
        var result = Run("main", "feature");
        Assert.Equal(0, result.Exit);
        Assert.Contains("src/[Shop]/Cart.cs", result.Console);
    }

    // ---------- JSON and Markdown ----------

    [Fact]
    public void Json_output_is_valid_and_complete()
    {
        Pr(SloppyClass);
        var result = Run("main", "feature", "--json");
        Assert.Equal(0, result.Exit);
        Assert.Empty(result.Console);

        using var json = JsonDocument.Parse(result.Out);
        var root = json.RootElement;
        Assert.Equal("main", root.GetProperty("range").GetProperty("baseRef").GetString());

        var file = root.GetProperty("report").GetProperty("files")[0];
        Assert.Equal("src/Shop/Cart.cs", file.GetProperty("path").GetString());
        Assert.Equal("modified", file.GetProperty("status").GetString());
        Assert.Equal("behaviorChanged", file.GetProperty("semanticDiff").GetProperty("changes")[0].GetProperty("change").GetString());

        var finding = file.GetProperty("findings")[0];
        Assert.Equal("PRB001", finding.GetProperty("ruleId").GetString());
        Assert.Equal("warning", finding.GetProperty("severity").GetString());
    }

    [Fact]
    public void Markdown_output_is_plain_stdout()
    {
        Pr(SloppyClass);
        var result = Run("main", "feature", "--markdown", "--link-base", "https://example.test/blob/x");

        Assert.Empty(result.Console);
        Assert.StartsWith("<!-- prbuddy -->", result.Out);
        Assert.Contains("[L11](https://example.test/blob/x/src/Shop/Cart.cs#L11)", result.Out);
    }

    [Fact]
    public void Sarif_output_is_plain_stdout_and_valid_json()
    {
        Pr(SloppyClass);
        var result = Run("main", "feature", "--sarif");

        Assert.Equal(0, result.Exit);
        Assert.Empty(result.Console);
        using var json = JsonDocument.Parse(result.Out);
        var results = json.RootElement.GetProperty("runs")[0].GetProperty("results");
        Assert.Contains(results.EnumerateArray(), r => r.GetProperty("ruleId").GetString() == "PRB001");
    }

    // ---------- config ----------

    [Fact]
    public void Config_is_read_from_the_base_branch()
    {
        _repo.Write("prbuddy.json", """{ "rules": { "PRB001": "off" } }""");
        Pr(SloppyClass);

        var result = Run("main", "feature");
        Assert.DoesNotContain("PRB001", result.Console);
        Assert.Contains("config: prbuddy.json @", result.Console);
    }

    [Fact]
    public void A_pr_cannot_switch_off_its_own_checks()
    {
        Pr(SloppyClass);
        _repo.Write("prbuddy.json", """{ "rules": { "PRB001": "off" } }""").Commit("sneaky");

        var result = Run("main", "feature");
        Assert.Contains("PRB001", result.Console);
        Assert.Contains("PRB201", result.Console); // and the config change itself is flagged
    }

    [Fact]
    public void Config_option_overrides_the_base_config()
    {
        _repo.Write("prbuddy.json", """{ "rules": { "PRB001": "off" } }""");
        Pr(SloppyClass);
        var local = Path.Combine(_repo.Dir, "local.json");
        File.WriteAllText(local, """{ "rules": { "PRB001": "critical" } }""");

        var result = Run("main", "feature", "--config", local, "--fail-on", "critical");
        Assert.Equal(1, result.Exit);
        Assert.Contains("PRB001", result.Console);
    }

    [Fact]
    public void Config_fail_on_applies_unless_the_flag_overrides_it()
    {
        _repo.Write("prbuddy.json", """{ "failOn": "warning" }""");
        Pr(SloppyClass);

        Assert.Equal(1, Run("main", "feature").Exit);
        Assert.Equal(0, Run("main", "feature", "--fail-on", "critical").Exit);
    }

    [Fact]
    public void Config_ignore_hides_files()
    {
        _repo.Write("prbuddy.json", """{ "ignore": ["src/Shop/**"] }""");
        Pr(SloppyClass);

        var result = Run("main", "feature");
        Assert.DoesNotContain("PRB001", result.Console);
        Assert.Contains("all 1 changed file(s) are ignored by prbuddy.json", result.Console);
    }

    [Fact]
    public void Config_ignore_count_shows_next_to_other_changes()
    {
        _repo.Write("prbuddy.json", """{ "ignore": ["docs/**"] }""");
        Pr(SloppyClass);
        _repo.Write("docs/notes.md", "hello\n").Commit();

        var result = Run("main", "feature");
        Assert.Contains("1 ignored by config", result.Console);
        Assert.DoesNotContain("docs/notes.md", result.Console);
    }

    [Fact]
    public void Missing_config_file_is_an_error()
    {
        Pr(SloppyClass);
        var result = Run("main", "feature", "--config", "nope.json");
        Assert.Equal(2, result.Exit);
        Assert.Contains("'nope.json' not found", result.Err);
    }

    [Fact]
    public void Config_errors_name_the_file_they_came_from()
    {
        Pr(SloppyClass);
        var local = Path.Combine(_repo.Dir, "custom.json");
        File.WriteAllText(local, """{ "rules": { "PRB01": "off" } }""");

        var result = Run("main", "feature", "--config", local);
        Assert.Equal(2, result.Exit);
        Assert.Contains("custom.json mentions unknown rule(s): PRB01", result.Err);
    }

    [Fact]
    public void Broken_base_config_is_reported_not_ignored()
    {
        _repo.Write("prbuddy.json", "{ nope");
        Pr(SloppyClass);

        var result = Run("main", "feature", "--json");
        Assert.Equal(2, result.Exit);
        Assert.Contains("isn't valid JSON", result.Err);
        Assert.Empty(result.Out); // the error must not end up inside the JSON
    }

    // ---------- suppressions ----------

    [Fact]
    public void Suppressed_findings_are_listed_with_their_reason_and_do_not_fail_the_build()
    {
        Pr(System.Text.RegularExpressions.Regex.Replace(SloppyClass, @"^(\s*)catch", "$1// prbuddy-ignore PRB001: length can't throw; kept for the demo\n$1catch",
            System.Text.RegularExpressions.RegexOptions.Multiline));
        _repo.Write("tests/CartTests.cs", "public class CartTests { [Fact] public void A() => Assert.Equal(0, 0 + new Shop.Cart().Count([])); }").Commit();

        var result = Run("main", "feature", "--fail-on", "warning");
        Assert.True(result.Exit == 0, result.Console);
        Assert.True(result.Console.Contains("1 suppressed by comment"), result.Console);
        Assert.Contains("Suppressed by comment (1)", result.Console);
        Assert.Contains("PRB001 src/Shop/Cart.cs:L12: length can't throw; kept for the demo", result.Console);

        var md = Run("main", "feature", "--markdown").Out;
        Assert.Contains("<summary>Suppressed by comment (1)</summary>", md);
    }

    // ---------- --uncommitted ----------

    [Fact]
    public void Uncommitted_sees_unstaged_edits()
    {
        _repo.Write("src/Shop/Cart.cs", CleanClass).Commit("initial");
        _repo.Write("src/Shop/Cart.cs", SloppyClass); // not staged, not committed

        Assert.DoesNotContain("PRB001", Run("main", "HEAD").Console); // nothing committed yet

        var result = Run("--uncommitted");
        Assert.Equal(0, result.Exit);
        Assert.Contains("main...working tree", result.Console);
        Assert.Contains("PRB001 L11", result.Console);
    }

    [Fact]
    public void Uncommitted_sees_staged_and_untracked_files_but_not_ignored_ones()
    {
        _repo.Write(".gitignore", "bin/\n").Write("src/A.cs", "class A { }").Commit("initial");
        _repo.Write("src/Staged.cs", "class Staged { void M() { try { } catch { } } }").Stage("src/Staged.cs");
        _repo.Write("src/New.cs", "class New { void M() { try { } catch { } } }");
        _repo.Write("bin/Generated.cs", "class Junk { void M() { try { } catch { } } }");

        var result = Run("--uncommitted");
        Assert.Contains("src/Staged.cs", result.Console);
        Assert.Contains("src/New.cs", result.Console);
        Assert.DoesNotContain("bin/Generated.cs", result.Console);
    }

    [Fact]
    public void Uncommitted_includes_what_the_branch_already_committed()
    {
        _repo.Write("src/A.cs", "class A { }").Commit("initial");
        _repo.Branch("feature").Write("src/Committed.cs", "class C { void M() { try { } catch { } } }").Commit("on branch");
        _repo.Write("src/Pending.cs", "class P { void M() { try { } catch { } } }");

        var result = Run("--uncommitted");
        Assert.Contains("src/Committed.cs", result.Console);
        Assert.Contains("src/Pending.cs", result.Console);
    }

    [Fact]
    public void Uncommitted_reports_deleted_files()
    {
        _repo.Write("src/Old.cs", "public class Old { public void M() { } }").Write("src/Keep.cs", "class Keep { }").Commit("initial");
        _repo.Delete("src/Old.cs");

        Assert.Contains("deleted: 2 removed", Run("--uncommitted").Console);
    }

    [Fact]
    public void Uncommitted_on_a_clean_tree_has_nothing_to_report()
    {
        _repo.Write("src/A.cs", "class A { }").Commit("initial");
        var result = Run("--uncommitted");
        Assert.Equal(0, result.Exit);
        Assert.Contains("No changes to review", result.Console);
    }

    [Fact]
    public void Uncommitted_takes_a_base_but_no_head()
    {
        var result = Cli.Run("--uncommitted", "main", "feature");
        Assert.Equal(2, result.Exit);
        Assert.Contains("takes a base ref but no head ref", result.Err);
    }

    [Fact]
    public void Uncommitted_needs_at_least_one_commit()
    {
        var result = Run("--uncommitted", "main");
        Assert.Equal(2, result.Exit);
        Assert.Contains("no commits yet", result.Err);
    }

    [Fact]
    public void Uncommitted_repo_wide_rules_see_untracked_files()
    {
        _repo.Write("src/A.cs", "class A { }").Commit("initial");
        _repo.Write("src/Helper.cs", "internal static class Helper { public static int One() => 1; }");
        _repo.Write("src/Caller.cs", "public class Caller { public int M() => Helper.One(); }");

        // Helper is only used from another untracked file, so this proves the snapshot includes it.
        var result = Run("--uncommitted");
        Assert.DoesNotContain("'Helper'", result.Console);
        Assert.DoesNotContain("PRB301", result.Console);
    }

    [Fact]
    public void Uncommitted_ignores_crlf_checkouts()
    {
        // Windows default: git stores LF, the working tree has CRLF.
        _repo.SetConfig("core.autocrlf", "true");
        var lf = CleanClass.Replace("\r\n", "\n");
        _repo.Write("src/Shop/Cart.cs", lf).Commit("initial");
        _repo.Write("src/Shop/Cart.cs", lf.Replace("return items.Length;", "return items.Length + 1;").Replace("\n", "\r\n"));

        var result = Run("--uncommitted");
        Assert.Contains("1 real member changes", result.Console);
        Assert.DoesNotContain("formatting or comment changes", result.Console);
    }

    // ---------- scope creep with real history ----------

    static string Fields(int count, string tag) =>
        "class C" + tag + " {\n" + string.Concat(Enumerable.Range(0, count).Select(i => $"    int F{i};\n")) + "}\n";

    [Fact]
    public void Scope_creep_uses_git_history_to_tell_partners_from_strays()
    {
        _repo.Write("src/Game/Player.cs", Fields(1, "P")).Write("tools/Check/Probe.cs", Fields(1, "Q")).Write("src/Billing/Invoice.cs", Fields(1, "B")).Commit("initial");
        // Game and its check tool always change together.
        for (var i = 2; i <= 4; i++)
            _repo.Write("src/Game/Player.cs", Fields(i, "P")).Write("tools/Check/Probe.cs", Fields(i, "Q")).Commit($"together {i}");

        _repo.Branch("feature")
            .Write("src/Game/Player.cs", Fields(60, "P"))
            .Write("src/Game/Enemy.cs", Fields(40, "E"))
            .Write("tools/Check/Probe.cs", Fields(6, "Q"))
            .Write("src/Billing/Invoice.cs", Fields(3, "B"))
            .Commit();

        var result = Run("main", "feature");
        Assert.Contains("PRB303", result.Console);
        Assert.Contains("src/Billing/Invoice.cs", result.Console);
        Assert.DoesNotContain("tools/Check/Probe.cs (", result.Console);
    }

    // ---------- --online ----------

    sealed class CountingRegistry : PrBuddy.Core.Packages.IPackageRegistry
    {
        public int Calls { get; private set; }
        public PrBuddy.Core.Packages.PackageInfo? Lookup(string id)
        {
            Calls++;
            return new(id, Exists: false);
        }
    }

    [Fact]
    public void Packages_are_only_looked_up_with_online()
    {
        const string project = "<Project Sdk=\"Microsoft.NET.Sdk\">\n</Project>\n";
        _repo.Write("src/A/A.csproj", project).Commit("initial");
        _repo.Branch("feature")
            .Write("src/A/A.csproj", project.Replace("</Project>", "  <ItemGroup>\n    <PackageReference Include=\"Made.Up\" Version=\"1.0.0\" />\n  </ItemGroup>\n</Project>"))
            .Commit();
        var registry = new CountingRegistry();

        var offline = Cli.Run(registry, "main", "feature", "--repo", _repo.Dir);
        Assert.Equal(0, registry.Calls);
        Assert.Contains("New package dependency 'Made.Up'", offline.Console);

        var online = Cli.Run(registry, "main", "feature", "--online", "--repo", _repo.Dir);
        Assert.Equal(1, registry.Calls);
        Assert.Contains("'Made.Up' isn't on nuget.org", online.Console);
    }

    // ---------- repo-wide rules ----------

    [Fact]
    public void Repo_wide_rules_see_files_the_pr_did_not_touch()
    {
        _repo.Write("src/Format.cs", "internal static class Format { }").Commit("initial");
        _repo.Branch("feature")
            .Write("src/Unused.cs", "internal static class Leftover { }")
            .Write("src/Uses.cs", "class Uses { void M() { Format.Equals(null, null); } }")
            .Commit();

        var result = Run("main", "feature");
        Assert.Contains("New internal class 'Leftover' isn't referenced", result.Console);
        Assert.DoesNotContain("'Format'", result.Console);
    }

    // ---------- awkward changes ----------

    [Fact]
    public void Binary_files_do_not_break_anything()
    {
        _repo.Write("a.cs", "class A { }").Commit();
        _repo.Branch("feature").WriteBytes("logo.png", [0x89, 0x50, 0x4E, 0x47, 0x00, 0x00, 0xFF, 0x00]).Commit();

        var result = Run("main", "feature");
        Assert.Equal(0, result.Exit);
        Assert.Contains("logo.png", result.Console);
    }

    [Fact]
    public void Deleted_files_are_reported_as_removed_members()
    {
        _repo.Write("src/Old.cs", "public class Old { public void M() { } }").Write("src/Keep.cs", "class Keep { }").Commit();
        _repo.Branch("feature").Delete("src/Old.cs").Commit();

        var result = Run("main", "feature");
        Assert.Equal(0, result.Exit);
        Assert.Contains("deleted: 2 removed", result.Console);
    }

    [Fact]
    public void Renamed_files_are_detected()
    {
        _repo.Write("src/Old.cs", CleanClass).Commit();
        _repo.Branch("feature").Move("src/Old.cs", "src/Shop/Cart.cs").Commit();

        var result = Run("main", "feature");
        Assert.Equal(0, result.Exit);
        Assert.Contains("renamed from src/Old.cs, contents unchanged", result.Console);
    }

    [Fact]
    public void Renamed_and_edited_files_get_reviewed()
    {
        _repo.Write("src/Old.cs", CleanClass).Commit();
        _repo.Branch("feature").Move("src/Old.cs", "src/Shop/Cart.cs").Write("src/Shop/Cart.cs", SloppyClass).Commit();

        var result = Run("main", "feature");
        Assert.Contains("renamed from src/Old.cs", result.Console);
        Assert.Contains("PRB001", result.Console);
    }

    [Fact]
    public void Line_ending_changes_are_cosmetic()
    {
        // Stop git normalizing line endings, so the CRLF change really gets committed.
        _repo.Write(".gitattributes", "* -text\n");
        _repo.Write("src/A.cs", CleanClass.Replace("\r\n", "\n")).Commit();
        _repo.Branch("feature").Write("src/A.cs", CleanClass.Replace("\r\n", "\n").Replace("\n", "\r\n")).Commit();

        var result = Run("main", "feature");
        Assert.True(result.Console.Contains("0 real member changes"), result.Console);
        Assert.Contains("Skim only", result.Console);
    }

    [Fact]
    public void Syntax_errors_are_reported_not_fatal()
    {
        Pr("namespace Shop; public class Cart { public int Count(int[] items) { return items.Length }");

        var result = Run("main", "feature");
        Assert.Equal(0, result.Exit);
        Assert.Contains("syntax errors", result.Console);
    }

    [Fact]
    public void Non_csharp_text_changes_show_line_counts()
    {
        _repo.Write("README.md", "# Shop\n").Commit();
        _repo.Branch("feature").Write("README.md", "# Shop\n\nNow with carts.\n").Commit();

        var result = Run("main", "feature");
        Assert.Contains("+2 -0 lines", result.Console);
    }
}
