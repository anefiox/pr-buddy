# PR Buddy

A deterministic sanity check for pull requests, with an optional agent review mode. The default review gives the same report for the same diff, and every finding says why.

It answers two questions before you start reading a PR:

1. **What actually changed?** Roslyn compares each C# member before and after, ignoring whitespace and comments. An 800-line "refactor" becomes "6 methods changed behavior, 40 were reformatted."
2. **What smells off?** Rules check only the lines the PR adds, so old debt in the codebase doesn't drown out the new problems.

## Usage

```bash
dotnet run --project src/PrBuddy.Cli -- main my-feature-branch
```

```
prbuddy [<base>] [<head>] [options]

  <base>   Branch or commit the PR targets (default: origin/main, origin/master, main or master)
  <head>   Branch or commit with the changes (default: HEAD)

  -u, --uncommitted       Check the working tree (staged, unstaged and new files) instead of a commit
      --staged            Check only staged changes against HEAD (no refs)
  -r, --repo <path>       Repository to analyze (default: current directory)
  -c, --config <path>     Config file (default: prbuddy.json as it is on the base branch)
      --json              Print the report as JSON
      --markdown          Print the report as Markdown, for a PR comment
      --sarif             Print the report as SARIF 2.1.0, for IDEs and code scanning
      --link-base <url>   Turn files and lines into links, e.g. https://github.com/o/r/blob/<sha>
      --online            Look new NuGet packages up on nuget.org
      --agent <provider>  Review with codex, claude, openrouter or local and write JSON
      --model <name>      Model to use with --agent
      --benchmark <file> Score the review against expected findings in a JSON file
      --fail-on <level>   Exit with code 1 if any finding is at least info, warning or critical
      --only <ids>        Run only these rules, comma-separated (e.g. --only PRB010,PRB001)
      --skip <ids>        Run everything except these rules
      --list-rules        Show the built-in rules
      --explain <rule>    Why a rule exists, what to do about it, and its options
```

Exit codes: `0` ok, `1` findings at or above `--fail-on`, `2` bad input or config.

Like GitHub, it diffs from the **merge base**, so commits that landed on `main` after the branch was cut aren't counted.

### Before you commit

```bash
prbuddy --uncommitted
```

This checks everything you'd commit right now (staged, unstaged and new untracked files, respecting `.gitignore`), plus whatever the branch has already committed. It's handy for checking an AI agent's work before it goes anywhere. Nothing is staged, committed or written.

To review just the changes you've staged for your next commit:

```bash
prbuddy --staged --fail-on warning
```

`--staged` compares the Git index against `HEAD`. For partially staged files, it checks the staged version. Unstaged edits, untracked files and earlier commits on your branch aren't included. Repository-wide rules also read from the index, so an unstaged caller can't hide an unused staged type. It supports every report format, rule filters and inline suppressions. The config comes from `HEAD` unless you pass `--config`. It takes no branch refs and can't be combined with `--uncommitted`; resolve merge conflicts before running it. It reads the index without changing it.

### Allowing a finding in one place

When a finding is wrong for one line but the rule is right in general, say so in a comment. **A reason is required:**

```csharp
// prbuddy-ignore PRB001: temp files; the OS cleans them up anyway
catch (IOException) { }

task.Wait(); // prbuddy-ignore PRB004: runs on a dedicated background thread
```

- A comment on its own line covers the next line of code; a comment after code covers that line.
- Several rules at once: `prbuddy-ignore PRB001, PRB002: …`. A whole file: `prbuddy-ignore-file PRB301: …`.
- It works in any file type: `<!-- prbuddy-ignore PRB202: … -->` in a `.csproj`, `# prbuddy-ignore …` in `.editorconfig`.
- Suppressed findings aren't hidden. The report lists each one with its reason, so a reviewer sees what was waved through and why. They don't count toward `--fail-on` or risk scores. In SARIF they're marked as suppressed in source, with the justification.
- A suppression with no reason, or naming a rule that doesn't exist, isn't applied and is reported as PRB900. So is a suppression the PR adds that matches nothing, as info.

### SARIF

