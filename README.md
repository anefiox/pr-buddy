# PR Buddy

A deterministic sanity check for pull requests. No AI: the same diff always gives the same report, and every finding says why.

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
  -r, --repo <path>       Repository to analyze (default: current directory)
  -c, --config <path>     Config file (default: prbuddy.json as it is on the base branch)
      --json              Print the report as JSON
      --markdown          Print the report as Markdown, for a PR comment
      --link-base <url>   Turn files and lines into links, e.g. https://github.com/o/r/blob/<sha>
      --online            Look new NuGet packages up on nuget.org
      --fail-on <level>   Exit with code 1 if any finding is at least info, warning or critical
      --list-rules        Show the built-in rules
```

Exit codes: `0` ok, `1` findings at or above `--fail-on`, `2` bad input or config.

Like GitHub, it diffs from the **merge base**, so commits that landed on `main` after the branch was cut aren't counted.

### Before you commit

```bash
prbuddy --uncommitted
```

This checks everything you'd commit right now (staged, unstaged and new untracked files, respecting `.gitignore`), plus whatever the branch has already committed. It's handy for checking an AI agent's work before it goes anywhere. Nothing is staged, committed or written.

### Online package checks

Even offline, PRB202 spots names one keystroke away from one of the 1,000 most-downloaded packages: an added, dropped, swapped or changed letter, a missing separator (`NewtonsoftJson`), or `0`/`1` in place of `o`/`l`. Names that differ in the first letter (`bunit`/`xunit`), only in digits (`win7-x64`/`win-x64`), or inside a short code (`.lt`/`.pt`) aren't flagged. Checked against 3,000 real packages just below the top 1,000, it flags 1 (`WebApi.Cors` looks like `WebApi.Core`). The list ships with the tool; refresh it with `tools/Update-PopularPackages.ps1`.

By default PR Buddy never touches the network. With `--online`, PRB202 looks up each *new* package on nuget.org. Packages with lots of downloads drop to info, with the count shown. Packages that aren't on nuget.org (a typo, a private feed, or a package that doesn't exist at all) or have few downloads stay as warnings that say why. Only package names are sent. If nuget.org can't be reached, it falls back to the offline warning.

To install it as a global tool:

```bash
dotnet pack src/PrBuddy.Cli -o nupkg
dotnet tool install -g PrBuddy --add-source ./nupkg
```

## Configuration

Put `prbuddy.json` in the repo root. Comments and trailing commas are allowed.

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
| cosmetic | Only formatting or comments changed (counted, not listed) |

Members that only moved around in the file aren't reported at all. Changes visible outside the assembly are tagged **public API**.

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
2. Register it in `Rules/DefaultRules.cs`.
3. Test it with `Pr.Run(new MyRule(), Pr.File("src/A.cs", before, after))`. For rules that use `context.RepoTrees()`, `Pr.RunInRepo` takes the rest of the repo as a dictionary.

## Ideas for next steps

- GitHub Action that posts the `--markdown` report as a PR comment.
- Semantic version of PRB301 using `MSBuildWorkspace`, to find unused public members of internal types.
