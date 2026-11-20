[CmdletBinding()]
param([string]$DataRoot)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$executable = Join-Path $projectRoot 'artifacts\Amifolio\desktop\Amifolio.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    throw '尚未生成可运行版本，请先运行 tools\Build.ps1。'
}
$launchOptions = @{ FilePath = $executable; WorkingDirectory = Split-Path $executable -Parent; WindowStyle = 'Hidden' }
if ($DataRoot) {
    if (-not [IO.Path]::IsPathFullyQualified($DataRoot)) { throw '数据目录必须是绝对路径。' }
    if ($DataRoot.Contains('"')) { throw '数据目录包含不支持的引号。' }
    $argumentDataRoot = $DataRoot
    if ($argumentDataRoot.EndsWith('\')) { $argumentDataRoot += '.' }
    $launchOptions.ArgumentList = @('--data-root', ('"' + $argumentDataRoot + '"'))
}
Start-Process @launchOptions
