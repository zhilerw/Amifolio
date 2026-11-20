[CmdletBinding()]
param([string]$PackageRoot='artifacts/Amifolio',[string]$ResultsRoot='.local/stage5/package')
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$package=[IO.Path]::GetFullPath($PackageRoot,$repo)
$run=Join-Path ([IO.Path]::GetFullPath($ResultsRoot,$repo)) ([Guid]::NewGuid().ToString('N'))
$synthetic=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('Amifolio-stage5-package-'+[Guid]::NewGuid().ToString('N'))))
$oldCodexHome=$env:CODEX_HOME
try{
    New-Item -ItemType Directory -Path $run,$synthetic -Force|Out-Null
    Copy-Item -LiteralPath (Join-Path $package 'bridge') -Destination $run -Recurse
    $exe=Join-Path $run 'bridge/Amifolio.Bridge.exe'
    $data=Join-Path $run 'data'
    $env:CODEX_HOME=Join-Path $run 'codex'
    New-Item -ItemType Directory -Path $env:CODEX_HOME|Out-Null
    @{ 'local-projects'=@{p=@{name='Synthetic';rootPaths=@($synthetic)}} }|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $env:CODEX_HOME '.codex-global-state.json') -Encoding utf8
    $source=Join-Path $synthetic 'notes.md'
    [IO.File]::WriteAllText($source,'Synthetic original remains read-only.')
    $before=(Get-FileHash -LiteralPath $source).Hash
    $time=[IO.File]::GetLastWriteTimeUtc($source)
    $attrs=[IO.File]::GetAttributes($source)
    $receipt=(& $exe --data-root $data submit $source|ConvertFrom-Json)
    if($LASTEXITCODE -ne 0 -or $receipt.status -ne 'referenced' -or $receipt.path -ne $source){throw '引用回执错误。'}
    $tasks=(& $exe --data-root $data tasks|ConvertFrom-Json).tasks
    if($LASTEXITCODE -ne 0 -or @($tasks).Count -ne 0){throw '登记引用不应启动解析。'}
    $status=(& $exe --data-root $data status $receipt.id|ConvertFrom-Json)
    if($LASTEXITCODE -ne 0 -or $status.status -ne 'referenced' -or $status.extractionStatus -ne '未解析'){throw '引用与解析状态不明确。'}
    if(@(Get-ChildItem -LiteralPath (Join-Path $data 'objects') -File).Count -ne 0){throw '不应复制原文件。'}
    if((Get-FileHash -LiteralPath $source).Hash -ne $before -or [IO.File]::GetLastWriteTimeUtc($source) -ne $time -or [IO.File]::GetAttributes($source) -ne $attrs){throw '原文件发生变化。'}
    $again=(& $exe --data-root $data submit $source|ConvertFrom-Json)
    if($LASTEXITCODE -ne 0 -or $again.id -ne $receipt.id){throw '重复引用不幂等。'}
    $outside=Join-Path $run 'outside.md';[IO.File]::WriteAllText($outside,'Outside current project.')
    $rejected=(& $exe --data-root $data submit $outside 2>(Join-Path $run 'outside.stderr.txt')|ConvertFrom-Json)
    if($LASTEXITCODE -eq 0 -or [string]::IsNullOrEmpty($rejected.error.code)){throw '项目外文件未被拒绝。'}
    [ordered]@{status='passed';checks=@('reference-without-worker','no-automatic-parser-task','reference-status-distinct','no-original-copy','source-bytes-time-attributes-unchanged','reference-idempotent','outside-project-rejected');syntheticOnly=$true}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $run 'report.json') -Encoding utf8
    Write-Host "PASS isolated readonly package: 7 checks; report $run"
}finally{
    $env:CODEX_HOME=$oldCodexHome
    $expected=Join-Path ([IO.Path]::GetTempPath()) 'Amifolio-stage5-package-'
    if(-not $synthetic.StartsWith($expected,[StringComparison]::OrdinalIgnoreCase)){throw '清理路径越界。'}
    if(Test-Path -LiteralPath $synthetic){Remove-Item -LiteralPath $synthetic -Recurse -Force}
}
exit 0