`--sarif` writes the report as [SARIF 2.1.0](https://docs.oasis-open.org/sarif/sarif/v2.1.0/sarif-v2.1.0.html), the standard format for static analysis results. Open it with the SARIF Viewer extension in VS Code or Visual Studio to see findings next to the code. Output is checked against the official schema.

```bash
prbuddy main --sarif > prbuddy.sarif
```

### Online package checks

Even offline, PRB202 spots names one keystroke away from one of the 1,000 most-downloaded packages: an added, dropped, swapped or changed letter, a missing separator (`NewtonsoftJson`), or `0`/`1` in place of `o`/`l`. Names that differ in the first letter (`bunit`/`xunit`), only in digits (`win7-x64`/`win-x64`), or inside a short code (`.lt`/`.pt`) aren't flagged. Checked against 3,000 real packages just below the top 1,000, it flags 1 (`WebApi.Cors` looks like `WebApi.Core`). The list ships with the tool; refresh it with `tools/Update-PopularPackages.ps1`.

By default PR Buddy never touches the network. With `--online`, PRB202 looks up each *new* package on nuget.org. Packages with lots of downloads drop to info, with the count shown. Packages that aren't on nuget.org (a typo, a private feed, or a package that doesn't exist at all) or have few downloads stay as warnings that say why. Only package names are sent by `--online`. If nuget.org can't be reached, it falls back to the offline warning.

### Agent review and benchmarking

`--agent` reviews a commit range or the staged Git index and writes a JSON report. It sends changed text and any requested excerpts to the selected provider. The bundled [PR review skill](src/PrBuddy.Cli/Agents/pr-review/SKILL.md) applies to every provider. Agents can request up to four excerpts from changed files. PR Buddy checks that each finding anchors to an added or removed line and quotes that line as evidence. It skips binary content and uses the `ignore` paths from the base config or an explicit `--config` file.

