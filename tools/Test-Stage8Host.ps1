[CmdletBinding()]
param(
    [string]$PackageRoot = 'artifacts/Amifolio',
    [string]$ResultsRoot = '.local/stage8/host',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$package = [IO.Path]::GetFullPath($PackageRoot, $repo)
$results = Join-Path ([IO.Path]::GetFullPath($ResultsRoot, $repo)) ([Guid]::NewGuid().ToString('N'))
$runner = Join-Path $repo "tests/Amifolio.Context.Tests/bin/$Configuration/net10.0/Amifolio.Context.Tests.dll"
foreach ($required in @($runner, (Join-Path $package 'bridge/Amifolio.Bridge.exe'), (Join-Path $package 'codex/skills/amifolio/SKILL.md'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw 'Build the context test runner and package first. This opt-in harness does not build or publish.'
    }
}
# No global environment changes, authentication, formal config reads, or raw output log.
# The C# harness uses Get-Command codex in a sanitized child and requires codex-cli 0.163.0.
Write-Host 'Checking the installed Codex host with an isolated deterministic local provider (no paid service).'
& dotnet $runner --stage8-host $package $results
$code = $LASTEXITCODE
if (-not (Test-Path -LiteralPath (Join-Path $results 'report.json') -PathType Leaf)) {
    throw 'The host report is missing. Register --stage8-host in the test entrypoint and rebuild before invoking this script.'
}
Write-Host "Sanitized host acceptance report: $(Join-Path $results 'report.json')"
exit $code
