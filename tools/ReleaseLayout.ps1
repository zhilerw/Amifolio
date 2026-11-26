function Get-ReleaseVersion([string]$Repository) {
    $props = [xml](Get-Content -LiteralPath (Join-Path $Repository 'Directory.Build.props') -Raw)
    $version = [string]$props.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw '项目版本必须是三段式正式版本号。' }
    return $version
}

function Assert-OrdinaryReleasePath([string]$Root, [string]$Path) {
    $relative = [IO.Path]::GetRelativePath($Root, $Path)
    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '^\.\.(?:[\\/]|$)') {
        throw "发行路径越界：$Path"
    }
    $current = $Root
    foreach ($part in ($relative -split '[\\/]')) {
        $current = Join-Path $current $part
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "发行路径含链接或重解析点：$current"
        }
    }
}

function Get-ReleaseOutputPath([string]$Repository, [string]$Path) {
    $fullPath = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path, $Repository))
    $relative = [IO.Path]::GetRelativePath($Repository, $fullPath)
    if ($relative -notmatch '^(?:\.local|artifacts)[\\/][^\\/]') {
        throw '发行输出目录必须是 .local 或 artifacts 下的子目录。'
    }
    Assert-OrdinaryReleasePath $Repository $fullPath
    return $fullPath
}

function Assert-OutsideCandidate([string]$Root, [string]$Path) {
    $relative = [IO.Path]::GetRelativePath($Root, $Path)
    if (-not [IO.Path]::IsPathRooted($relative) -and $relative -notmatch '^\.\.(?:[\\/]|$)') {
        throw "输出必须位于候选运行目录之外：$Path"
    }
}

function Test-ReleaseDocument([string]$Name) {
    return ($Name -notmatch '^docs/releases/(evidence-[^/]+/|amifolio-v[^/]+\.png$)')
}

function Assert-PackageFilename([string]$Name) {
    $localPath = '(^|/)(\.git|\.local|\.codex|\.vs|bin|obj|artifacts|backups|publish|node_modules)(/|$)'
    $credentialName = '(^|/)(\.env(?!\.example$)[^/]*|auth\.json|credentials(?:\.[^/]*)?|secrets(?:\.[^/]*)?|secret\.json|id_(?:rsa|dsa|ecdsa|ed25519)|\.npmrc|\.pypirc|\.netrc)$'
    $localExtension = '\.(lnk|bak|pfx|snk|pem|key|p12|p8|keystore|jks|kdbx|db|sqlite|db-wal|db-shm|sqlite-wal|sqlite-shm|pdb|zip)$'
    if ($Name -match $localPath -or $Name -match $credentialName -or $Name -match $localExtension -or
        $Name -ieq 'PACKAGE-MANIFEST.json') {
        throw "打包中出现本机文件、敏感文件或保留名称：$Name"
    }
}

function Get-PackageInventory {
    param(
        [string]$Root,
        [string[]]$Files,
        [switch]$Runtime,
        [Collections.Generic.List[IO.FileStream]]$OpenFiles
    )
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $Files) {
        if (-not $Runtime -or $name -cne 'codex/Amifolio-Codex-skills.zip') { Assert-PackageFilename $name }
        if (-not $seen.Add($name)) { throw "ZIP 中出现重复路径：$name" }
        $fullPath = [IO.Path]::GetFullPath($name, $Root)
        Assert-OrdinaryReleasePath $Root $fullPath
        $stream = [IO.File]::Open($fullPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            $length = $stream.Length
            if ($length -ge 100000000) { throw "单个发布文件过大：$name" }
            $hash = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash
            if ($null -ne $OpenFiles) {
                $OpenFiles.Add($stream)
                $stream = $null
            }
            [ordered]@{ path = $name; bytes = $length; sha256 = $hash }
        } finally {
            if ($null -ne $stream) { $stream.Dispose() }
        }
    }
}

function Get-CandidatePayloadInventory([string]$Root, [Collections.Generic.List[IO.FileStream]]$OpenFiles) {
    $files = @(Get-ChildItem -LiteralPath $Root -Recurse -Force | ForEach-Object {
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "不能使用含链接的候选包：$($_.FullName)" }
        if (-not $_.PSIsContainer -and $_.Extension -ne '.pdb') {
            [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
        }
    } | Where-Object { Test-ReleaseDocument $_ } | Sort-Object)
    if ($files.Count -eq 0) { throw "候选运行目录没有有效载荷：$Root" }
    Get-PackageInventory -Root $Root -Files $files -Runtime -OpenFiles $OpenFiles
}

function Assert-CandidatePayloadMatch($Expected, $Actual) {
    $remaining = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($file in $Expected) {
        if (-not $file.path -or $null -eq $file.bytes -or $file.sha256 -notmatch '^[0-9A-Fa-f]{64}$' -or
            -not $remaining.TryAdd($file.path, $file)) {
            throw "候选包证据中的文件记录无效或重复：$($file.path)"
        }
    }
    if ($remaining.Count -eq 0) { throw '原生摘要缺少完整的候选包证据，请重跑 Test-Native.ps1。' }
    foreach ($file in $Actual) {
        if (-not $remaining.ContainsKey($file.path)) { throw "候选包有效载荷出现额外路径：$($file.path)" }
        $original = $remaining[$file.path]
        if ($original.bytes -ne $file.bytes -or $original.sha256 -ne $file.sha256) {
            throw "候选包有效载荷内容已变化：$($file.path)"
        }
        [void]$remaining.Remove($file.path)
    }
    if ($remaining.Count -ne 0) {
        $missing = $remaining.Keys | Sort-Object | Select-Object -First 1
        throw "候选包有效载荷缺少路径：$missing"
    }
}

