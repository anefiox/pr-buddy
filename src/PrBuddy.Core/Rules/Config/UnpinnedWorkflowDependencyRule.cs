using System.Text.RegularExpressions;

namespace PrBuddy.Core.Rules.Config;

/// <summary>
/// PRB204: a GitHub workflow pulls in an action or container image by a tag or branch, which its
/// publisher can move. Warning when the same file already pins other actions to a commit; info
/// otherwise.
/// </summary>
public sealed class UnpinnedWorkflowDependencyRule : RuleBase
{
    const RegexOptions Options = RegexOptions.CultureInvariant;

    static readonly Regex Uses = new(@"^\s*-?\s*uses:\s*['""]?(?<ref>[^'""\s#]+)", Options);
    static readonly Regex PinnedRef = new(@"@[0-9a-f]{40}$", Options);
    // "image: x", "container: x", "FOO_IMAGE: x" or docker://x, where x looks like [registry/]name:tag.
    static readonly Regex Image = new(
        @"^\s*-?\s*(?:[\w-]*image[\w-]*|container)\s*:\s*['""]?(?:docker://)?(?<image>[a-z0-9][\w.-]*(?:/[\w.-]+)*(?::[\w.-]+)?(?:@sha256:[0-9a-f]{64})?)['""]?\s*(?:#.*)?$",
        Options | RegexOptions.IgnoreCase);

    public override RuleDescriptor Descriptor { get; } =
        new("PRB204", "Workflow dependency not pinned to a digest", Severity.Warning, "Config");

    public override IEnumerable<Finding> Analyze(PullRequestContext context)
    {
        foreach (var file in context.Files.Where(f => IsWorkflow(f.Path)))
        {
            var allLines = (file.File.NewContent ?? "").Split('\n');
            var pinnedActions = allLines.Select(l => Uses.Match(l)).Count(m => m.Success && PinnedRef.IsMatch(m.Groups["ref"].Value));
            var severity = pinnedActions > 0 ? Severity.Warning : Severity.Info;
            var convention = pinnedActions > 0 ? " Other actions in this file are pinned to a commit." : "";

            foreach (var (line, text) in file.AddedLineTexts())
            {
                if (text.TrimStart().StartsWith('#')) continue;
                var uses = Uses.Match(text);
                if (uses.Success && uses.Groups["ref"].Value.StartsWith("docker://", StringComparison.Ordinal))
                {
                    var dockerImage = uses.Groups["ref"].Value["docker://".Length..];
                    if (!dockerImage.Contains("@sha256:", StringComparison.Ordinal))
                        yield return Report(file.Path, line,
                            $"Container image '{dockerImage}' is pulled by tag, which can be re-pushed with different contents.{convention} " +
                            "Pin it with @sha256:<digest>, and check a new publisher goes through your dependency review.", severity);
                    continue;
                }
                if (uses.Success)
                {
                    var reference = uses.Groups["ref"].Value;
                    if (reference.StartsWith("./", StringComparison.Ordinal) || PinnedRef.IsMatch(reference) ||
                        reference.Contains("@sha256:", StringComparison.Ordinal) || !reference.Contains('@'))
                        continue;
                    yield return Report(file.Path, line,
                        $"Action '{reference}' is referenced by a tag or branch, which its owner can move to different code.{convention} " +
                        "Pin it to a full commit SHA and keep the version in a comment.", severity);
                    continue;
                }

                var image = Image.Match(text);
                if (!image.Success) continue;
                var name = image.Groups["image"].Value;
                if (name.Contains("@sha256:", StringComparison.Ordinal) || !name.Contains(':') && !name.Contains('/') ||
                    name.StartsWith("${{", StringComparison.Ordinal))
                    continue;
                yield return Report(file.Path, line,
                    $"Container image '{name}' is pulled by tag, which can be re-pushed with different contents.{convention} " +
                    "Pin it with @sha256:<digest>, and check a new publisher goes through your dependency review.", severity);
            }
        }
    }

    static bool IsWorkflow(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase) &&
            (normalized.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || normalized.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)) ||
            normalized.EndsWith("/action.yml", StringComparison.OrdinalIgnoreCase) || normalized == "action.yml";
    }
}
