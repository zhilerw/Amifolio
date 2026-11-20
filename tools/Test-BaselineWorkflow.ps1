[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageRoot,
    [Parameter(Mandatory)][string]$SourceRoot,
    [string]$ResultsRoot = '.local/baseline-workflow',
    [ValidateSet('prepare','verify')][string]$Phase = 'prepare',
    [string]$RunRoot
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$package = [IO.Path]::GetFullPath($PackageRoot, $root)
$source = [IO.Path]::GetFullPath($SourceRoot, $root)
if ($Phase -eq 'verify' -and !$RunRoot) { throw 'verify requires an existing isolated RunRoot' }
$run = if ($RunRoot) { [IO.Path]::GetFullPath($RunRoot, $root) } else { Join-Path ([IO.Path]::GetFullPath($ResultsRoot, $root)) ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')) }
# Refuse the real default store and require our own marker before a verification pass.
$formal = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Amifolio'))
if ($run.Equals($formal, [StringComparison]::OrdinalIgnoreCase) -or $run.StartsWith($formal + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'The formal user store is not a workflow test location' }
if ($Phase -eq 'prepare' -and (Test-Path -LiteralPath $run)) { throw 'Prepare must use a new empty run directory' }
if ($Phase -eq 'verify' -and !(Test-Path -LiteralPath (Join-Path $run 'baseline-test.marker'))) { throw 'Missing isolated test marker' }
New-Item -ItemType Directory -Path $run -Force | Out-Null
if ($Phase -eq 'prepare') { 'Amifolio isolated baseline workflow' | Set-Content (Join-Path $run 'baseline-test.marker') }
$harness = Join-Path $run 'harness'
New-Item -ItemType Directory -Path $harness -Force | Out-Null
$core = [Security.SecurityElement]::Escape((Join-Path $source 'src/Amifolio.Core/Amifolio.Core.csproj'))
$context = [Security.SecurityElement]::Escape((Join-Path $source 'src/Amifolio.Context/Amifolio.Context.csproj'))
@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include="Program.cs" /><ProjectReference Include="$core" /><ProjectReference Include="$context" /></ItemGroup></Project>
"@ | Set-Content (Join-Path $harness 'Workflow.csproj') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'validation/BaselineWorkflow.cs') -Destination (Join-Path $harness 'Program.cs') -Force
& dotnet build (Join-Path $harness 'Workflow.csproj') -c Release --verbosity quiet
if ($LASTEXITCODE) { throw 'Workflow helper build failed' }
foreach ($name in @('Amifolio.Core.dll','Amifolio.Context.dll')) {
    $actual = (Get-FileHash -LiteralPath (Join-Path $harness ('bin/Release/net10.0/' + $name))).Hash
    $tested = (Get-FileHash -LiteralPath (Join-Path $package ('bridge/' + $name))).Hash
    if ($actual -ne $tested) { throw "Helper/package mismatch: $name" }
}
& dotnet (Join-Path $harness 'bin/Release/net10.0/Workflow.dll') $package $run $Phase
if ($LASTEXITCODE) { throw 'Baseline workflow failed' }
Write-Host "RunRoot: $run"
