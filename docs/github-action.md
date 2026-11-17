## GitHub Action

Add `.github/workflows/prbuddy.yml` to your repo:

```yaml
name: PR Buddy
on: pull_request
permissions:
  contents: read
  pull-requests: write
jobs:
  prbuddy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0 # needs history to find the merge base
      - uses: anefiox/pr-buddy@master
        with:
          fail-on: warning # optional; leave out to only report
```

It posts the report as a PR comment and updates that same comment on every push. The report also goes into the job summary, which is the only place it appears on PRs from forks, because their token can't comment.

| Input | Default | |
|---|---|---|
| `fail-on` | none | Fail the job at this severity (or use `failOn` in the config) |
| `comment` | `true` | Post or update the PR comment |
| `config` | base branch's `prbuddy.json` | Path to a config file instead |
| `github-token` | `github.token` | Needs `pull-requests: write` |
