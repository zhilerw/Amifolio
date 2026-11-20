[CmdletBinding()]
param([string]$PackageRoot='artifacts/Amifolio',[string]$ResultsRoot='.local/stage9/performance',[ValidateRange(300,1800)][int]$IdleSeconds=300,[switch]$Stress)
$ErrorActionPreference='Stop'
$work=Split-Path $PSScriptRoot -Parent
$package=[IO.Path]::GetFullPath($PackageRoot,$work)
$results=[IO.Path]::GetFullPath($ResultsRoot,$work)
$run=Join-Path $results ([Guid]::NewGuid().ToString('N'))
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('Amifolio-performance-'+[Guid]::NewGuid().ToString('N'))
$homePath=Join-Path $fixture 'codex'
$source=Join-Path $fixture 'project'
$data=Join-Path $run 'data'
foreach($directory in @($run,$homePath,$source,$data)){[IO.Directory]::CreateDirectory($directory)|Out-Null}
@{owner='AmifolioStage9Performance'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $data 'performance-fixture.json') -Encoding utf8
foreach($directory in @($homePath,$source)){@{owner='AmifolioStage9Performance'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $directory 'performance-fixture.json') -Encoding utf8}
1..100|ForEach-Object{[IO.File]::WriteAllText((Join-Path $source ('sample{0:D3}.md' -f $_)),('# synthetic performance input '+$_))}
if($Stress){
    $docx=[IO.Compression.ZipFile]::Open((Join-Path $source 'parse-workload.docx'),[IO.Compression.ZipArchiveMode]::Create)
    try{
        $entry=$docx.CreateEntry('word/document.xml');$writer=[IO.StreamWriter]::new($entry.Open(),[Text.UTF8Encoding]::new($false))
        # Keep the real parsing workload below the parser's per-document XML node budget.
        try{$writer.Write('<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>');$writer.Write(('<w:p><w:r><w:t>synthetic performance input</w:t></w:r></w:p>'*7000));$writer.Write('</w:body></w:document>')}finally{$writer.Dispose()}
    }finally{$docx.Dispose()}
}
@{'local-projects'=@{'stage9-probe'=@{id='stage9-probe';name='Synthetic';rootPaths=@($source)}}}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $homePath '.codex-global-state.json') -Encoding utf8
$before=@(Get-ChildItem -LiteralPath $source -File|ForEach-Object{@{name=$_.Name;hash=(Get-FileHash -LiteralPath $_.FullName).Hash;modified=$_.LastWriteTimeUtc.Ticks;attributes=[int]$_.Attributes}})
$processes=[Collections.Generic.List[Diagnostics.Process]]::new()
function Launch([string]$exe,[string[]]$arguments){
    $info=[Diagnostics.ProcessStartInfo]::new($exe);$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle='Hidden'
    $info.WorkingDirectory=Split-Path $exe -Parent
    $info.Environment['CODEX_HOME']=$homePath
    foreach($argument in $arguments){$info.ArgumentList.Add($argument)}
    $info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $child=[Diagnostics.Process]::Start($info);$processes.Add($child);return $child
}
try{
    $report=Join-Path $data 'probe.json'
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $launchArgs=@('--data-root',$data,'--profile-report',$report,'--profile-parse','true');if($Stress){$launchArgs+=@('--profile-stress','true')}
    $ui=Launch (Join-Path $package 'desktop/Amifolio.exe') $launchArgs
    while(-not(Test-Path -LiteralPath ($report+'.ready'))){
        if($ui.HasExited -or $watch.Elapsed.TotalSeconds -gt 60){throw 'Isolated native startup failed.'};Start-Sleep -Milliseconds 25
    }
    $startupMs=$watch.Elapsed.TotalMilliseconds
    $nativeStartupMs=([DateTimeOffset](Get-Content -LiteralPath ($report+'.ready') -Raw|ConvertFrom-Json).readyAt-[DateTimeOffset]$ui.StartTime.ToUniversalTime()).TotalMilliseconds
    $bridge=Launch (Join-Path $package 'bridge/Amifolio.Bridge.exe') @('mcp','--data-root',$data)
    $bridge.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"isolated-performance","version":"1"}}}')
    $bridge.StandardInput.Flush()
    while(-not(Test-Path -LiteralPath $report)){
        if(Test-Path -LiteralPath ($report+'.error')){throw 'Isolated probe failed.'}
        if($ui.HasExited -or $watch.Elapsed.TotalSeconds -gt 240){throw 'Isolated probe did not finish.'};Start-Sleep -Milliseconds 100
    }
    $cpu=Get-CimInstance Win32_Processor|Select-Object -First 1
    $os=Get-CimInstance Win32_OperatingSystem
    $logical=[Environment]::ProcessorCount
    $samples=[Collections.Generic.List[object]]::new();$previous=@{}
    $idle=[Diagnostics.Stopwatch]::StartNew();$round=0
    while($true){
        $round++;$sampleSeconds=$idle.Elapsed.TotalSeconds
        $tree=@(Get-CimInstance Win32_Process|Select-Object ProcessId,ParentProcessId,Name)
        $ids=[Collections.Generic.HashSet[int]]::new();[void]$ids.Add($ui.Id);[void]$ids.Add($bridge.Id)
        do{$added=$false;foreach($item in $tree){if($ids.Contains([int]$item.ParentProcessId)-and $ids.Add([int]$item.ProcessId)){$added=$true}}}while($added)
        $counterFilter=($ids|ForEach-Object{"IDProcess = $_"})-join' OR '
        $counters=@(Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter $counterFilter)
        foreach($childId in $ids){
            try{
                $child=[Diagnostics.Process]::GetProcessById($childId);$child.Refresh()
                $elapsed=$sampleSeconds;$total=$child.TotalProcessorTime.TotalSeconds
                $cpuDelta=if($previous.ContainsKey($childId)){$total-$previous[$childId].cpu}else{$null}
                $percent=if($previous.ContainsKey($childId)){100*($total-$previous[$childId].cpu)/($elapsed-$previous[$childId].time)/$logical}else{$null}
                $previous[$childId]=@{cpu=$total;time=$elapsed}
                $counter=$counters|Where-Object IDProcess -eq $childId|Select-Object -First 1
                $role=if($childId-eq$ui.Id){'ui'}elseif($childId-eq$bridge.Id){'bridge'}elseif($child.ProcessName-eq'Amifolio.Worker'){'worker'}else{'runtime'}
                $samples.Add([pscustomobject][ordered]@{round=$round;seconds=[math]::Round($elapsed,3);pid=$childId;processName=$child.ProcessName;role=$role;cpuSecondsDelta=$cpuDelta;cpuPercent=$percent;workingSetMiB=$child.WorkingSet64/1MB;privateCommitMiB=$child.PrivateMemorySize64/1MB;privateWorkingSetMiB=if($counter){$counter.WorkingSetPrivate/1MB}else{$null}})
                $child.Dispose()
            }catch [ArgumentException]{}
        }
        if($ui.HasExited -or $bridge.HasExited){throw 'An owned idle process exited unexpectedly.'}
        if($sampleSeconds-ge$IdleSeconds){break}
        if($idle.Elapsed.TotalSeconds-lt$IdleSeconds){Start-Sleep -Seconds ([math]::Min(10,[math]::Ceiling($IdleSeconds-$idle.Elapsed.TotalSeconds)))}
    }
    $rollup=@($samples|Group-Object role|ForEach-Object{
        $rows=$_.Group;$valid=@($rows|Where-Object {$null-ne$_.cpuPercent})
        [ordered]@{role=$_.Name;sampleCount=$rows.Count;privateWorkingSetSamples=@($rows|Where-Object {$null-ne$_.privateWorkingSetMiB}).Count;maxPrivateWorkingSetMiB=($rows.privateWorkingSetMiB|Measure-Object -Maximum).Maximum;meanCpuPercent=100*($valid.cpuSecondsDelta|Measure-Object -Sum).Sum/($samples[-1].seconds-$samples[0].seconds)/$logical;maxPrivateCommitMiB=($rows.privateCommitMiB|Measure-Object -Maximum).Maximum}
    })
    if($rollup.role-notcontains'worker'){$rollup+=@{role='worker';sampleCount=0;privateWorkingSetSamples=0;maxPrivateWorkingSetMiB=$null;meanCpuPercent=0;maxPrivateCommitMiB=$null;note='No worker alive at idle sample points; parse peak is not measured.'}}
    # Reopen the same owned library to measure the persisted directory-cache path.
    $ui.Kill($true);$ui.WaitForExit()
    $warmReport=Join-Path $data 'probe-warm.json';$warmWatch=[Diagnostics.Stopwatch]::StartNew()
    $warm=Launch (Join-Path $package 'desktop/Amifolio.exe') @('--data-root',$data,'--profile-report',$warmReport)
    while(-not(Test-Path -LiteralPath ($warmReport+'.ready'))){if($warm.HasExited-or$warmWatch.Elapsed.TotalSeconds-gt60){throw 'Warm reopen failed.'};Start-Sleep -Milliseconds 25}
    $warmStartupMs=$warmWatch.Elapsed.TotalMilliseconds
    $warmNativeStartupMs=([DateTimeOffset](Get-Content -LiteralPath ($warmReport+'.ready') -Raw|ConvertFrom-Json).readyAt-[DateTimeOffset]$warm.StartTime.ToUniversalTime()).TotalMilliseconds
    while(-not(Test-Path -LiteralPath $warmReport)){if($warm.HasExited-or$warmWatch.Elapsed.TotalSeconds-gt90-or(Test-Path -LiteralPath ($warmReport+'.error'))){throw 'Warm probe failed.'};Start-Sleep -Milliseconds 50}
    foreach($original in $before){$file=Get-Item -LiteralPath (Join-Path $source $original.name);if((Get-FileHash -LiteralPath $file.FullName).Hash-ne$original.hash-or$file.LastWriteTimeUtc.Ticks-ne$original.modified-or[int]$file.Attributes-ne$original.attributes){throw 'Synthetic input integrity check failed.'}}
    $groupMean=100*($samples.cpuSecondsDelta|Measure-Object -Sum).Sum/($samples[-1].seconds-$samples[0].seconds)/$logical
    [ordered]@{status='measured';syntheticOnly=$true;sourceIntegrityVerified=$true;startupMilliseconds=$startupMs;nativeStartupMilliseconds=$nativeStartupMs;warmStartupMilliseconds=$warmStartupMs;warmNativeStartupMilliseconds=$warmNativeStartupMs;idleSeconds=$samples[-1].seconds;meanGroupCpuPercent=$groupMean;hardware=@{cpu=$cpu.Name;logicalProcessors=$logical;physicalMemoryGiB=$os.TotalVisibleMemorySize/1MB;os=$os.Caption};rollup=$rollup;samples=$samples;probe=(Get-Content -LiteralPath $report -Raw|ConvertFrom-Json);warmProbe=(Get-Content -LiteralPath $warmReport -Raw|ConvertFrom-Json);limitations=@('Current machine; not a 4-core 8GB acceptance machine.','Fresh application data; OS file cache is uncontrolled, not true cold boot.','No physical input/frame pacing claim.','Workers are observed as descendants; idle parsing worker count may be zero.')}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $run 'summary.json') -Encoding utf8
    Write-Host "Performance evidence: $run"
}finally{
    foreach($owned in $processes){try{if(-not$owned.HasExited){$owned.Kill($true);$owned.WaitForExit()}}finally{$owned.Dispose()}}
}
exit 0
