[CmdletBinding()]
param(
    [string]$PackageRoot = 'artifacts/Amifolio',
    [string]$ResultsRoot = '.local/native-tests',
    [ValidateRange(10, 300)][int]$TimeoutSeconds = 90
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$package = [IO.Path]::GetFullPath($PackageRoot, $projectRoot)
$results = [IO.Path]::GetFullPath($ResultsRoot, $projectRoot)
$executable = Join-Path $package 'desktop/Amifolio.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw '请先运行 tools/Build.ps1。' }
# Every run owns a fresh root. Even the edit scenario only changes its own smoke-AGENTS.md.
$run = Join-Path $results ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$observations = @()
foreach ($mode in @('empty', 'edit', 'motion')) {
    $scenario = Join-Path $run $mode
    New-Item -ItemType Directory -Path $scenario | Out-Null
    $report = Join-Path $scenario 'report.json'
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.WorkingDirectory = Split-Path $executable -Parent
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in @('--data-root', (Join-Path $scenario 'data'), '--smoke-report', $report, '--smoke-mode', $mode)) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            throw "原生测试超时：$mode"
        }
        if ($process.ExitCode -ne 0 -or (Test-Path -LiteralPath ($report + '.error.txt')) -or -not (Test-Path -LiteralPath $report)) {
            throw "原生测试失败：$mode；请查看 $scenario"
        }
        $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        $pages = @('library', 'projects', 'context', 'help', 'agents', 'connections', 'settings')
        if ($result.status -ne 'native-pages-loaded' -or -not $result.uiThread -or $result.pages.Count -ne $pages.Count) { throw "页面报告无效：$mode" }
        foreach ($page in $pages) {
            $found = @($result.pages | Where-Object page -eq $page)
            if ($found.Count -ne 1 -or -not $found[0].visible -or $found[0].width -le 0 -or $found[0].height -le 0) { throw "页面布局无效：$mode/$page" }
            $image = if ($page -eq 'library') { $report + '.png' } else { $report + '.' + $page + '.png' }
            if (-not (Test-Path -LiteralPath $image -PathType Leaf) -or (Get-Item -LiteralPath $image).Length -eq 0) { throw "截图缺失：$mode/$page" }
        }
        if ($mode -eq 'empty' -and $result.artifacts -ne 0) { throw '空库测试出现非测试数据。' }
        if ($mode -eq 'edit' -and $result.editRegression.checks.Count -ne 22) { throw '编辑检查未完整执行。' }
        if ($mode -eq 'motion' -and ($result.motionRegression.checks.Count -ne 28 -or $result.motionRegression.stableIdle.activeTransitions -ne 0)) { throw '动效检查未完整执行或动画未回收。' }
        $checks = if ($mode -eq 'edit') { 22 } elseif ($mode -eq 'motion') { 28 } else { 0 }
        $observations += [ordered]@{ mode = $mode; exitCode = $process.ExitCode; pages = $result.pages.Count; checks = $checks; report = [IO.Path]::GetRelativePath($run, $report).Replace('\', '/') }
        Write-Host "PASS native $mode : 7 pages, $checks regression checks"
    } finally { $process.Dispose() }
}
$binary = Get-Item -LiteralPath (Join-Path $package 'desktop/Amifolio.dll')
[ordered]@{
    verifiedAt = [DateTimeOffset]::Now.ToString('o')
    version = $binary.VersionInfo.ProductVersion
    desktopSha256 = (Get-FileHash -LiteralPath $binary.FullName -Algorithm SHA256).Hash
    binaries = @(Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object Extension -in @('.exe', '.dll', '.pri', '.xbf') | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($package, $_.FullName).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    scenarios = $observations
    limitations = 'Local native XAML/compositor checks; not physical mouse/file-picker, live Codex, paid-provider or cross-machine acceptance.'
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run 'summary.json') -Encoding utf8
Write-Host "原生测试记录：$run"
