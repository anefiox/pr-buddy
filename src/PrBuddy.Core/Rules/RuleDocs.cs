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
        ["PRB014"] = Doc(
            "A loop can still consume CLR stack if it calls a helper that calls back into the loop's method. On deep input a repeating cycle can overflow the stack. PR Buddy binds both synchronous calls to methods in the same type and file and reports cycles absent from the base version.",
            "Put pending work on the loop's worklist, or return the next step for the loop to process. Add a regression test for a deep input that follows the re-entry path."),
        ["PRB015"] = Doc(
            "Discarding the result of CancellationToken.Register or UnsafeRegister loses the handle used to unregister the callback. The callback can retain state or run after the operation has finished.",
            "Keep the registration for the intended callback lifetime, then dispose or unregister it."),
        ["PRB016"] = Doc(
            "Sibling callbacks can complete the same task through different paths. When one disposes a captured resource or passes it to a helper that disposes it, a sibling that completes the task without using the resource can skip cleanup.",
            "Release the owned resource on each terminal path, or centralize cleanup where callback ownership and completion are coordinated."),
        ["PRB017"] = Doc(
            "An event handler can run during a notification that stops or disposes the instance. An explicit synchronous callback can also invalidate the instance before the caller resumes. This rule looks for a call that throws on a state set by Dispose, including through a source base class where available.",
            "Recheck the lifecycle state immediately before calling the accessor, or use an accessor that handles a stopped instance."),
        ["PRB018"] = Doc(
            "A method returns early once a member is set, then sets that member inside a try before later work that can still fail. When the later work throws, the catch leaves the member set, so the early return blocks every retry and the error sticks.",
            "Assign the member only after all the work has succeeded, or reset it in the catch."),
        ["PRB019"] = Doc(
            "A new collection field of objects is added to but never removed from, or only emptied by Clear() in some other method. It holds a strong reference to everything it has seen, which can stop items being garbage collected and quietly grows memory.",
            "Remove items when they stop being relevant, bound the collection, or hold them weakly. If Clear() really runs often enough, ignore the info finding."),
        ["PRB020"] = Doc(
            "A new member of a [Flags] enum has a plain number, or an implicit value, that isn't a single bit. It overlaps other flags, so setting it also sets them and HasFlag checks give the wrong answer. Values written with member names, zero, -1 and an all-flags mask are allowed.",
            "Give it the next free power of two, or write the combination with member names (A | B) if that's what it means."),
        ["PRB021"] = Doc(
            "A new non-public method returns a Task or ValueTask but has no Async suffix, while at least two other task-returning methods in the same type do. The name no longer tells callers it has to be awaited. Public, protected, override and attributed methods are skipped, since their names can be fixed by an outside contract.",
            "Rename it with the Async suffix, matching the rest of the type."),
        ["PRB022"] = Doc(
            "A parameter of a new method is never read. Either it's left over, or the code forgot to use it and quietly ignores what callers pass. Public, protected, virtual and override methods are skipped, since an interface or caller elsewhere may need the parameter, except for parameters ASP.NET binds with [FromQuery], [FromBody] and the like, whose values a client sends.",
            "Remove the parameter, or use it if it was meant to change the result. Prefix it with _ if it has to stay."),
        ["PRB023"] = Doc(
            "A lone ';' is an empty statement. After a block (\"};\") it's noise. Right after if, while or for it is the whole body, so the block below runs regardless of the condition.",
            "Remove it. If an empty loop body is meant, write {} so the intent is visible."),
        ["PRB024"] = Doc(
            "A new or renamed file holds one type whose name doesn't match the file name. Go-to-file and search land in the wrong place, and a case-only mismatch causes trouble on case-sensitive file systems. Partial types and Name.Suffix.cs files are allowed.",
            "Rename the file or the type so they match exactly."),
        ["PRB025"] = Doc(
            "A new parameter is named like an id (orderId) but its type is the object itself (Order). Readers and callers expect a number or key and get confused about what to pass.",
            "Name it after the object (order), or pass the id instead."),
        ["PRB026"] = Doc(
            "A string is encoded to a byte array and the next statement refuses it when the array is too long. An oversized input, often untrusted, makes the code allocate its whole size before refusing it.",
            "Check Encoding.GetByteCount(text) against the limit first, then encode."),
        ["PRB027"] = Doc(
            "A catch inside a try calls the same error handler as the catch-all around it. If the handler throws, as handlers that honour a throw-on-error option do, the outer catch handles the error again and the caller sees it wrapped twice.",
            "Throw from the inner catch, adding context if needed, and let the outer catch handle the error once."),
        ["PRB028"] = Doc(
            "A media type or content type is matched with Contains, or an exact comparison is OR'ed with a Contains of the same value. Contains accepts anything that merely includes the text, and makes the exact check next to it pointless.",
            "Compare the media type, without parameters, with Equals and StringComparison.OrdinalIgnoreCase. Keep Contains only if partial matches are really wanted."),
        ["PRB029"] = Doc(
            "A new property only returns, and maybe assigns, a private field that nothing else needs directly. That's two declarations for what an auto-property says in one. Fields passed by ref, locked on or marked volatile are left alone.",
            "Replace the pair with an auto-property, using a private setter if the field is assigned elsewhere."),
        ["PRB030"] = Doc(
            "New code reads a tuple through Item1, Item2 and so on. The reader has to look up what each position holds, and swapping two values of the same type compiles fine.",
            "Deconstruct the tuple into named variables, or give its elements names in the method's return type."),
        ["PRB031"] = Doc(
            "A field, constant, property or variable named like a credential (ClientSecret, ApiKey, AccessToken) is set to a random-looking string. PRB010 only knows formats with a distinctive shape; this catches the rest. A name like ClientId or AppId is info: public clients may embed one, but its origin and owner should be clear. The value is never printed.",
            "Load secrets from configuration or a secret store and rotate any that were real. For an embedded id, say where it was registered and who owns it, or make it configurable."),
        ["PRB032"] = Doc(
            "A new EF Core migration has no .Designer.cs and no [Migration] attribute. EF finds migrations through [DbContext] and [Migration], which dotnet ef writes into the designer file, so this one is never applied and nothing fails.",
            "Regenerate the migration with dotnet ef migrations add, or add the designer file with [DbContext(typeof(...))] and [Migration(\"<id>\")]."),
        ["PRB033"] = Doc(
            "A new comment tells the story of the change (\"the former algorithm\", \"used to take\", \"now uses\", \"this PR\") instead of explaining the code as it is. After merge nobody can see the old version, so the comment explains something that isn't there.",
            "Put the history in the commit message or PR description, and keep comments for invariants and reasons."),
        ["PRB034"] = Doc(
            "The code calls an API this repository has banned in prbuddy.json. Matching is by name: \"Utf8Parser.TryParse\" matches that call however it's qualified, and \".Chunk\" matches any member called Chunk. Tests are skipped unless includeTests is true. The rule does nothing until you list APIs.",
            "Use what the reason says instead. An entry can be scoped to paths, which makes it a layering rule: \"AccreditationApiMapper\": { \"reason\": \"The domain must not use the API layer\", \"in\": [\"**/DomainModel/**\"] }. To ban something everywhere: \"PRB034\": { \"apis\": { \"Utf8Parser.TryParse\": \"Use SessionParseState.TryGetInt\", \".Chunk\": \"No LINQ in hot paths\" } }."),
        ["PRB035"] = Doc(
            "A new #nullable enable turns nullable warnings on for one file. In a project with nullable off, that pulls annotation changes into code the PR isn't about; in a project with it on, the line does nothing. The project setting comes from the nearest .csproj or Directory.Build.props.",
            "Remove it, or turn nullable on for the whole project in a PR of its own."),
        ["PRB036"] = Doc(
            "Added C# lines end in spaces or tabs, or are blank lines with whitespace on them. Editors strip these later, which turns them into noise in someone else's diff.",
            "Trim them, or set trim_trailing_whitespace = true in .editorconfig so the editor does it."),
        ["PRB037"] = Doc(
            "The same call with the same arguments is used as a condition twice in one method, for example in a bool local and again in an if. It repeats the work, and if the call reads changing state the two checks can disagree. Calls whose arguments are reassigned in between aren't reported.",
            "Store the result in a local and use it in both places."),
        ["PRB038"] = Doc(
            "CancellationToken.Register or UnsafeRegister is given a lambda that captures one variable. Each call allocates a closure and a delegate; the overload that takes a state object, with a static lambda, avoids both.",
            "Use Register(static state => ((T)state!).Cancel(), value). It matters most on hot paths."),
        ["PRB039"] = Doc(
            "A new constructor builds its base the same way another constructor does (both call base(new X(...))) instead of chaining to it with this(...). Construction then happens in two places, and a later change to one misses the other.",
            "Chain to the existing constructor with this(...), adding the new values to it if needed."),
        ["PRB040"] = Doc(
            "A nullable parameter is null-checked three times or more in a new method or constructor (options?.A, options?.B, options ?? ...). Each check is another branch to read and test.",
            "Default it once at the top (options ??= new Options();), or make the parameter non-nullable with a default value."),
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
        ["PRB105"] = Doc(
            "A test looks up a non-public member by name with BindingFlags.NonPublic. Renaming or changing that member still compiles and only fails when the test runs, and the test is tied to implementation details.",
            "Make the member internal and expose it with InternalsVisibleTo, or test the behavior through the public API."),
        ["PRB106"] = Doc(
            "A test asserts on measured time: a Stopwatch's Elapsed, a DateTime difference. Shared CI machines pause and throttle, so the bound either fails at random or is so loose it misses the regression it was written for.",
            "Move the timing check into a benchmark that is tracked over time, or assert on behavior that causes the speed, such as no wait being started."),
        ["PRB107"] = Doc(
            "A test has two or more Assert.True or Assert.False calls with no message. When one fails, the output only says a value was false, so you have to rerun or debug to find which check it was.",
            "Give each a message, or use an assertion that shows the values, such as Assert.Equal or Assert.Contains."),
        ["PRB108"] = Doc(
            "Test code names a real external host in a URL. A test that calls it depends on the network and on someone else's server; one that only uses it as data can still resolve it by accident. Reserved names (example.com, *.test, *.invalid, localhost), private addresses and well-known schema URIs are allowed.",
            "Use example.com, a .test name or localhost for data. For a real dependency such as a script, ship it with the tests or serve it locally."),
        ["PRB109"] = Doc(
            "An async assertion such as Assert.ThrowsAsync or x.Should().ThrowAsync() returns a task that nothing awaits. The test can finish, and pass, before the assertion fails. In NUnit files it's info, because NUnit's Assert.ThrowsAsync blocks before version 5.",
            "Await the assertion and make the test async Task."),
        ["PRB201"] = Doc(
            "CI workflows, `.editorconfig`, `Directory.Build.*` and `prbuddy.json` change how every other change is built and checked, so they deserve their own look.",
            "Make sure the change is intended and doesn't weaken checks (see PRB203)."),
        ["PRB202"] = Doc(
            "Every dependency is code you ship and trust. AI tools sometimes invent package names, and typosquatters register names one letter off popular ones. Offline, names one keystroke from a top-1,000 package are called out. With `--online`, packages are looked up on nuget.org.",
            "Check the name is spelled right, it's the package you meant, it's maintained, and it's actually needed."),
        ["PRB203"] = Doc(
            "`<NoWarn>`, `<Nullable>disable`, turning analyzers off or setting rule severity to none silences warnings for the whole project, not just one line.",
            "Fix the warnings, or suppress one specific warning where it happens, with a justification."),
        ["PRB204"] = Doc(
            "A GitHub workflow uses an action, or pulls a container image, by tag or branch. Tags can be moved and images re-pushed, so the code that runs in CI, with its secrets, can change without a commit in this repo. It's a warning when the same file already pins other actions to a commit SHA.",
            "Pin actions to a full commit SHA with the version in a comment, and images with @sha256:<digest>. Put a new publisher through your dependency review."),
        ["PRB205"] = Doc(
            "The PR adds a file that looks like a local database (.sqlite, .db, .mdf), a build output (.pdb, .nupkg, anything under bin/ or obj/), per-user IDE state (.suo, .user) or a log. These are machine-specific, can hold real data, and change on every run. Files in test folders, testdata, fixtures or samples are left alone.",
            "Remove the file and add its pattern to .gitignore, or explain in the PR why it belongs in the repo."),
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
        ["PRB304"] = Doc(
            "A public member of a public type was removed, renamed or given different parameters. Code outside this repository that uses it stops compiling, or fails at run time against a new binary. Changes to attributes, modifiers or the return type alone aren't reported.",
            "If anyone builds against this, keep the old signature as an overload (perhaps [Obsolete]) or call out the break in the release notes."),
        ["PRB305"] = Doc(
            "A member added in a Razor component's @code block isn't used by the component's markup or code, the PR's other files or the repo's C#. PR Buddy only parses .cs files, so the unused-code check (PRB301) can't see component code. Members with attributes ([Parameter], [Inject]...), overrides and lifecycle methods are skipped.",
            "Remove it, or use it if it was meant to be."),
        ["PRB900"] = Doc(
            "A prbuddy-ignore comment without a reason, or naming a rule that doesn't exist, isn't applied, so the finding stays. A suppression this PR adds that matches nothing is reported too (as info): usually a wrong id or a leftover.",
            "Give the reason after a colon (`prbuddy-ignore PRB001: why this is fine`), fix the rule id, or remove the comment."),
    };
}
