[CmdletBinding()]
param(
    [switch]$UseExistingLocalProxy
)

$dotnetDirectory = Join-Path $env:ProgramFiles 'dotnet'
$dotnetExecutable = Join-Path $dotnetDirectory 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetExecutable)) {
    throw '未找到 .NET，请先按照开发环境说明安装 SDK。'
}

if (($env:PATH -split ';') -notcontains $dotnetDirectory) {
    $env:PATH = "$dotnetDirectory;$env:PATH"
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if ($UseExistingLocalProxy) {
    $internetSettings = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    $proxyAddress = [string]$internetSettings.ProxyServer
    if ($proxyAddress -notmatch '^(?:http://)?(?:127\.0\.0\.1|localhost):(?<port>\d{1,5})$' -or
        [int]$Matches.port -lt 1 -or [int]$Matches.port -gt 65535) {
        throw '没有找到可复用的本机代理地址；未修改系统代理。'
    }
    if (-not $proxyAddress.StartsWith('http://')) {
        $proxyAddress = "http://$proxyAddress"
    }
    $env:HTTP_PROXY = $proxyAddress
    $env:HTTPS_PROXY = $proxyAddress
    $env:DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP2SUPPORT = 'false'
    Write-Host '已为当前 PowerShell 会话启用现有本机代理；未修改系统代理或证书。'
}

& $dotnetExecutable --version
if ($LASTEXITCODE -ne 0) {
    throw '当前目录的 .NET SDK 解析失败，请检查 global.json。'
}
Write-Host '开发环境已载入当前 PowerShell 会话。'
