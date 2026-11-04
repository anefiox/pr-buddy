using PrBuddy.Core;
using PrBuddy.Core.Rules.Code;
using PrBuddy.Core.Rules.Structure;

namespace PrBuddy.Tests;

/// <summary>PRB009–PRB013.</summary>
public class NewCodeRuleTests
{
    // Fake credentials are assembled at run time so that no complete one ever sits in this file:
    // PR Buddy itself, and GitHub's push protection, would otherwise flag it.
    static string Fake(string prefix, char fill, int length) => prefix + new string(fill, 1) + string.Concat(Enumerable.Range(0, length - 1).Select(i => (char)('A' + i % 26)));

    static readonly string AwsKey = "AK" + "IA" + "Q4ZJ7M2KXW8RN3TP";
    static readonly string GitHubToken = "gh" + "p_" + Fake("", 'a', 36);
    static readonly string AnthropicKey = "sk-" + "ant-" + Fake("api03-", 'x', 30);
    static readonly string PrivateKeyHeader = "-----BEGIN " + "RSA PRIVATE KEY-----";

    // ---------- PRB010 ----------

    [Fact]
    public void Secrets_in_code_and_config_are_critical_and_redacted()
    {
        var code = $"class Keys\n{{\n    const string Aws = \"{AwsKey}\";\n    const string Gh = \"{GitHubToken}\";\n}}\n";
        // prbuddy-ignore PRB010: fake password, the input this test checks gets detected
        var settings = $"{{\n  \"ConnectionStrings\": {{ \"Db\": \"Server=db;User Id=app;Password=Hunter2Rocks!;\" }},\n  \"Claude\": \"{AnthropicKey}\"\n}}\n";
        var key = $"{PrivateKeyHeader}\nMIIEowIBAAKCAQEA...\n";

        var findings = Pr.Run(new HardcodedSecretRule(),
            Pr.File("src/Keys.cs", null, code),
            Pr.File("src/appsettings.json", null, settings),
            Pr.File("deploy/id_rsa", null, key));

        Assert.Equal(5, findings.Count);
        Assert.All(findings, f => Assert.Equal(Severity.Critical, f.Severity));
        Assert.Contains(findings, f => f is { Path: "src/Keys.cs", Line: 3 } && f.Message.Contains("AWS access key (AKIA…)"));
        Assert.Contains(findings, f => f is { Path: "src/Keys.cs", Line: 4 } && f.Message.Contains("GitHub token (ghp_…)"));
        Assert.Contains(findings, f => f is { Path: "src/appsettings.json", Line: 2 } && f.Message.Contains("password in a connection string"));
        Assert.Contains(findings, f => f is { Path: "src/appsettings.json", Line: 3 } && f.Message.Contains("OpenAI or Anthropic API key"));
        Assert.Contains(findings, f => f is { Path: "deploy/id_rsa", Line: 1 } && f.Message.Contains("private key"));

        // The report must never contain the secret itself.
        foreach (var secret in new[] { AwsKey, GitHubToken, AnthropicKey, "Hunter2Rocks!" })
            Assert.DoesNotContain(findings, f => f.Message.Contains(secret));
    }

    [Theory]
    [InlineData("var key = \"AKIAIOSFODNN7EXAMPLE\";")]                                      // AWS's documented example key
    [InlineData("\"Db\": \"Server=db;Password=${DB_PASSWORD};\"")]                          // environment variable
    [InlineData("\"Db\": \"Server=db;Password=$(DbPassword);\"")]                           // MSBuild property
    [InlineData("\"Db\": \"Server=db;Password=%DB_PASSWORD%;\"")]                           // Windows variable
    [InlineData("\"Db\": \"Server=db;Password={0};\"")]                                     // format placeholder
    [InlineData("\"Db\": \"Server=db;Password=<your-password>;\"")]
    [InlineData("\"Db\": \"Server=db;Password=changeme;\"")]
    [InlineData("\"Db\": \"Server=db;Password=********;\"")]
    [InlineData("\"Db\": \"Server=db;Integrated Security=true;\"")]                         // no password at all
    [InlineData("var hash = \"d41d8cd98f00b204e9800998ecf8427e\";")]                        // long hex isn't a known format
    [InlineData("public string Password { get; set; } = \"\";")]
    public void Placeholders_and_ordinary_strings_are_not_secrets(string line) =>
        Assert.Empty(Pr.Run(new HardcodedSecretRule(), Pr.File("src/appsettings.json", null, line + "\n")));

    [Fact]
    public void Secrets_in_tests_are_warnings_and_old_lines_are_ignored()
    {
        var before = $"class T {{ const string Old = \"{AwsKey}\"; }}\n";
        Assert.Empty(Pr.Run(new HardcodedSecretRule(), Pr.File("src/T.cs", before, before + "// unrelated\n")));

        var inTest = Pr.Run(new HardcodedSecretRule(), Pr.File("tests/ApiTests.cs", null, $"class ApiTests {{ const string K = \"{GitHubToken}\"; }}\n"));
        Assert.Equal(Severity.Warning, Assert.Single(inTest).Severity);
    }

