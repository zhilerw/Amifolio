#Requires -Version 7.2
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'ReleaseLayout.ps1')

function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    try {
        & $Action
    } catch {
        if ($_.Exception.Message -like "*$Message*") { return }
        throw
    }
    throw "Expected rejection: $Message"
}

$fixture = Get-ReleaseOutputPath $repository ('.local/package-evidence-tests/' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $fixture 'runtime'
$summaryPath = Join-Path $fixture 'summary.json'
$output = Join-Path $fixture 'zips'
$config = Join-Path $package 'desktop/Amifolio.runtimeconfig.json'
$catalog = Join-Path $package 'codex/marketplace/.agents/plugins/marketplace.json'
$diagnostic = Join-Path $package 'docs/releases/evidence-fixture/report.json'
$desktop = Join-Path $package 'desktop'
New-Item -ItemType Directory -Path $desktop, (Split-Path $catalog), (Split-Path $diagnostic) -Force | Out-Null
[IO.File]::WriteAllText($config, '{"runtime":1}')
[IO.File]::WriteAllText($catalog, '{"name":"fixture"}')
[IO.File]::WriteAllText($diagnostic, 'excluded diagnostic')
[IO.File]::WriteAllText((Join-Path $desktop 'Amifolio.pdb'), 'excluded symbols')

$openFiles = [Collections.Generic.List[IO.FileStream]]::new()
try {
    $tested = @(Get-CandidatePayloadInventory $package -OpenFiles $openFiles)
    if ($tested.Count -ne 2 -or $openFiles.Count -ne 2) { throw 'Expected the config and catalog in the payload.' }
    try {
        [IO.File]::WriteAllText($config, '{"runtime":2}')
        throw 'Expected Windows to reject a write while the candidate is held open.'
    } catch [IO.IOException] {
        Write-Host 'PASS existing payload cannot be overwritten while held open'
    }
    Assert-CandidatePayloadMatch $tested @(Get-CandidatePayloadInventory $package)
} finally {
    foreach ($file in $openFiles) { $file.Dispose() }
}

$checks = @{
    empty = 0; edit = 29; motion = 28; configuration = 7; 'configuration-reopen' = 5
    tasks = 16; collection = 84; automation = 31; startup = 21; 'startup-reopen' = 6
    learning = 27; settings = 25; capacity = 13; m3 = 16; 'm3-reopen' = 12; m4 = 8
}
$summary = [ordered]@{
    version = Get-ReleaseVersion $repository
    payload = $tested
    scenarios = @($checks.GetEnumerator() | ForEach-Object {
        @{ mode = $_.Key; exitCode = 0; pages = 8; checks = $_.Value }
    })
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding utf8
$packageCandidate = { & (Join-Path $PSScriptRoot 'Package.ps1') -PackageRoot $package -NativeSummary $summaryPath -OutputRoot $output }

[IO.File]::WriteAllText($config, '{"runtime":2}')
Assert-Rejected $packageCandidate '候选包有效载荷内容已变化：desktop/Amifolio.runtimeconfig.json'
[IO.File]::WriteAllText($config, '{"runtime":1}')
Write-Host 'PASS Package.ps1 rejects an equally sized runtimeconfig replacement'

[IO.File]::WriteAllText($catalog, '{"name":"changed"}')
Assert-Rejected $packageCandidate '候选包有效载荷内容已变化：codex/marketplace/.agents/plugins/marketplace.json'
[IO.File]::WriteAllText($catalog, '{"name":"fixture"}')
Write-Host 'PASS Package.ps1 rejects a changed plugin catalog'

$extra = Join-Path $desktop 'unlisted.json'
[IO.File]::WriteAllText($extra, '{}')
Assert-Rejected $packageCandidate '候选包有效载荷出现额外路径：desktop/unlisted.json'
[IO.File]::Delete($extra)
[IO.File]::Delete($catalog)
Assert-Rejected $packageCandidate '候选包有效载荷缺少路径：codex/marketplace/.agents/plugins/marketplace.json'
[IO.File]::WriteAllText($catalog, '{"name":"fixture"}')
Write-Host 'PASS Package.ps1 rejects added and missing payload paths'

# The unchanged fixture gets past the evidence gate and reaches release layout checks.
[IO.File]::WriteAllText($diagnostic, 'changed excluded diagnostic')
Assert-Rejected $packageCandidate '发布文件缺失或为空：desktop/Amifolio.exe'
$summary.Remove('payload')
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding utf8
Assert-Rejected $packageCandidate '原生摘要缺少完整的候选包证据'
if (Test-Path -LiteralPath $output) { throw 'A rejected candidate must not produce an output directory.' }
Write-Host 'PASS unchanged payload reaches layout validation; legacy evidence is rejected'

Assert-OrdinaryReleasePath $repository $fixture
Remove-Item -LiteralPath $fixture -Recurse