| Provider | Setup |
| --- | --- |
| `codex` | Install the native Codex CLI on PATH and use `codex login` to sign in with ChatGPT. PR Buddy uses the CLI's cached sign-in. [Codex authentication](https://learn.chatgpt.com/docs/auth) |
| `claude` | Install the native Claude Code CLI on PATH and sign in through `claude auth login`. Use your own account or API credentials as supported by Claude Code. Use a version that supports `--safe-mode` and `--json-schema`. [Claude Code authentication](https://code.claude.com/docs/en/authentication) |
| `openrouter` | Set `OPENROUTER_API_KEY` and use an OpenRouter model ID. [OpenRouter API](https://openrouter.ai/docs/api/api-reference/chat/send-chat-completion-request) |
| `local` | Start an OpenAI-compatible chat completions server. `PRBUDDY_LOCAL_BASE_URL` defaults to `http://127.0.0.1:11434/v1`. Set `PRBUDDY_LOCAL_API_KEY` when the server requires a bearer key. [Ollama compatibility](https://docs.ollama.com/api/openai-compatibility) |

```bash
prbuddy main HEAD --agent codex --model <codex-model>
prbuddy main HEAD --agent claude --model <claude-model>
prbuddy main HEAD --agent openrouter --model <provider/model>
prbuddy --staged --agent local --model <local-model>
```

For a benchmark, save the known issues as a JSON array of anchors for a fixed Git range. Each anchor uses the path shown in the diff, the line number on that side, and the category from the skill.

```json
[
  { "path": "src/Auth.cs", "side": "head", "line": 42, "category": "security" }
]
```

```bash
prbuddy <base-sha> <head-sha> --agent openrouter --model <provider/model> --benchmark expected.json
```

Use the same range, expected anchors, and config across providers. Use `base` for removed lines and the new path for renamed files. The expected anchors stay local. The report records the requested model, input and skill hashes, elapsed time, and provider request count. With `--benchmark` it also reports precision, recall, and F1 by exact path, side, line, and category. Severity, explanation quality, and fix quality require human evaluation. Model outputs can vary between runs.

Agent mode supports commit ranges and `--staged`. Successful runs exit with code `0`, including reviews that find defects. Invalid input, provider errors, and invalid review responses exit with code `2`. Review packets over 96,000 JSON characters need a smaller range.

To install it as a global tool:

```bash
dotnet pack src/PrBuddy.Cli -c Release -o nupkg
dotnet tool install -g PrBuddy --add-source ./nupkg
prbuddy --version
```

The package bundles libgit2 for Windows, Linux (glibc and musl, x64 and ARM) and macOS (x64 and Apple Silicon). The full test suite passes on Windows and on Linux (Ubuntu 24.04, x64), and both produce byte-identical reports for the same history. macOS hasn't been tried yet. Numbers in reports use one fixed format, so the machine's language settings don't change the output either.

To set up a config, run this in your repository. It writes a commented `prbuddy.json` at the root, and won't overwrite an existing one without `--force`:

```bash
prbuddy init
```

### Speed

Most PRs take well under a second. On a synthetic repository with 20,000 C# files (120,000 methods), a 40-file PR takes about 3 seconds, including the whole-repo checks. Files are parsed in parallel, and the output is identical from run to run.

## Configuration

Put `prbuddy.json` in the repo root (`prbuddy init` writes a starter one). Comments and trailing commas are allowed.

```jsonc
{
  "ignore": ["**/Migrations/**", "*.Designer.cs", "docs/"],
  "failOn": "warning",
  "rules": {
    "PRB008": "off",                  // turn a rule off
    "PRB004": "info",                 // change its severity
    "PRB006": { "threshold": 5 },     // rule options (all listed below)
    "PRB101": { "enabled": false }
  }
}
```

- **`ignore`**: gitignore-style globs. `*` matches within one folder, `**` across folders. A pattern with no `/` matches at any depth.
- **`rules`**: keyed by rule id. The value is `"off"`, `"on"`, a severity, or an object with `enabled`, `severity` and rule options. A typo in a rule id is reported as an error.
- **`failOn`**: used when `--fail-on` isn't passed.

Rule options:

| Rule | Option | Default | |
|---|---|---|---|
| PRB006 | `threshold` | `3` | New `!` operators in one file before it's reported |
| PRB013 | `threshold` | `25` | Cyclomatic complexity at which a new method is reported |
| PRB302 | `similarity` | `0.8` | Report methods at least this similar |
| PRB302 | `copySimilarity` | `0.95` | Warning at or above this, info below |
| PRB302 | `minTokens` | `40` | Ignore shorter method bodies |
| PRB303 | `focusShare` | `0.7` | Share of changed code the main area needs |

**The config is read from the base branch, not from the PR.** Otherwise a PR could add `"PRB001": "off"` and pass its own check. A PR that changes `prbuddy.json` is flagged by PRB201, and the change applies once it's merged. To try a config locally before merging, use `--config prbuddy.json`.

## What the semantic diff reports

| Kind | Meaning |
|---|---|
| `behavior` | Same signature, different code inside |
| `signature` | Attributes, modifiers, return type or parameters changed |
| `added` / `removed` | Member exists in only one version |
| `renamed` | Identical body under a new name or in another type |
| `moved` | Removed from one file and added unchanged to another (formatting aside), shown on both sides with where it went or came from |
| cosmetic | Only formatting or comments changed (counted, not listed) |

Members that only moved around in the file aren't reported at all. Members moved to another file show as `moved`, and the new-code checks (unused, duplicated, complex, untested) skip them, so a "split this file up" refactor doesn't read as hundreds of lines of new code. Making a type `partial` to split it counts as cosmetic. On a real 4-method split of a 525-line file, git shows +109/−88 lines; PR Buddy shows 4 moves and no findings. Changes visible outside the assembly are tagged **public API**.

## Rules

| Id | Severity | Rule |
|---|---|---|
| PRB001 | warning | Empty catch block |
| PRB002 | warning | `catch (Exception)` that never rethrows |
| PRB003 | warning | `async void` outside event handlers |
| PRB004 | warning | `.Wait()`, `.GetAwaiter().GetResult()`, `.Result` (info) |
| PRB005 | warning | `#pragma warning disable`, `#nullable disable`, `[SuppressMessage]` |
| PRB006 | info | 3+ new null-forgiving `!` in one file (`null!` / `default!` ignored) |
| PRB007 | warning | `NotImplementedException`, placeholder strings, TODO comments (info) |
| PRB008 | info | Comment that just restates the line below it |
| PRB009 | warning | `throw ex;` in a catch, which resets the stack trace (use `throw;`) |
| PRB010 | critical | Hard-coded secret in any text file: AWS/GitHub/Slack/Stripe/Google/OpenAI/Anthropic keys, private keys, Azure account keys, JWTs, connection-string passwords (warning in tests; never printed) |
| PRB011 | warning | `Debugger.Break()` / `Debugger.Launch()` left in |
| PRB012 | info | `Thread.Sleep` in production code |
| PRB013 | info | New method with cyclomatic complexity of 25 or more (one finding per file) |
| PRB014 | warning | New synchronous cycle where a loop calls a helper that calls back into the loop's method (same type and file) |
| PRB015 | warning | Discarded cancellation registration from Register or UnsafeRegister |
| PRB016 | warning | Sibling callbacks complete the same task, but one skips a resource that another disposes |
| PRB017 | warning | A callback path calls a lifecycle-sensitive accessor without checking state that disposal can invalidate |
| PRB101 | warning | Behavior changed but no test files touched |
| PRB102 | warning | Existing test lost assertions, or was deleted |
| PRB103 | warning | Test newly skipped or ignored |
| PRB104 | warning | Test with no assertions, or only `Assert.True(true)`-style ones |
| PRB201 | info | CI, `.editorconfig`, `Directory.Build.*` or `prbuddy.json` changed |
| PRB202 | warning | New NuGet package (version bumps ignored); names one keystroke from a popular package are called out |
| PRB203 | warning | `<NoWarn>`, `<Nullable>disable`, analyzers off, rule severity set to none |
| PRB301 | warning | New private member nothing in its file uses; new internal type nothing in the repo references (info) |
| PRB302 | warning | New method that's a copy (95%+) of an existing one; 80–95% similar is info |
| PRB303 | info | Most of the PR is in one area, but a few changes land elsewhere |
| PRB900 | warning | `prbuddy-ignore` without a reason or naming an unknown rule; a new one that matches nothing (info) |

Run `prbuddy --explain <id>` for why a rule exists, what to do when it fires, and its options.

PRB301 and PRB302 look at the whole repository at the PR's head, read straight from git, not just the diff. PRB301 matches by name. It skips anything an outside caller might use: members with attributes, overrides, partial/extern members, extension holders, and Unity/Godot callbacks. PRB302 blanks out local names and literals before comparing, so renaming variables doesn't hide a copy. Test methods are skipped, since tests look like their neighbours by design.

PRB303 treats a file's first two folders as its area. It only fires when one area has at least 70% of the changed code and the rest is in one to three files. It checks the last 300 commits before the PR: areas that often changed together, or shared code that changes alongside many areas, count as partners rather than strays.

## Layout

```
src/PrBuddy.Core
  Git/GitDiffReader.cs        merge-base diff via LibGit2Sharp → ChangedFile
  Git/GitSnapshot.cs          the repo's C# files at the PR head, for repo-wide rules
  Semantics/                  member-by-member Roslyn diff
  Rules/                      IRule, AddedCodeRule base, the built-in rules
  Configuration/              prbuddy.json parsing and glob matching
  Reporting/                  shared wording and the Markdown report
  PrAnalyzer.cs               applies config, runs the rules, scores risk, orders files
src/PrBuddy.Cli               argument parsing, config loading and Spectre.Console output
tests/PrBuddy.Tests           xUnit; Pr.File(path, before, after) builds a diff without git
tests/PrBuddy.Cli.Tests       runs the real command against throwaway git repos
tools/                        refreshes the popular-package list from nuget.org
```

## Adding a rule

1. For a check on new C# code, derive from `AddedCodeRule` and only report nodes where `file.IsNew(node)` is true. For a cross-file check, derive from `RuleBase`.
2. Register it in `Rules/DefaultRules.cs`, and add its explanation to `Rules/RuleDocs.cs` (a test fails if you forget).
3. Test it with `Pr.Run(new MyRule(), Pr.File("src/A.cs", before, after))`. For rules that use `context.RepoTrees()`, `Pr.RunInRepo` takes the rest of the repo as a dictionary.

## Ideas for next steps

- GitHub Action that posts the `--markdown` report as a PR comment.
- Semantic version of PRB301 using `MSBuildWorkspace`, to find unused public members of internal types.
