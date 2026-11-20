[CmdletBinding()]
param([string]$PackageRoot='artifacts/Amifolio',[string]$ResultsRoot='.local/stage3-package')
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$package=[IO.Path]::GetFullPath($PackageRoot,$repo)
$run=Join-Path ([IO.Path]::GetFullPath($ResultsRoot,$repo)) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force|Out-Null
# Copy only the bridge: the separate parser is deliberately absent.
Copy-Item -LiteralPath (Join-Path $package 'bridge') -Destination $run -Recurse
$exe=Join-Path $run 'bridge/Amifolio.Bridge.exe'
$data=Join-Path $run 'data'
$source=Join-Path $run 'synthetic.md'
Set-Content -LiteralPath $source -Value 'Synthetic missing-component acceptance document.' -Encoding utf8
$receipt=(& $exe --data-root $data submit $source|ConvertFrom-Json)
if($LASTEXITCODE -ne 0 -or $receipt.status -ne 'archived' -or -not(Test-Path -LiteralPath $receipt.path)){throw '组件缺失影响了原件入库。'}
$tasks=(& $exe --data-root $data tasks|ConvertFrom-Json).tasks
if($LASTEXITCODE -ne 0 -or $tasks.Count -ne 1 -or $tasks[0].State -ne 'failed' -or $tasks[0].ErrorCode -ne 'worker_unavailable'){throw '解析组件缺失未形成明确失败任务。'}
$status=(& $exe --data-root $data status $receipt.id|ConvertFrom-Json)
if($LASTEXITCODE -ne 0 -or $status.status -ne 'archived' -or $status.extractionStatus -ne '提取失败'){throw '归档与分析状态未分开。'}
[ordered]@{status='passed';checks=@('archive-retained-without-worker','missing-worker-visible','archive-analysis-states-distinct');syntheticOnly=$true}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $run 'report.json') -Encoding utf8
Write-Host "PASS isolated missing-worker package: 3 checks; report $run"
