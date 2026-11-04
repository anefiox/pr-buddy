using PrBuddy.Core.Rules.Code;
using PrBuddy.Core.Rules.Config;
using PrBuddy.Core.Rules.Testing;

namespace PrBuddy.Core.Rules;

public static class DefaultRules
{
    /// <summary>Every built-in rule. Add new rules here.</summary>
    public static IReadOnlyList<IRule> Create() =>
    [
        // Code (PRB0xx)
        new EmptyCatchRule(),
        new SwallowedExceptionRule(),
        new AsyncVoidRule(),
        new SyncOverAsyncRule(),
        new SuppressedWarningsRule(),
        new NullForgivingRule(),
        new PlaceholderRule(),
        new RedundantCommentRule(),

        // Tests (PRB1xx)
        new MissingTestsRule(),
        new WeakenedTestsRule(),
        new SkippedTestRule(),
        new TestWithoutAssertionsRule(),

        // Config and dependencies (PRB2xx)
        new BuildConfigChangedRule(),
        new NewPackageRule(),
        new WeakenedChecksRule(),
    ];
}
