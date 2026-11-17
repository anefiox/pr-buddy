using PrBuddy.Core.Rules.Code;

namespace PrBuddy.Tests;

public sealed class CallbackRuleTests
{
    [Theory]
    [InlineData("token.Register(() => Work());")]
    [InlineData("_ = token.UnsafeRegister(_ => Work(), null);")]
    [InlineData("optionalToken?.Register(Work);")]
    public void ReportsDiscardedCancellationRegistration(string statement)
    {
        var after = CancellationCode(statement);
        var finding = Assert.Single(Pr.Run(new DiscardedCancellationRegistrationRule(),
            Pr.File("src/Callback.cs", CancellationCode("Work();"), after)));
        Assert.Equal("PRB015", finding.RuleId);
        Assert.Empty(Pr.Run(new DiscardedCancellationRegistrationRule(), Pr.File("src/Callback.cs", after, after)));
    }

    [Theory]
    [InlineData("using var registration = token.Register(Work);")]
    [InlineData("var registration = token.Register(Work); registration.Unregister();")]
    [InlineData("new Custom().Register(Work);")]
    [InlineData("CancellationToken.None.Register(Work);")]
    [InlineData("new CancellationToken().Register(Work);")]
    public void AllowsRetainedRegistrationsAndOtherReceivers(string statement) =>
        Assert.Empty(Pr.Run(new DiscardedCancellationRegistrationRule(),
            Pr.File("src/Callback.cs", null, CancellationCode(statement))));

    static string CancellationCode(string statement) => $$"""
        using System;
        using System.Threading;
        class Callback
        {
            static void Run(CancellationToken token, CancellationToken? optionalToken) { {{statement}} }
            static void Work() { }
            class Custom { public void Register(Action callback) { } }
        }
        """;

    [Fact]
    public void FindsMissingCleanupInAlternateCompletion()
    {
        var after = CleanupCode("() => result.TrySetResult(false)");
        var finding = Assert.Single(Pr.Run(new CallbackCleanupRule(), Pr.File("src/Callback.cs", null, after)));
        Assert.Contains("resource", finding.Message);
        Assert.Contains("result", finding.Message);
        Assert.Empty(Pr.Run(new CallbackCleanupRule(), Pr.File("src/Callback.cs", after, after)));
    }

    [Theory]
    [InlineData("() => { resource.Dispose(); result.TrySetResult(false); }")]
    [InlineData("() => result.TrySetResult(Release(resource))")]
    [InlineData("() => other.TrySetResult(false)")]
    public void AllowsCleanupOnBothPathsAndDifferentCompletionSources(string callback) =>
        Assert.Empty(Pr.Run(new CallbackCleanupRule(), Pr.File("src/Callback.cs", null, CleanupCode(callback))));

    static string CleanupCode(string callback) => $$"""
        using System;
        using System.Threading.Tasks;
        class Callback
        {
            static void Run(IDisposable resource)
            {
                var result = new TaskCompletionSource<bool>();
                var other = new TaskCompletionSource<bool>();
                Schedule(() => result.TrySetResult(Release(resource)),
                    {{callback}});
            }
            static bool Release(IDisposable resource) { resource.Dispose(); return true; }
            static void Schedule(Action success, Action cancel) { }
        }
        """;

    [Theory]
    [InlineData("if (!IsRunning) return; Changed?.Invoke(); Read();", 1)]
    [InlineData("Changed?.Invoke(); if (!IsRunning) return; Read();", 0)]
    [InlineData("Changed?.Invoke(); if (IsRunning) Read();", 0)]
    [InlineData("Changed?.Invoke(); if (IsRunning && Read() > 0) return;", 0)]
    [InlineData("Changed?.GetInvocationList(); Read();", 0)]
    public void RequiresLifecycleCheckAfterSynchronousCallback(string body, int expected)
    {
        var after = $$"""
            using System;
            class Source
            {
                public event Action Changed;
                private bool _running = true;
                bool IsRunning => _running;
                public void Dispose() => Stop();
                void Stop() => _running = false;
                int Read() { if (!IsRunning) throw new InvalidOperationException(); return 1; }
                void Publish() { {{body}} }
            }
            """;
        Assert.Equal(expected, Pr.Run(new CallbackLifetimeRule(), Pr.File("src/Source.cs", null, after)).Count);
    }

    [Fact]
    public void ReportsNewCallbackBeforeAnExistingAccess()
    {
        const string before = """
            using System;
            class Source
            {
                private bool _disposed;
                public void Dispose() => _disposed = true;
                void Read() { ObjectDisposedException.ThrowIf(_disposed, this); }
                void Publish(Action callback)
                {
                    Read();
                }
            }
            """;
        var after = before.Replace("        Read();", "        callback();\n        Read();");
        var finding = Assert.Single(Pr.Run(new CallbackLifetimeRule(), Pr.File("src/Source.cs", before, after)));
        Assert.Equal(9, finding.Line);
    }

    [Theory]
    [InlineData("if (Matches(e)) return;", 1)]
    [InlineData("if (!IsRunning) return; if (Matches(e)) return;", 0)]
    [InlineData("if (IsRunning && Matches(e)) return;", 0)]
    public void ChecksEventHandlerCallingInheritedLifecycleSensitiveAccessor(string handlerBody, int expected)
    {
        const string baseCode = """
            using System;
            class Base
            {
                private bool _disposed;
                protected bool IsRunning => !_disposed;
                public void Dispose() => _disposed = true;
                protected int Read() { ObjectDisposedException.ThrowIf(_disposed, this); return 1; }
            }
            """;
        var after = $$"""
            using System;
            class Binding : Base
            {
                void Subscribe(ExternalPublisher publisher) => publisher.Changed += OnChanged;
                void OnChanged(object sender, ExternalEvent e) { {{handlerBody}} }
                bool Matches(ExternalEvent e) => Read() > 0;
            }
            """;
        var findings = Pr.RunInRepo(new CallbackLifetimeRule(), new Dictionary<string, string> { ["src/Base.cs"] = baseCode },
            Pr.File("src/Binding.cs", null, after));
        Assert.Equal(expected, findings.Count);
    }
}
