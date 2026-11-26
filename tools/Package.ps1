#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$NativeSummary,
    [string]$PackageRoot = 'artifacts/Amifolio',
    [string]$OutputRoot,
    [switch]$IncludeSource
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'ReleaseLayout.ps1')

function Assert-NativeSummary($Summary, $RuntimeInventory, [string]$Version) {
    if (-not $Summary.version -or $Summary.version.Split('+')[0] -cne $Version) {
        throw '原生测试结果版本与候选版本不一致。'
    }
    Assert-CandidatePayloadMatch $Summary.payload $RuntimeInventory

    # Test-Native records 22 edit checks plus seven AGENTS recovery checks.
    $expectedChecks = @{
        empty = 0; edit = 29; motion = 28; configuration = 7; 'configuration-reopen' = 5
        tasks = 16; collection = 84; automation = 31; startup = 21; 'startup-reopen' = 6
        learning = 27; settings = 25; capacity = 13; m3 = 18; 'm3-reopen' = 12; m4 = 8
    }
    foreach ($scenario in $Summary.scenarios) {
        if (-not $scenario.mode -or -not $expectedChecks.ContainsKey($scenario.mode) -or
            $null -eq $scenario.exitCode -or $scenario.exitCode -ne 0 -or $scenario.pages -ne 8 -or
            $null -eq $scenario.checks -or $scenario.checks -ne $expectedChecks[$scenario.mode]) {
            throw "原生场景失败、重复或检查数量不符：$($scenario.mode)"
        }
        $expectedChecks.Remove($scenario.mode)
    }
    if ($expectedChecks.Count -ne 0) { throw "原生证据缺少场景：$($expectedChecks.Keys -join ', ')" }
}

function Assert-ZipEntry($Entry, $File) {
    if ($null -eq $Entry -or $Entry.Length -ne $File.bytes) { throw "ZIP 条目无效：$($File.path)" }
    $stream = $Entry.Open()
    try { $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
    if ($digest -ne $File.sha256) { throw "ZIP 校验失败：$($File.path)" }
}

function Assert-SkillArchive([string]$Root) {
    $skillsRoot = Join-Path $Root 'codex/skills'
    $files = @(Get-ChildItem -LiteralPath (Join-Path $skillsRoot 'amifolio') -Recurse -File -Force |
        ForEach-Object { [IO.Path]::GetRelativePath($skillsRoot, $_.FullName).Replace('\', '/') })
    $inventory = @(Get-PackageInventory $skillsRoot $files)
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $Root 'codex/Amifolio-Codex-skills.zip'))
    try {
        if (@($archive.Entries | Where-Object Name).Count -ne $inventory.Count) { throw '内置 skill ZIP 条目数不符。' }
        foreach ($file in $inventory) { Assert-ZipEntry ($archive.GetEntry($file.path)) $file }
    } finally { $archive.Dispose() }
}

function Write-VerifiedZip([string]$Path, [string]$Root, $Manifest) {
    $manifestJson = $Manifest | ConvertTo-Json -Depth 8
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $Manifest.files) {
            $fullPath = [IO.Path]::GetFullPath($file.path, $Root)
            Assert-OrdinaryReleasePath $Root $fullPath
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $fullPath, $file.path, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
        $writer = [IO.StreamWriter]::new($zip.CreateEntry('PACKAGE-MANIFEST.json').Open(), [Text.UTF8Encoding]::new($false))
        try { $writer.Write($manifestJson) } finally { $writer.Dispose() }
    } finally { $zip.Dispose() }

    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        if ($zip.Entries.Count -ne $Manifest.files.Count + 1) { throw 'ZIP 条目数不符。' }
        $reader = [IO.StreamReader]::new($zip.GetEntry('PACKAGE-MANIFEST.json').Open())
        try {
            if ($reader.ReadToEnd() -cne $manifestJson) { throw 'ZIP 清单内容不符。' }
        } finally { $reader.Dispose() }
        foreach ($file in $Manifest.files) { Assert-ZipEntry ($zip.GetEntry($file.path)) $file }
    } finally { $zip.Dispose() }
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    "$hash  $([IO.Path]::GetFileName($Path))" | Set-Content -LiteralPath "$Path.sha256.txt" -Encoding ascii
    Write-Host "PASS package $([IO.Path]::GetFileName($Path)): $($Manifest.files.Count) verified files"
}

$openFiles = [Collections.Generic.List[IO.FileStream]]::new()
Push-Location $projectRoot
try {
    $version = Get-ReleaseVersion $projectRoot
    $package = Get-ReleaseOutputPath $projectRoot $PackageRoot
    $candidateId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    if (-not $OutputRoot) { $OutputRoot = ".local/release-candidates/Amifolio-$version-$candidateId" }
    $output = Get-ReleaseOutputPath $projectRoot $OutputRoot
    Assert-OutsideCandidate $package $output
    if (Test-Path -LiteralPath $output) { throw "候选输出目录已存在：$output" }

    $runtimeInventory = @(Get-CandidatePayloadInventory $package -OpenFiles $openFiles)
    $summaryBytes = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($NativeSummary, $projectRoot))
    $summary = [Text.Encoding]::UTF8.GetString($summaryBytes).TrimStart([char]0xfeff) | ConvertFrom-Json
    Assert-NativeSummary $summary $runtimeInventory $version
    Assert-ReleaseLayout $projectRoot $package $version
    Assert-SkillArchive $package
    $runtimeManifest = [ordered]@{
        product = 'Amifolio'
        version = $version
        kind = 'windows-x64'
        nativeSummarySha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($summaryBytes))
        files = $runtimeInventory
    }

    if ($IncludeSource) {
        $sourceFiles = @(git -c core.quotepath=false ls-files --cached --others --exclude-standard |
            Sort-Object -Unique | Where-Object { Test-ReleaseDocument $_ })
        if ($LASTEXITCODE -ne 0 -or $sourceFiles.Count -eq 0) { throw '源码打包需要在 Git 工作区内运行。' }
        $sourceInventory = @(Get-PackageInventory $projectRoot $sourceFiles)
    }

    $staging = Get-ReleaseOutputPath $projectRoot "$output.pack-$candidateId"
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    try {
        Write-VerifiedZip (Join-Path $staging "Amifolio-$version-win-x64.zip") $package $runtimeManifest
        if ($IncludeSource) {
            Write-VerifiedZip (Join-Path $staging "Amifolio-$version-source.zip") $projectRoot ([ordered]@{
                product = 'Amifolio'; version = $version; kind = 'source'; files = $sourceInventory
            })
        }
        Assert-CandidatePayloadMatch $runtimeInventory @(Get-CandidatePayloadInventory $package)
        [IO.Directory]::Move($staging, $output)
    } catch {
        if (Test-Path -LiteralPath $staging -PathType Container) { Write-Host "未完成打包的暂存目录：$staging" }
        throw
    }
    Write-Host "本地候选包：$output。正式发行须使用已完成独立 Windows 11 x64 验收的相同 ZIP。"
} finally {
    foreach ($file in $openFiles) { $file.Dispose() }
    Pop-Location
}
