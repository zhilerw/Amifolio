[CmdletBinding()]
param([switch]$UseExistingLocalProxy, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    . .\tools\environment\Enter-DevEnvironment.ps1 -UseExistingLocalProxy:$UseExistingLocalProxy
    # Lock the test projects too; implicit restore during dotnet run could rewrite their locks.
    dotnet restore Amifolio.slnx --locked-mode --disable-parallel --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw '解决方案锁定依赖还原失败' }
    dotnet build Amifolio.slnx -c Release --no-restore --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw '解决方案编译失败' }
    if (-not $SkipTests) {
        foreach ($testProject in @('tests/Amifolio.Core.Tests/Amifolio.Core.Tests.csproj', 'tests/Amifolio.Context.Tests/Amifolio.Context.Tests.csproj', 'tests/Amifolio.Bridge.Tests/Amifolio.Bridge.Tests.csproj', 'tests/Amifolio.Desktop.Logic.Tests/Amifolio.Desktop.Logic.Tests.csproj')) {
            dotnet run --project $testProject -c Release --no-build --no-restore
            if ($LASTEXITCODE -ne 0) { throw "验证未通过：$testProject" }
        }
    }
    dotnet publish src/Amifolio.Desktop/Amifolio.Desktop.csproj -c Release --no-restore --self-contained false -o artifacts/Amifolio/desktop
    if ($LASTEXITCODE -ne 0) { throw '桌面程序发布失败' }
    foreach ($resource in @('Amifolio.pri', 'App.xbf', 'MainWindow.xbf', 'LearningView.xbf')) {
        if (-not (Test-Path -LiteralPath (Join-Path 'artifacts/Amifolio/desktop' $resource))) { throw "发布缺少 WinUI 资源：$resource" }
    }
    # Native apphost follows this Windows SDK host. An ad-hoc RID here would rewrite
    # shared library lock files and break the next ordinary locked restore.
    dotnet publish src/Amifolio.Bridge/Amifolio.Bridge.csproj -c Release --no-restore --self-contained false -o artifacts/Amifolio/bridge
    if ($LASTEXITCODE -ne 0) { throw '桥接程序发布失败' }
    dotnet publish src/Amifolio.Worker/Amifolio.Worker.csproj -c Release --no-restore --self-contained false -o artifacts/Amifolio/worker
    if ($LASTEXITCODE -ne 0) { throw '解析组件发布失败' }
    New-Item -ItemType Directory -Path 'artifacts/Amifolio/codex' -Force | Out-Null
    Get-ChildItem -LiteralPath 'plugins/codex' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination 'artifacts/Amifolio/codex' -Recurse -Force }
    $skillZip = Join-Path $projectRoot 'artifacts/Amifolio/codex/Amifolio-Codex-skills.zip'
    if(Test-Path -LiteralPath $skillZip){Remove-Item -LiteralPath $skillZip}
    [IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $projectRoot 'plugins/codex/skills/amifolio'),$skillZip,[IO.Compression.CompressionLevel]::Fastest,$true)
    Copy-Item -LiteralPath 'tools/Package-README.md' -Destination 'artifacts/Amifolio/README.md' -Force
    New-Item -ItemType Directory -Path 'artifacts/Amifolio/docs' -Force | Out-Null
    Copy-Item -LiteralPath 'docs/RELEASE.md' -Destination 'artifacts/Amifolio/docs/RELEASE.md' -Force
    Copy-Item -LiteralPath 'docs/STORAGE-RECOVERY.md' -Destination 'artifacts/Amifolio/docs/STORAGE-RECOVERY.md' -Force
    Copy-Item -LiteralPath 'docs/TASKS.md' -Destination 'artifacts/Amifolio/docs/TASKS.md' -Force
    Copy-Item -LiteralPath 'docs/COLLECTION.md' -Destination 'artifacts/Amifolio/docs/COLLECTION.md' -Force
    Copy-Item -LiteralPath 'docs/FORMATS.md' -Destination 'artifacts/Amifolio/docs/FORMATS.md' -Force
    Copy-Item -LiteralPath 'docs/CONTEXT.md' -Destination 'artifacts/Amifolio/docs/CONTEXT.md' -Force
    Copy-Item -LiteralPath 'THIRD_PARTY_NOTICES.md' -Destination 'artifacts/Amifolio/THIRD_PARTY_NOTICES.md' -Force
    Copy-Item -LiteralPath 'docs/third-party' -Destination 'artifacts/Amifolio/docs' -Recurse -Force
    Write-Host '可运行目录：artifacts\Amifolio；双击 desktop\Amifolio.exe 启动。'
} finally { Pop-Location }
