using PrBuddy.Core;
using PrBuddy.Core.Rules;
using PrBuddy.Core.Rules.Code;

namespace PrBuddy.Tests;

public class CodeRuleTests
{
    static IReadOnlyList<Finding> Run(IRule rule, string? before, string after, string path = "src/Service.cs") =>
        Pr.Run(rule, Pr.File(path, before, after));

    [Fact]
    public void EmptyCatch_reports_only_catches_the_pr_added()
    {
        const string before = """
            class Service
            {
                void Old() { try { Work(); } catch { } }
                void Work() { }
            }
            """;
        const string after = """
            class Service
            {
                void Old() { try { Work(); } catch { } }
                void Work() { }
                void New()
                {
                    try { Work(); }
                    catch (System.Exception)
                    {
                    }
                }
            }
            """;

        var finding = Assert.Single(Run(new EmptyCatchRule(), before, after));
        Assert.Equal("PRB001", finding.RuleId);
        Assert.Equal(8, finding.Line);
    }

    [Fact]
    public void SwallowedException_reports_catch_all_without_rethrow()
    {
        const string after = """
            using System;
            using System.IO;
            class Service
            {
                void A() { try { } catch (Exception ex) { Log(ex); } }
                void B() { try { } catch (Exception ex) { Log(ex); throw; } }
                void C() { try { } catch (Exception ex) when (ex is TimeoutException) { Log(ex); } }
                void D() { try { } catch (IOException ex) { Log(ex); } }
                void Log(Exception ex) { }
            }
            """;

        var finding = Assert.Single(Run(new SwallowedExceptionRule(), null, after));
        Assert.Equal(5, finding.Line);
    }

    [Fact]
    public void AsyncVoid_ignores_event_handlers()
    {
        const string after = """
            using System;
            using System.Threading.Tasks;
            class Form
            {
                async void Fire() => await Task.Delay(1);
                async void OnClick(object sender, EventArgs e) => await Task.Delay(1);
                async Task Fine() => await Task.Delay(1);
            }
            """;

        var finding = Assert.Single(Run(new AsyncVoidRule(), null, after));
        Assert.Contains("Fire", finding.Message);
    }

    [Fact]
    public void SyncOverAsync_reports_blocking_calls()
    {
        const string after = """
            using System.Threading.Tasks;
            class Service
            {
                int M(Task<int> task)
                {
                    task.Wait();
                    var a = GetAsync().GetAwaiter().GetResult();
                    return task.Result;
                }
                Task<int> GetAsync() => Task.FromResult(1);
            }
            """;

        var findings = Run(new SyncOverAsyncRule(), null, after);
        Assert.Equal([6, 7, 8], findings.Select(f => f.Line!.Value));
        Assert.Equal(Severity.Info, findings[2].Severity);
    }

    [Fact]
    public void SuppressedWarnings_reports_pragmas_nullable_and_attributes()
    {
        const string after = """
            using System.Diagnostics.CodeAnalysis;
            #pragma warning disable CS8618
            #nullable disable
            class Service
            {
                [SuppressMessage("Design", "CA1031")]
                void M() { }
            }
            #pragma warning restore CS8618
            """;

        var findings = Run(new SuppressedWarningsRule(), null, after);
        Assert.Equal([2, 3, 6], findings.Select(f => f.Line!.Value));
        Assert.Contains("CS8618", findings[0].Message);
    }

    [Fact]
    public void NullForgiving_needs_several_and_ignores_null_bang()
    {
        const string few = """
            class S { string M(string? a, string? b) => a! + b!; }
            """;
        const string many = """
            class S { string M(string? a, string? b, string? c) => a! + b! + c!; }
            """;
        const string idiomatic = """
            class S { public string A { get; set; } = null!; public string B = null!; public string C = default!; }
            """;

        Assert.Empty(Run(new NullForgivingRule(), null, few));
        Assert.Single(Run(new NullForgivingRule(), null, many));
        Assert.Empty(Run(new NullForgivingRule(), null, idiomatic));
    }

    [Fact]
    public void Placeholder_reports_unfinished_code()
    {
        const string after = """
            using System;
            class Service
            {
                // TODO: handle errors
                void A() => throw new NotImplementedException();
                string Key = "your-api-key";
                string Name = "Ada";
            }
            """;

        var findings = Run(new PlaceholderRule(), null, after);
        Assert.Equal([4, 5, 6], findings.OrderBy(f => f.Line).Select(f => f.Line!.Value));
        Assert.Equal(Severity.Info, findings.Single(f => f.Line == 4).Severity);
    }

    [Fact]
    public void Placeholder_strings_are_allowed_in_tests()
    {
        const string after = """class FooTests { string Key = "your-api-key"; }""";
        Assert.Empty(Run(new PlaceholderRule(), null, after, "tests/FooTests.cs"));
    }

    [Theory]
    [InlineData("Increment the counter", "counter++;", true)]
    [InlineData("Create a new user", "var user = new User();", true)]
    [InlineData("Loop through all users", "foreach (var user in users)", true)]
    [InlineData("Check if user is null", "if (user == null)", true)]
    [InlineData("Save changes", "db.SaveChanges();", true)]
    [InlineData("Retry because the API rate-limits bursts", "await SendAsync(request);", false)]
    [InlineData("Arrange", "var sut = new Service();", false)]
    [InlineData("Act & Assert", "Assert.Throws<X>(() => sut.Run());", false)]
    [InlineData("Off by one on purpose: the API is 1-based", "index++;", false)]
    public void RedundantComment_detects_comments_that_repeat_the_code(string comment, string code, bool expected) =>
        Assert.Equal(expected, RedundantCommentRule.IsRedundant(comment, code));

    [Fact]
    public void RedundantComment_reports_the_comment_line()
    {
        const string after = """
            class Counter
            {
                int count;
                void Tick()
                {
                    // Increment the count
                    count++;
                }
            }
            """;

        var finding = Assert.Single(Run(new RedundantCommentRule(), null, after));
        Assert.Equal(6, finding.Line);
    }
}
