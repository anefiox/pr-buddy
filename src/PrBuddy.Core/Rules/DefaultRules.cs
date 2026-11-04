using PrBuddy.Core.Configuration;
using PrBuddy.Core.Rules.Code;
using PrBuddy.Core.Rules.Config;
using PrBuddy.Core.Rules.Structure;
using PrBuddy.Core.Rules.Testing;

namespace PrBuddy.Core.Rules;

public static class DefaultRules
{
    /// <summary>
    /// Every built-in rule, with rule options taken from <paramref name="config"/>. Add new rules here.
    /// Disabled rules are still returned; <see cref="PrAnalyzer"/> skips them.
    /// </summary>
    public static IReadOnlyList<IRule> Create(PrBuddyConfig? config = null)
    {
        config ??= PrBuddyConfig.Default;
        return
        [
        // Code (PRB0xx)
        new EmptyCatchRule(),
        new SwallowedExceptionRule(),
        new AsyncVoidRule(),
        new SyncOverAsyncRule(),
        new SuppressedWarningsRule(),
        new NullForgivingRule(config.GetInt("PRB006", "threshold", NullForgivingRule.DefaultThreshold)),
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

        // Structure, using the whole repo (PRB3xx)
        new UnusedNewCodeRule(),
        new DuplicateCodeRule(),
        ];
    }
}