    // ---------- PRB009 ----------

    [Fact]
    public void Throw_ex_is_reported_but_throw_and_wrapping_are_fine()
    {
        const string code = """
            using System;
            class S
            {
                void A() { try { } catch (Exception ex) { Log(ex); throw ex; } }
                void B() { try { } catch (Exception ex) { Log(ex); throw; } }
                void C() { try { } catch (Exception ex) { throw new InvalidOperationException("context", ex); } }
                void D() { try { } catch (Exception ex) { var other = new Exception(); throw other; } }
                void E() { try { } catch (Exception ex) { Action a = () => { var ex2 = ex; }; throw; } }
                void Log(Exception e) { }
            }
            """;

        var finding = Assert.Single(Pr.Run(new RethrowResetsStackRule(), Pr.File("src/S.cs", null, code)));
        Assert.Equal(4, finding.Line);
        Assert.Contains("'throw ex;'", finding.Message);
    }

    // ---------- PRB011 ----------

    [Fact]
    public void Debugger_calls_are_reported()
    {
        const string code = """
            using System.Diagnostics;
            class S
            {
                void A() { Debugger.Break(); }
                void B() { System.Diagnostics.Debugger.Launch(); }
                void C() { if (Debugger.IsAttached) Trace.WriteLine("ok"); }
            }
            """;

        Assert.Equal([4, 5], Pr.Run(new DebuggingLeftoversRule(), Pr.File("src/S.cs", null, code)).Select(f => f.Line!.Value));
    }

    // ---------- PRB012 ----------

    [Fact]
    public void Thread_sleep_in_production_code_is_reported_with_async_advice()
    {
        const string code = """
            using System.Threading;
            using System.Threading.Tasks;
            class S
            {
                void A() { Thread.Sleep(100); }
                async Task B() { Thread.Sleep(100); await Task.Yield(); }
                async Task C() { await Task.Delay(100); }
            }
            """;

        var findings = Pr.Run(new ThreadSleepRule(), Pr.File("src/S.cs", null, code));
        Assert.Equal([5, 6], findings.Select(f => f.Line!.Value));
        Assert.Contains("If it's waiting for something", findings[0].Message);
        Assert.Contains("await Task.Delay", findings[1].Message);

        Assert.Empty(Pr.Run(new ThreadSleepRule(), Pr.File("tests/STests.cs", null, code)));
    }

    // ---------- PRB013 ----------

    [Fact]
    public void Complexity_counts_branch_points()
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("""
            class S
            {
                int M(int a, int? b, bool c)
                {
                    if (a > 0 && c) return 1;                  // if, &&
                    for (var i = 0; i < a; i++) { }            // for
                    var x = b ?? 0;                            // ??
                    var y = c ? 1 : 2;                         // ?:
                    switch (a) { case 1: break; case 2: break; default: break; }   // 2 cases
                    try { } catch { }                          // catch
                    return a switch { 1 => 1, _ => 0 };        // 2 arms
                }
            }
            """);
        var method = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().Single();
        Assert.Equal(1 + 2 + 1 + 1 + 1 + 2 + 1 + 2, ComplexNewMethodRule.Complexity(method.Body!));
    }

    [Fact]
    public void Only_new_methods_over_the_threshold_are_reported()
    {
        static string Method(string name, int ifs) =>
            $"    public int {name}(int a)\n    {{\n" + string.Concat(Enumerable.Range(0, ifs).Select(i => $"        if (a == {i}) return {i};\n")) + "        return -1;\n    }\n";

        var before = "public class S\n{\n" + Method("Old", 30) + "}\n";
        var after = "public class S\n{\n" + Method("Old", 31) + Method("Big", 26) + Method("Small", 5) + "}\n";

        var finding = Assert.Single(Pr.Run(new ComplexNewMethodRule(), Pr.File("src/S.cs", before, after)));
        Assert.Contains("'S.Big' has cyclomatic complexity 27", finding.Message);
        Assert.Equal(Severity.Info, finding.Severity);

        Assert.Empty(Pr.Run(new ComplexNewMethodRule(threshold: 40), Pr.File("src/S.cs", before, after)));
    }

    [Fact]
    public void Several_complex_methods_in_one_file_are_one_finding()
    {
        static string Method(string name, int ifs) =>
            $"    public int {name}(int a)\n    {{\n" + string.Concat(Enumerable.Range(0, ifs).Select(i => $"        if (a == {i}) return {i};\n")) + "        return -1;\n    }\n";

        var after = "public class P\n{\n" + Method("Parse", 26) + Method("Run", 40) + Method("Fine", 3) + "}\n";
        var finding = Assert.Single(Pr.Run(new ComplexNewMethodRule(), Pr.File("src/P.cs", null, after)));

        Assert.StartsWith("2 new methods are over complexity 25: P.Run (41, L", finding.Message);
        Assert.Contains("P.Parse (27, L3)", finding.Message);
        Assert.Equal(33, finding.Line); // points at the worst one: Run starts after Parse's 30 lines
    }
}
