[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BridgeRoot,
    [Parameter(Mandatory)][string]$MarketplaceRoot
)
$ErrorActionPreference='Stop'
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root 'plugins/codex'
$fullBridge = (Resolve-Path -LiteralPath $BridgeRoot).Path
if (!(Test-Path -LiteralPath (Join-Path $fullBridge 'Amifolio.Bridge.exe') -PathType Leaf)) {
    throw '先构建 Windows x64 桥接程序。'
}
$market = [IO.Path]::GetFullPath($MarketplaceRoot, $root)
$catalog = Join-Path $market '.agents/plugins'
$plugin = Join-Path $market 'plugins/amifolio'
foreach ($folder in @($catalog, $plugin)) {
    [void][IO.Directory]::CreateDirectory($folder)
}
Copy-Item -LiteralPath (Join-Path $source 'marketplace.json') -Destination $catalog -Force
Get-ChildItem -LiteralPath (Join-Path $source 'package') -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $plugin -Recurse -Force
}
foreach ($content in @(
    @{ From = Join-Path $source 'skills'; To = Join-Path $plugin 'skills' },
    @{ From = $fullBridge; To = Join-Path $plugin 'runtime' }
)) {
    [void][IO.Directory]::CreateDirectory($content.To)
    Get-ChildItem -LiteralPath $content.From -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $content.To -Recurse -Force
    }
}
Write-Host ("Amifolio local marketplace: " + $market)
