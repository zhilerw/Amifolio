[CmdletBinding()]
param([Parameter(Mandatory)][string]$NativeSummary)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $package = Join-Path $projectRoot 'artifacts/Amifolio'
    $summary = Get-Content -LiteralPath $NativeSummary -Raw | ConvertFrom-Json
    $desktop = Join-Path $package 'desktop/Amifolio.dll'
    if ((Get-FileHash -LiteralPath $desktop -Algorithm SHA256).Hash -ne $summary.desktopSha256) { throw '原生测试结果与待打包程序不一致。' }
    if (@($summary.binaries).Count -eq 0) { throw '原生测试缺少二进制清单，请重新运行 Test-Native.ps1。' }
    $currentBinaries = @(Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object Extension -in @('.exe', '.dll', '.pri', '.xbf'))
    if ($currentBinaries.Count -ne $summary.binaries.Count) { throw '待打包二进制文件数已变化。' }
    foreach ($binary in $summary.binaries) {
        $path = [IO.Path]::GetFullPath($binary.path, $package)
        if (-not $path.StartsWith($package + '\', [StringComparison]::OrdinalIgnoreCase) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $binary.sha256) { throw "已测二进制发生变化：$($binary.path)" }
    }
    $modes = @($summary.scenarios.mode | Sort-Object)
    if (($modes -join ',') -ne 'automation,capacity,collection,configuration,configuration-reopen,edit,empty,learning,motion,settings,startup,startup-reopen,tasks' -or @($summary.scenarios | Where-Object { $_.exitCode -ne 0 -or $_.pages -ne 8 }).Count -ne 0) { throw '原生测试未完整通过。' }
    $expectedChecks = @{ empty = 0; edit = 22; motion = 28; configuration = 7; 'configuration-reopen' = 5; tasks = 16; collection = 84; automation = 31; startup = 21; 'startup-reopen' = 6; learning = 27; settings = 25; capacity = 13 }
    foreach ($scenario in $summary.scenarios) {
        if ($scenario.checks -ne $expectedChecks[$scenario.mode]) { throw "原生检查数量不符：$($scenario.mode)" }
    }
    $version = ([xml](Get-Content src/Amifolio.Desktop/Amifolio.Desktop.csproj -Raw)).Project.PropertyGroup.Version
    if ((Get-Item -LiteralPath $desktop).VersionInfo.ProductVersion.Split('+')[0] -ne $version) { throw '发布程序版本与源码不同。' }
    foreach ($name in @('desktop/Amifolio.exe', 'desktop/Amifolio.pri', 'desktop/App.xbf', 'desktop/MainWindow.xbf', 'desktop/LearningView.xbf', 'bridge/Amifolio.Bridge.exe', 'worker/Amifolio.Worker.exe', 'worker/Amifolio.Worker.dll', 'worker/Amifolio.Worker.runtimeconfig.json', 'codex/README.md', 'codex/skills/amifolio/SKILL.md', 'codex/Amifolio-Codex-skills.zip', 'docs/COLLECTION.md', 'README.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $package $name) -PathType Leaf)) { throw "发布文件缺失：$name" }
    }
    $dirty = @(git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) { throw '请在干净的发行提交上打包。' }
    $source = @(git -c core.quotepath=false ls-tree -r --name-only HEAD | Sort-Object -Unique)
    if ($LASTEXITCODE -ne 0 -or $source.Count -eq 0) { throw '源码打包需要发行提交。' }
    function Assert-PackageFile([string]$Name, [string]$FullPath) {
        if ($Name -match '(^|/)(\.git|\.local|bin|obj|artifacts|backups|publish|node_modules)(/|$)' -or
            $Name -match '(?i)(^|/)(\.env(?!\.example$)[^/]*|credentials(?:\.[^/]*)?|secrets(?:\.[^/]*)?|secret\.json|id_(?:rsa|dsa|ecdsa|ed25519)|\.npmrc|\.pypirc|\.netrc)$' -or
            $Name -match '(?i)\.(lnk|bak|pfx|snk|pem|key|p12|p8|keystore|jks|kdbx|db|sqlite|db-wal|db-shm|sqlite-wal|sqlite-shm|zip)$') { throw "打包中出现本机文件或敏感文件：$Name" }
        if ($Name -match '^docs/(superpowers|design|releases|environment|research)/|^docs/(PUBLIC-READINESS|GITHUB|2026-).*\.md$|^tests/[^/]+/evidence/') { throw "不能打包内部过程材料：$Name" }
        if ((Get-Item -LiteralPath $FullPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "不能打包链接：$Name" }
    }
    function Write-VerifiedZip([string]$Path, [string]$Root, [string[]]$Files, [bool]$SourceArchive) {
        $temp = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        $inventory = @()
        try {
            $zip = [IO.Compression.ZipFile]::Open($temp, [IO.Compression.ZipArchiveMode]::Create)
            try {
                foreach ($name in $Files) {
                    $full = [IO.Path]::GetFullPath($name, $Root)
                    if (-not $full.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'ZIP 路径越界。' }
                    if ($SourceArchive) { Assert-PackageFile $name $full }
                    $item = Get-Item -LiteralPath $full -Force
                    if ($item.Length -ge 100000000) { throw "单个发布文件过大：$name" }
                    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $full, $name.Replace('\', '/'), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
                    $inventory += [ordered]@{ path = $name.Replace('\', '/'); bytes = $item.Length; sha256 = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash }
                }
                $entry = $zip.CreateEntry('PACKAGE-MANIFEST.json')
                $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
                try { $writer.Write(([ordered]@{ product = 'Amifolio'; version = $version; files = $inventory } | ConvertTo-Json -Depth 7)) } finally { $writer.Dispose() }
            } finally { $zip.Dispose() }
            $zip = [IO.Compression.ZipFile]::OpenRead($temp)
            try {
                if ($zip.Entries.Count -ne $inventory.Count + 1) { throw 'ZIP 条目数不符。' }
                foreach ($item in $inventory) {
                    $entry = $zip.GetEntry($item.path)
                    if ($null -eq $entry -or $entry.Length -ne $item.bytes) { throw "ZIP 条目无效：$($item.path)" }
                    $stream = $entry.Open()
                    try { $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
                    if ($digest -ne $item.sha256) { throw "ZIP 校验失败：$($item.path)" }
                }
            } finally { $zip.Dispose() }
            Move-Item -LiteralPath $temp -Destination $Path -Force
            ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash + '  ' + [IO.Path]::GetFileName($Path)) | Set-Content -LiteralPath ($Path + '.sha256.txt') -Encoding ascii
            Write-Host "PASS package $([IO.Path]::GetFileName($Path)): $($inventory.Count) verified files"
        } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp } }
    }
    # Portable PDB compilation options can retain the original PathMap input; keep symbols local.
    $runtimeFiles = @(Get-ChildItem -LiteralPath $package -Recurse -File -Force | Where-Object Extension -ne '.pdb' | ForEach-Object { [IO.Path]::GetRelativePath($package, $_.FullName).Replace('\', '/') })
    if (@($runtimeFiles | Where-Object { $_ -match '^docs/(superpowers|design|releases|environment|research)/|^docs/(PUBLIC-READINESS|GITHUB|2026-).*\.md$' }).Count) { throw '运行目录中有旧版过程材料，请从干净目录构建。' }
    Write-VerifiedZip (Join-Path $projectRoot "artifacts/Amifolio-$version-win-x64.zip") $package $runtimeFiles $false
    Write-VerifiedZip (Join-Path $projectRoot "artifacts/Amifolio-$version-source.zip") $projectRoot $source $true
} finally { Pop-Location }
