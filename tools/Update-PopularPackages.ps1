<#
.SYNOPSIS
  Refreshes src/PrBuddy.Core/Packages/popular-packages.txt from nuget.org.

.DESCRIPTION
  PRB202 uses this list offline to spot new packages whose names are one keystroke away from a
  popular package (Newtonsoft.Jsonn, Serilog.Sinks.Consol). Run it now and then; the result is
  committed, so PR Buddy itself never needs the network for this check.
#>
param([int]$Count = 1000)

$ErrorActionPreference = 'Stop'
$index = Invoke-RestMethod 'https://api.nuget.org/v3/index.json'
$search = ($index.resources | Where-Object { $_.'@type' -like 'SearchQueryService*' } | Select-Object -First 1).'@id'

# An empty query is ranked by downloads but silently leaves some big packages out (it skipped
# Microsoft.NET.Test.Sdk and xunit.runner.visualstudio in 2026). So also search for the words
# popular package families use, and rank the union by real download counts.
$terms = @('') + @(
    'microsoft', 'system', 'test', 'xunit', 'nunit', 'mstest', 'azure', 'aws', 'google', 'amazon',
    'entityframework', 'aspnetcore', 'extensions', 'json', 'logging', 'serilog', 'http', 'sql', 'redis',
    'grpc', 'protobuf', 'identity', 'auth', 'jwt', 'swagger', 'openapi', 'mvc', 'blazor', 'maui', 'xamarin',
    'wpf', 'winforms', 'avalonia', 'unity', 'npgsql', 'mysql', 'sqlite', 'mongo', 'elastic', 'rabbit',
    'kafka', 'polly', 'automapper', 'mediatr', 'fluent', 'moq', 'nsubstitute', 'coverlet', 'benchmark',
    'analyzers', 'build', 'sdk', 'runtime', 'native', 'opentelemetry', 'hangfire', 'quartz', 'csv',
    'excel', 'pdf', 'image', 'crypto', 'bouncy', 'yaml', 'xml', 'mail', 'signalr', 'graphql', 'dapper')

$packages = @{}
foreach ($term in $terms) {
    $page = Invoke-RestMethod "$search`?q=$([uri]::EscapeDataString($term))&take=1000&prerelease=false&semVerLevel=2.0.0"
    foreach ($p in $page.data) { $packages[$p.id.ToLowerInvariant()] = [pscustomobject]@{ Id = $p.id; Downloads = [long]$p.totalDownloads } }
}

$top = $packages.Values | Sort-Object Downloads -Descending | Select-Object -First $Count
$out = Join-Path $PSScriptRoot '..\src\PrBuddy.Core\Packages\popular-packages.txt'
$lines = @("# The $Count most-downloaded packages on nuget.org, $(Get-Date -Format yyyy-MM-dd). Regenerate with tools/Update-PopularPackages.ps1.")
$lines += $top | ForEach-Object Id
[IO.File]::WriteAllLines((Resolve-Path (Split-Path $out)).Path + '\popular-packages.txt', $lines)
"Wrote $($top.Count) packages to $out"
