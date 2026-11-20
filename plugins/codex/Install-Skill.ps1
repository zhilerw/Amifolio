[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory = $true)]
    [string]$SkillsRoot,
    [switch]$Install,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
if ($Install -and $DryRun) { throw 'Choose -Install or -DryRun, not both.' }
$source = Join-Path $PSScriptRoot 'skills\amifolio'
if (-not (Test-Path -LiteralPath (Join-Path $source 'SKILL.md') -PathType Leaf)) { throw 'The bundled skill is missing.' }
$resolvedRoot = [IO.Path]::GetFullPath($SkillsRoot)
$destination = Join-Path $resolvedRoot 'amifolio'
$backupRoot = Join-Path ([IO.Path]::GetDirectoryName($resolvedRoot.TrimEnd([IO.Path]::DirectorySeparatorChar))) '.amifolio-skill-backups'
$token = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '-' + [Guid]::NewGuid().ToString('N')
$backup = Join-Path $backupRoot $token
$stage = Join-Path $backupRoot ($token + '-stage')

function Assert-NoReparsePoint([string]$Path) {
    $candidate = $Path
    while ($candidate) {
        if (Test-Path -LiteralPath $candidate) {
            $item = Get-Item -LiteralPath $candidate -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Skill installation does not follow symbolic links or junctions.' }
            if (-not $item.PSIsContainer) { throw 'An installation path is not a directory.' }
        }
        $parent = [IO.Path]::GetDirectoryName($candidate.TrimEnd([IO.Path]::DirectorySeparatorChar))
        if ($parent -eq $candidate) { break }
        $candidate = $parent
    }
}

Assert-NoReparsePoint $resolvedRoot
Assert-NoReparsePoint $destination
Assert-NoReparsePoint $backupRoot
Get-ChildItem -LiteralPath $source -Recurse -Force | ForEach-Object {
    if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The skill bundle contains a symbolic link or junction.' }
}
$existing = Test-Path -LiteralPath $destination
if ($existing) {
    Get-ChildItem -LiteralPath $destination -Recurse -Force | ForEach-Object {
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The existing skill contains a symbolic link or junction.' }
    }
}

if (-not $Install -or $DryRun -or -not $PSCmdlet.ShouldProcess($destination, 'Install only the Amifolio skill, backing up its previous version')) {
    [pscustomobject]@{ mode = 'dry-run'; source = $source; destination = $destination; backup = $(if ($existing) { $backup } else { $null }) } | ConvertTo-Json -Compress
    return
}

New-Item -ItemType Directory -Path $resolvedRoot -Force | Out-Null
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
# Stage the supplied skill before touching an existing installation. No config or AGENTS files are copied.
Copy-Item -LiteralPath $source -Destination $stage -Recurse
if ($existing) { Move-Item -LiteralPath $destination -Destination $backup }
try { Move-Item -LiteralPath $stage -Destination $destination }
catch {
    if ($existing -and -not (Test-Path -LiteralPath $destination)) { Move-Item -LiteralPath $backup -Destination $destination }
    throw
}
[pscustomobject]@{ mode = 'installed'; destination = $destination; backup = $(if ($existing) { $backup } else { $null }) } | ConvertTo-Json -Compress
