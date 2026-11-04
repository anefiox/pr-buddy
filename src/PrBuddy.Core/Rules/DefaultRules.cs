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
    /// <param name="registry">For --online package checks; null keeps everything offline.</param>
    public static IReadOnlyList<IRule> Create(PrBuddyConfig? config = null, Packages.IPackageRegistry? registry = null)
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
        new RethrowResetsStackRule(),
        new HardcodedSecretRule(),
        new DebuggingLeftoversRule(),
        new ThreadSleepRule(),

        // Tests (PRB1xx)
        new MissingTestsRule(),
        new WeakenedTestsRule(),
        new SkippedTestRule(),
        new TestWithoutAssertionsRule(),

        // Config and dependencies (PRB2xx)
        new BuildConfigChangedRule(),
        new NewPackageRule(registry),
        new WeakenedChecksRule(),

        // Structure, using the whole repo (PRB3xx)
        new UnusedNewCodeRule(),
        new DuplicateCodeRule(
            config.GetShare("PRB302", "similarity", DuplicateCodeRule.DefaultSimilarity),
            config.GetShare("PRB302", "copySimilarity", DuplicateCodeRule.DefaultCopySimilarity),
            config.GetInt("PRB302", "minTokens", DuplicateCodeRule.DefaultMinTokens)),
        new ScopeCreepRule(config.GetShare("PRB303", "focusShare", ScopeCreepRule.DefaultFocusShare)),
        new ComplexNewMethodRule(config.GetInt("PRB013", "threshold", ComplexNewMethodRule.DefaultThreshold)),
        ];
    }
}
