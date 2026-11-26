#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$UseExistingLocalProxy,
    [switch]$SkipTests,
    [string]$PackageRoot = 'artifacts/Amifolio'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'ReleaseLayout.ps1')
$outputRoot = Get-ReleaseOutputPath $projectRoot $PackageRoot
$defaultRoot = Join-Path $projectRoot 'artifacts\Amifolio'
$buildId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$staging = Get-ReleaseOutputPath $projectRoot "$outputRoot.build-$buildId"
if ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw '请使用 Windows x64 版 PowerShell 7 构建 Windows x64 运行包。'
}
if ((Test-Path -LiteralPath $outputRoot) -and
    ($outputRoot -ne $defaultRoot -or -not (Test-Path -LiteralPath $outputRoot -PathType Container))) {
    throw "运行目录已存在，请为本次候选指定新目录：$outputRoot"
}
$version = Get-ReleaseVersion $projectRoot
Push-Location $projectRoot
try {
    . .\tools\environment\Enter-DevEnvironment.ps1 -UseExistingLocalProxy:$UseExistingLocalProxy
    # Lock the test projects too; implicit restore during dotnet run could rewrite their locks.
    & $dotnetExecutable restore Amifolio.slnx --locked-mode --disable-parallel --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw '解决方案锁定依赖还原失败' }
    & $dotnetExecutable build Amifolio.slnx -c Release --no-restore --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw '解决方案编译失败' }
    if (-not $SkipTests) {
        foreach ($testProject in @('tests/Amifolio.Core.Tests/Amifolio.Core.Tests.csproj', 'tests/Amifolio.Context.Tests/Amifolio.Context.Tests.csproj', 'tests/Amifolio.Bridge.Tests/Amifolio.Bridge.Tests.csproj', 'tests/Amifolio.Desktop.Logic.Tests/Amifolio.Desktop.Logic.Tests.csproj')) {
            & $dotnetExecutable run --project $testProject -c Release --no-build --no-restore
            if ($LASTEXITCODE -ne 0) { throw "验证未通过：$testProject" }
        }
    }
    New-Item -ItemType Directory -Path $staging | Out-Null
    & $dotnetExecutable publish src/Amifolio.Desktop/Amifolio.Desktop.csproj -c Release --no-restore --self-contained false -o (Join-Path $staging 'desktop')
    if ($LASTEXITCODE -ne 0) { throw '桌面程序发布失败' }
    # Native apphost follows this Windows SDK host. An ad-hoc RID here would rewrite
    # shared library lock files and break the next ordinary locked restore.
    & $dotnetExecutable publish src/Amifolio.Bridge/Amifolio.Bridge.csproj -c Release --no-restore --self-contained false -o (Join-Path $staging 'bridge')
    if ($LASTEXITCODE -ne 0) { throw '桥接程序发布失败' }
    & $dotnetExecutable publish src/Amifolio.Worker/Amifolio.Worker.csproj -c Release --no-restore --self-contained false -o (Join-Path $staging 'worker')
    if ($LASTEXITCODE -ne 0) { throw '解析组件发布失败' }
    $codexOutput = Join-Path $staging 'codex'
    New-Item -ItemType Directory -Path $codexOutput | Out-Null
    foreach ($item in @('README.md', 'Install-Skill.ps1', 'mcp-config.example.toml', 'skills')) {
        Copy-Item -LiteralPath (Join-Path 'plugins/codex' $item) -Destination $codexOutput -Recurse
    }
    & (Join-Path $PSScriptRoot 'New-CodexPluginPackage.ps1') -BridgeRoot (Join-Path $staging 'bridge') -MarketplaceRoot (Join-Path $codexOutput 'marketplace')
    [IO.Compression.ZipFile]::CreateFromDirectory(
        (Join-Path $projectRoot 'plugins/codex/skills/amifolio'), (Join-Path $codexOutput 'Amifolio-Codex-skills.zip'),
        [IO.Compression.CompressionLevel]::Fastest, $true)
    Copy-Item -LiteralPath 'tools/Package-README.md' -Destination (Join-Path $staging 'README.md')
    Copy-Item -LiteralPath 'THIRD_PARTY_NOTICES.md' -Destination $staging
    $docs = Join-Path $staging 'docs'
    $releaseDocs = Join-Path $docs 'releases'
    New-Item -ItemType Directory -Path $releaseDocs -Force | Out-Null
    foreach ($file in @('RELEASE.md', 'STORAGE-RECOVERY.md', 'TASKS.md', 'COLLECTION.md', 'FORMATS.md', 'CONTEXT.md', 'third-party')) {
        Copy-Item -LiteralPath (Join-Path 'docs' $file) -Destination $docs -Recurse
    }
    foreach ($record in @(Get-ChildItem -LiteralPath 'docs/releases' -Filter '*.md' -File)) {
        Copy-Item -LiteralPath $record.FullName -Destination $releaseDocs
    }
    Assert-ReleaseLayout $projectRoot $staging $version

    $previous = $null
    if ($outputRoot -eq $defaultRoot -and (Test-Path -LiteralPath $outputRoot)) {
        Assert-OrdinaryReleasePath $projectRoot $outputRoot
        $previous = Get-ReleaseOutputPath $projectRoot "$outputRoot.previous-$buildId"
        [IO.Directory]::Move($outputRoot, $previous)
    }
    try {
        [IO.Directory]::Move($staging, $outputRoot)
    } catch {
        if ($previous -and -not (Test-Path -LiteralPath $outputRoot)) { [IO.Directory]::Move($previous, $outputRoot) }
        throw
    }
    if ($previous) { Write-Host "旧运行目录已保留：$previous" }
    Write-Host "Amifolio $version 可运行目录：$outputRoot；双击 desktop\Amifolio.exe 启动。"
} catch {
    if (Test-Path -LiteralPath $staging -PathType Container) { Write-Host "未完成构建的暂存目录：$staging" }
    throw
} finally { Pop-Location }
