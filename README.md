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

  -r, --repo <path>       Repository to analyze (default: current directory)
      --json              Print the report as JSON
      --fail-on <level>   Exit with code 1 if any finding is at least info, warning or critical
      --list-rules        Show the built-in rules
```

Like GitHub, it diffs from the **merge base**, so commits that landed on `main` after the branch was cut aren't counted.

To install it as a global tool:

```bash
dotnet pack src/PrBuddy.Cli -o nupkg
dotnet tool install -g PrBuddy --add-source ./nupkg
```

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
| PRB201 | info | CI, `.editorconfig` or `Directory.Build.*` changed |
| PRB202 | warning | New NuGet package (version bumps ignored) |
| PRB203 | warning | `<NoWarn>`, `<Nullable>disable`, analyzers off, rule severity set to none |

## Layout

```
src/PrBuddy.Core
  Git/GitDiffReader.cs        merge-base diff via LibGit2Sharp → ChangedFile
  Semantics/                  member-by-member Roslyn diff
  Rules/                      IRule, AddedCodeRule base, the built-in rules
  PrAnalyzer.cs               runs the rules, scores risk, orders files
src/PrBuddy.Cli               argument parsing and Spectre.Console output
tests/PrBuddy.Tests           xUnit; Pr.File(path, before, after) builds a diff without git
```

## Adding a rule

1. For a check on new C# code, derive from `AddedCodeRule` and only report nodes where `file.IsNew(node)` is true. For a cross-file check, derive from `RuleBase`.
2. Register it in `Rules/DefaultRules.cs`.
3. Test it with `Pr.Run(new MyRule(), Pr.File("src/A.cs", before, after))`.

## Ideas for next steps

- `prbuddy.json` config: turn rules off, change severities and thresholds, set ignore paths.
- GitHub Action that posts the report as a PR comment or check run (the `--json` output is the starting point).
- Dead additions: new private or internal members with no references. Needs `MSBuildWorkspace` to load the solution.
- Duplication: new code that closely matches existing code, found by hashing normalized token sequences.
- NuGet checks for PRB202: download count, publish date, names close to popular packages.
- Scope creep: flag files far from where most of the PR's changes are.
