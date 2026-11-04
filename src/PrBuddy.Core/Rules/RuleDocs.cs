namespace PrBuddy.Core.Rules;

public sealed record RuleOption(string Name, string Default, string Meaning);

/// <param name="Why">Why the rule exists: what goes wrong if this slips through.</param>
/// <param name="Fix">What to do when it fires.</param>
public sealed record RuleDoc(string Why, string Fix, IReadOnlyList<RuleOption> Options);

/// <summary>The text behind prbuddy --explain. Every rule needs an entry; a test enforces it.</summary>
public static class RuleDocs
{
    public static RuleDoc? For(string ruleId) => Docs.TryGetValue(ruleId, out var doc) ? doc : null;

    public static IReadOnlyCollection<string> DocumentedIds => Docs.Keys;

    static RuleDoc Doc(string why, string fix, params RuleOption[] options) => new(why, fix, options);

    static readonly Dictionary<string, RuleDoc> Docs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PRB001"] = Doc(
            "An empty catch makes failures invisible: the code carries on as if it worked, and nobody finds out why things are wrong later. Catching one specific exception, filtering with `when`, or leaving a comment inside is reported as info, because it shows the choice was made on purpose.",
            "Handle the error, log it, or let it propagate. If ignoring it really is right (best-effort cleanup, say), catch the specific exception type and say why in a comment."),
        ["PRB002"] = Doc(
            "`catch (Exception)` that logs and carries on also catches bugs: null references, bad casts, out-of-memory. The program keeps running in a state nobody planned for.",
            "Catch the exceptions you expect and can handle, or rethrow with `throw;` after logging. Add a `when` filter if only some cases should be swallowed."),
        ["PRB003"] = Doc(
            "Callers can't await an `async void` method, so they can't know when it finishes. An exception inside it isn't caught by the caller; it crashes the process.",
            "Return `Task` instead. `async void` is only right for event handlers, which this rule already skips."),
        ["PRB004"] = Doc(
            "`.Wait()`, `.Result` and `.GetAwaiter().GetResult()` block a thread until async work finishes. In UI apps and older ASP.NET that can deadlock; everywhere else it wastes a thread. The rule works from syntax alone, so it can't prove `.Result` is a Task, and that case is reported as info.",
            "Use `await` and make the calling method async. If the calling code truly can't be async (a constructor, `Main` before C# 7.1), keep it but say why."),
        ["PRB005"] = Doc(
            "Turning a warning off hides it from everyone who reads this code later. The warning is often right, and AI tools add suppressions to make code compile rather than to fix it.",
            "Fix what the warning points at. If it's really a false positive, suppress the single warning code, as narrowly as possible, with a justification."),
        ["PRB006"] = Doc(
            "Each `!` tells the compiler \"trust me, this isn't null\" without checking. A pile of them usually means nullable warnings were silenced rather than handled. `null!` and `default!` are ignored, since they're the normal way to initialize required members.",
            "Check for null, make the type nullable, or restructure so the compiler can see the value is set.",
            new RuleOption("threshold", "3", "How many new `!` in one file before it's reported")),
        ["PRB007"] = Doc(
            "`NotImplementedException`, TODO comments and placeholder values like \"your-api-key\" mean the change isn't finished, or that a generated stub was never filled in.",
            "Finish the code path, or take it out of this PR. Placeholder strings in tests are allowed."),
        ["PRB008"] = Doc(
            "A comment that repeats the code (`// increment counter` above `counter++`) adds reading time and nothing else, and goes stale when the code changes. It's a common sign of generated code.",
            "Delete it, or replace it with the *why*: the reason behind the code, which the code itself can't tell you."),
        ["PRB009"] = Doc(
            "`throw ex;` inside `catch (Exception ex)` rethrows the same exception, but its stack trace now starts at this line. Whoever debugs it later loses where the error really happened.",
            "Use `throw;` to rethrow with the original stack trace. To add context, wrap it: `throw new MyException(\"what failed\", ex);`."),
        ["PRB010"] = Doc(
            "A credential in source code is readable by everyone with repo access, copied into every clone and fork, and stays in git history even after it's deleted. Only formats with a distinctive shape are matched (AWS, GitHub, Slack, Stripe, Google, OpenAI/Anthropic keys, private keys, Azure account keys, JWTs, connection-string passwords), and placeholders and published example keys are skipped. In test files it's a warning rather than critical. The report never prints the secret itself.",
            "Move it to configuration: environment variables, user secrets (`dotnet user-secrets`) or a secret store such as Azure Key Vault. If the value was ever real, rotate it now; removing it from the code doesn't remove it from git history."),
        ["PRB011"] = Doc(
            "`Debugger.Break()` stops the process when a debugger is attached and can crash it when one isn't; `Debugger.Launch()` pops up a \"choose a debugger\" dialog. Both are debugging aids that got committed.",
            "Remove it. Breakpoints belong in the IDE, not the code."),
        ["PRB012"] = Doc(
            "`Thread.Sleep` holds a whole thread doing nothing, and in async code it blocks the thread the await was meant to free. A fixed sleep is also usually a guess standing in for waiting on the real event, which makes code slow when the guess is too long and flaky when it's too short. Test files are skipped.",
            "In async code use `await Task.Delay(...)`. Better still, wait for the thing itself: a task, an event, a `SemaphoreSlim`, a `CancellationToken`, or a retry with backoff."),
        ["PRB013"] = Doc(
            "Cyclomatic complexity counts independent paths through a method (one per if, case, loop, catch, ?:, ??, && and ||). High numbers mean a method that's hard to read, review and test fully. Generated code often produces one big method where a person would write several.",
            "Split it into smaller methods with clear names, use early returns, or replace long if/switch chains with a lookup table or polymorphism.",
            new RuleOption("threshold", "25", "Complexity at which a new method is reported")),
        ["PRB101"] = Doc(
            "Behavior changed but no test changed, so either the change isn't tested or existing tests don't cover it.",
            "Add or update a test that would fail without the change. If none is needed (a pure refactor the semantic diff misread, say), mention it in the PR description."),
        ["PRB102"] = Doc(
            "The easiest way to make a failing test pass is to weaken it. Fewer assertions in an existing test, or a deleted test, deserves a second look.",
            "Check the removed assertions were really wrong or redundant. If behavior changed on purpose, update the expected values rather than dropping the check."),
        ["PRB103"] = Doc(
            "A newly skipped test has stopped protecting anything, and skips tend to become permanent.",
            "Fix the test or the code. If it has to be skipped for now, link an issue in the skip reason."),
        ["PRB104"] = Doc(
            "A test with no assertions, or only ones like `Assert.True(true)`, passes unless something throws. It looks like coverage but checks nothing.",
            "Assert on the result or the observable effect. `Assert.Throws` and mock verifications count as assertions."),
        ["PRB201"] = Doc(
            "CI workflows, `.editorconfig`, `Directory.Build.*` and `prbuddy.json` change how every other change is built and checked, so they deserve their own look.",
            "Make sure the change is intended and doesn't weaken checks (see PRB203)."),
        ["PRB202"] = Doc(
            "Every dependency is code you ship and trust. AI tools sometimes invent package names, and typosquatters register names one letter off popular ones. Offline, names one keystroke from a top-1,000 package are called out. With `--online`, packages are looked up on nuget.org.",
            "Check the name is spelled right, it's the package you meant, it's maintained, and it's actually needed."),
        ["PRB203"] = Doc(
            "`<NoWarn>`, `<Nullable>disable`, turning analyzers off or setting rule severity to none silences warnings for the whole project, not just one line.",
            "Fix the warnings, or suppress one specific warning where it happens, with a justification."),
        ["PRB301"] = Doc(
            "New code nothing calls is dead on arrival: something to read, maintain and test that does nothing. Generated code often adds helpers it never uses. Matching is by name, and members an outside caller might use (attributes, overrides, callbacks, extension holders) are skipped.",
            "Delete it, or use it. If it's called by something PR Buddy can't see (reflection, a framework convention), suppress it with that reason."),
        ["PRB302"] = Doc(
            "Rewriting a helper the codebase already has means two copies to keep in sync, and they drift apart. Local names and literals are ignored in the comparison, so renaming variables doesn't hide a copy.",
            "Call the existing method, or move the shared logic into one place both can use.",
            new RuleOption("similarity", "0.8", "Report methods at least this similar"),
            new RuleOption("copySimilarity", "0.95", "Warning at or above this, info below"),
            new RuleOption("minTokens", "40", "Ignore shorter method bodies")),
        ["PRB303"] = Doc(
            "Small changes far from the rest of a PR are easy to miss in review and often unintended: a stray edit, or a second change hiding inside the first. Areas that usually change together in the last 300 commits don't count.",
            "Move unrelated changes into their own PR, or say in the description why they belong.",
            new RuleOption("focusShare", "0.7", "Share of changed code the main area needs")),
        ["PRB900"] = Doc(
            "A prbuddy-ignore comment without a reason, or naming a rule that doesn't exist, isn't applied, so the finding stays. A suppression this PR adds that matches nothing is reported too (as info): usually a wrong id or a leftover.",
            "Give the reason after a colon (`prbuddy-ignore PRB001: why this is fine`), fix the rule id, or remove the comment."),
    };
}