function Assert-ReleaseFileMatch([string]$Expected, [string]$Actual) {
    if (-not (Test-Path -LiteralPath $Actual -PathType Leaf) -or
        (Get-FileHash -LiteralPath $Expected -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $Actual -Algorithm SHA256).Hash) {
        throw "发布文件缺失或内容与来源不同：$Actual"
    }
}

function Assert-ReleaseLayout([string]$Repository, [string]$Package, [string]$Version) {
    $market = 'codex/marketplace'
    $plugin = "$market/plugins/amifolio"
    $required = @(
        'desktop/Amifolio.exe', 'desktop/Amifolio.dll', 'desktop/Amifolio.deps.json',
        'desktop/Amifolio.runtimeconfig.json', 'desktop/Amifolio.pri',
        'desktop/App.xbf', 'desktop/MainWindow.xbf', 'desktop/LearningView.xbf',
        'bridge/Amifolio.Bridge.exe', 'bridge/Amifolio.Bridge.dll', 'bridge/Amifolio.Bridge.deps.json',
        'bridge/Amifolio.Bridge.runtimeconfig.json',
        'worker/Amifolio.Worker.exe', 'worker/Amifolio.Worker.dll', 'worker/Amifolio.Worker.deps.json',
        'worker/Amifolio.Worker.runtimeconfig.json',
        'desktop/Assets/StylePreview/index.html', 'desktop/Assets/StylePreview/preview.css',
        'desktop/Assets/StylePreview/preview.js',
        "$market/.agents/plugins/marketplace.json", "$plugin/.codex-plugin/plugin.json",
        "$plugin/.mcp.json", "$plugin/hooks/hooks.json", "$plugin/skills/amifolio/SKILL.md",
        'codex/README.md', 'codex/skills/amifolio/SKILL.md', 'codex/Amifolio-Codex-skills.zip',
        'docs/COLLECTION.md', 'THIRD_PARTY_NOTICES.md', 'README.md'
    )
    foreach ($name in $required) {
        $file = Join-Path $Package $name
        Assert-OrdinaryReleasePath $Package $file
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) {
            throw "发布文件缺失或为空：$name"
        }
    }

    $sourceAssets = Join-Path $Repository 'src/Amifolio.Desktop/Assets/StylePreview'
    $outputAssets = Join-Path $Package 'desktop/Assets/StylePreview'
    $html = Get-Content -LiteralPath (Join-Path $outputAssets 'index.html') -Raw
    if ($html -notmatch '<meta\s+http-equiv="Content-Security-Policy"\s+content="[^"]*default-src\s+''none''[^"]*">') {
        throw '风格预览页面缺少默认拒绝的 Content-Security-Policy。'
    }
    $assets = @(Get-ChildItem -LiteralPath $sourceAssets -Recurse -File -Force)
    if (@(Get-ChildItem -LiteralPath $outputAssets -Recurse -File -Force).Count -ne $assets.Count) {
        throw '发布的风格预览资源数量与源码不同。'
    }
    foreach ($asset in $assets) {
        $file = Join-Path $outputAssets ([IO.Path]::GetRelativePath($sourceAssets, $asset.FullName))
        Assert-OrdinaryReleasePath $outputAssets $file
        Assert-ReleaseFileMatch $asset.FullName $file
    }

    $pluginSource = Join-Path $Repository 'plugins/codex'
    $catalogPath = Join-Path $Package "$market/.agents/plugins/marketplace.json"
    Assert-ReleaseFileMatch (Join-Path $pluginSource 'marketplace.json') $catalogPath
    $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
    $registration = @($catalog.plugins | Where-Object name -CEQ 'amifolio')
    if ($catalog.name -cne 'amifolio-local' -or $registration.Count -ne 1 -or
        $registration[0].source.source -cne 'local' -or $registration[0].source.path -cne './plugins/amifolio') {
        throw '本地市场的 Amifolio 注册与包内路径不匹配。'
    }
    $pluginRoot = Join-Path $Package $plugin
    $pluginManifest = Join-Path $pluginRoot '.codex-plugin/plugin.json'
    $manifest = Get-Content -LiteralPath $pluginManifest -Raw | ConvertFrom-Json
    if ($manifest.name -cne 'amifolio' -or $manifest.version -cne $Version) {
        throw "Codex 插件清单与 $Version 版本不符。"
    }
    foreach ($name in @('.codex-plugin/plugin.json', '.mcp.json', 'hooks/hooks.json')) {
        Assert-ReleaseFileMatch (Join-Path $pluginSource "package/$name") (Join-Path $pluginRoot $name)
    }
    Assert-ReleaseFileMatch (Join-Path $Package 'codex/skills/amifolio/SKILL.md') (Join-Path $pluginRoot 'skills/amifolio/SKILL.md')
    $bridge = Join-Path $Package 'bridge'
    foreach ($file in (Get-ChildItem -LiteralPath $bridge -Recurse -File -Force | Where-Object Extension -ne '.pdb')) {
        $copy = Join-Path $pluginRoot "runtime/$([IO.Path]::GetRelativePath($bridge, $file.FullName))"
        Assert-OrdinaryReleasePath $pluginRoot $copy
        Assert-ReleaseFileMatch $file.FullName $copy
    }

    foreach ($name in @('desktop/Amifolio.dll', 'bridge/Amifolio.Bridge.dll', 'worker/Amifolio.Worker.dll')) {
        $productVersion = (Get-Item -LiteralPath (Join-Path $Package $name)).VersionInfo.ProductVersion
        if (-not $productVersion -or $productVersion.Split('+')[0] -cne $Version) {
            throw "发布程序版本与 $Version 不符：$name"
        }
    }
}
