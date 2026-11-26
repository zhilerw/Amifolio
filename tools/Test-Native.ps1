[CmdletBinding()]
param(
    [string]$PackageRoot = 'artifacts/Amifolio',
    [string]$ResultsRoot = '.local/native-tests',
    [ValidateRange(10, 300)][int]$TimeoutSeconds = 90,
    [ValidateSet('empty','edit','motion','configuration','configuration-reopen','tasks','collection','automation','startup','startup-reopen','learning','settings','capacity','m3','m3-reopen','m4')]
    [string[]]$Modes=@('empty','edit','motion','configuration','configuration-reopen','tasks','collection','automation','startup','startup-reopen','learning','settings','capacity','m3','m3-reopen','m4')
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'ReleaseLayout.ps1')
$package = Get-ReleaseOutputPath $projectRoot $PackageRoot
$results = Get-ReleaseOutputPath $projectRoot $ResultsRoot
Assert-OutsideCandidate $package $results
$executable = Join-Path $package 'desktop/Amifolio.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw '请先运行 tools/Build.ps1。' }
# Every run owns a fresh root. Even the edit scenario only changes its own smoke-AGENTS.md.
$run = Join-Path $results ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$openFiles = [Collections.Generic.List[IO.FileStream]]::new()
try {
    # Keep the tested bytes in place on Windows while scenarios open their own read handles.
    $payload = @(Get-CandidatePayloadInventory $package -OpenFiles $openFiles)
    $binary = Get-Item -LiteralPath (Join-Path $package 'desktop/Amifolio.dll')
    $version = $binary.VersionInfo.ProductVersion

    $observations = @()
    foreach ($mode in $Modes) {
        $scenario = Join-Path $run $mode
        New-Item -ItemType Directory -Path $scenario | Out-Null
        $report = Join-Path $scenario 'report.json'
        $start = [Diagnostics.ProcessStartInfo]::new($executable)
        $start.WorkingDirectory = Split-Path $executable -Parent
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $scenarioData = if ($mode -eq 'configuration-reopen') { Join-Path $run 'configuration/data' } elseif ($mode -eq 'startup-reopen') { Join-Path $run 'startup/data' } elseif ($mode -eq 'm3-reopen') { Join-Path $run 'm3/data' } else { Join-Path $scenario 'data' }
        foreach ($argument in @('--data-root', $scenarioData, '--smoke-report', $report, '--smoke-mode', $mode)) {
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
            $pages = @('library', 'projects', 'tasks', 'learning', 'help', 'agents', 'connections', 'settings')
            if ($result.status -ne 'native-pages-loaded' -or -not $result.uiThread -or $result.pages.Count -ne $pages.Count) { throw "页面报告无效：$mode" }
            foreach ($page in $pages) {
                $found = @($result.pages | Where-Object page -eq $page)
                if ($found.Count -ne 1 -or -not $found[0].visible -or $found[0].width -le 0 -or $found[0].height -le 0) { throw "页面布局无效：$mode/$page" }
                $image = if ($page -eq 'library') { $report + '.png' } else { $report + '.' + $page + '.png' }
                if (-not (Test-Path -LiteralPath $image -PathType Leaf) -or (Get-Item -LiteralPath $image).Length -eq 0) { throw "截图缺失：$mode/$page" }
            }
            if ($mode -eq 'empty' -and $result.artifacts -ne 0) { throw '空库测试出现非测试数据。' }
            if ($mode -eq 'edit' -and ($result.editRegression.checks.Count -ne 22 -or $result.editRegression.agentsRecoveryChecks.Count -ne 7)) { throw '编辑检查未完整执行。' }
            if ($mode -eq 'motion' -and ($result.motionRegression.checks.Count -ne 28 -or $result.motionRegression.stableIdle.activeTransitions -ne 0)) { throw '动效检查未完整执行或动画未回收。' }
            if ($mode -eq 'configuration' -and $result.configurationRegression.checks.Count -ne 7) { throw '配置保存检查未完整执行。' }
            if ($mode -eq 'configuration-reopen' -and $result.configurationRegression.checks.Count -ne 5) { throw '跨进程配置读取检查未完整执行。' }
            if ($mode -eq 'tasks' -and $result.taskRegression.checks.Count -ne 16) { throw '任务检查未完整执行。' }
            if ($mode -eq 'collection' -and $result.collectionRegression.checks.Count -ne 84) { throw '只读目录与接入检查未完整执行。' }
            if ($mode -eq 'automation' -and ($result.automationRegression.checks.Count -ne 18 -or $result.contextRaceRegression.checks.Count -ne 13)) { throw '自动处理与偏好检查未完整执行。' }
            if ($mode -eq 'startup' -and $result.startupRegression.checks.Count -ne 21) { throw '启动后台检查未完整执行。' }
            if ($mode -eq 'startup-reopen' -and $result.startupRegression.checks.Count -ne 6) { throw '目录重启检查未完整执行。' }
            if ($mode -eq 'learning' -and $result.learningRegression.checks.Count -ne 27) { throw 'Learning UI regression incomplete.' }
            if ($mode -eq 'settings' -and $result.settingsRegression.checks.Count -ne 25) { throw 'Settings regression incomplete.' }
            if ($mode -eq 'capacity' -and $result.capacityRegression.checks.Count -ne 13) { throw 'Capacity UI regression incomplete.' }
            if (($mode -eq 'm3' -and $result.previewRegression.checks.Count -ne 18) -or
                ($mode -eq 'm3-reopen' -and $result.previewRegression.checks.Count -ne 12)) { throw "M3 preview regression incomplete: $mode" }
            if ($mode -in 'm3','m3-reopen' -and ($result.creativeStartup.WindowReady.initialPage -ne 'creative' -or
                $result.creativeStartup.Preview.stage -ne 'startup.creative.interactive' -or
                $result.creativeStartup.Preview.elapsedMilliseconds -le 0)) { throw "Creative startup readiness was not observed: $mode" }
            if ($mode -eq 'm4' -and ($result.nativeMemoryRegression.checks.Count -ne 8 -or -not (Test-Path -LiteralPath ($report + '.native-memory.png') -PathType Leaf))) { throw 'M4 memory browser regression incomplete.' }
            if ($mode -eq 'product' -and $result.productRegression.checks.Count -ne 13) { throw 'Product follow-up regression incomplete.' }
            $checks = if ($mode -eq 'product') { 13 } elseif ($mode -eq 'm4') { 8 } elseif ($mode -in 'm3','m3-reopen') { $result.previewRegression.checks.Count } elseif ($mode -eq 'edit') { 29 } elseif ($mode -eq 'motion') { 28 } elseif ($mode -eq 'configuration') { 7 } elseif ($mode -eq 'configuration-reopen') { 5 } elseif ($mode -eq 'tasks') { 16 } elseif ($mode -eq 'collection') { 84 } elseif ($mode -eq 'automation') { 31 } elseif ($mode -eq 'startup') { 21 } elseif ($mode -eq 'startup-reopen') { 6 } elseif ($mode -eq 'learning') { 27 } elseif ($mode -eq 'settings') { 25 } elseif ($mode -eq 'capacity') { 13 } else { 0 }
            $observations += [ordered]@{ mode = $mode; exitCode = $process.ExitCode; pages = $result.pages.Count; checks = $checks; report = [IO.Path]::GetRelativePath($run, $report).Replace('\', '/') }
            Write-Host "PASS native $mode : 8 pages, $checks regression checks"
        } finally { $process.Dispose() }
    }

    Assert-CandidatePayloadMatch $payload @(Get-CandidatePayloadInventory $package)
    [ordered]@{
        verifiedAt = [DateTimeOffset]::Now.ToString('o')
        version = $version
        payload = $payload
        scenarios = $observations
        limitations = 'Local native XAML/compositor checks; not physical mouse/file-picker, live Codex, paid-provider or cross-machine acceptance.'
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run 'summary.json') -Encoding utf8
    Write-Host "原生测试记录：$run"
} finally {
    foreach ($file in $openFiles) { $file.Dispose() }
}
