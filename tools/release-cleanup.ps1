# Release housekeeping: drop stale build worktrees and keep only the newest N dist builds.
#
# NOTE: this file is intentionally ASCII-only. Windows PowerShell 5.1 reads .ps1 as ANSI,
# so Korean text here breaks string terminators and the script fails to parse.
# The Korean explanation lives in ThrowMe_릴리스_작업_규칙.md instead.
#
# Usage (from anywhere):
#   powershell -ExecutionPolicy Bypass -File tools\release-cleanup.ps1
#   powershell -ExecutionPolicy Bypass -File tools\release-cleanup.ps1 -Keep 5 -WhatIf

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    # How many dist builds to keep (newest by version number).
    [int]$Keep = 5,

    # Project container that holds ThrowMe\, dist\ and any leftover _r* worktrees.
    [string]$Root = 'C:\claudeProject\Slimey'
)

$repo = Join-Path $Root 'ThrowMe'
$dist = Join-Path $Root 'dist'

function Info($m) { Write-Host "  $m" }

# ── 1) stale release worktrees (_r1161 etc.) ─────────────────────────────────
# Each release builds from a throwaway worktree so the binary comes from the exact
# committed tree. Removal can fail while MSBuild/AV still holds files, leaving the
# folder behind. Anything not registered with git is safe to delete.
Write-Host 'Stale release worktrees'
$registered = @()
if (Test-Path (Join-Path $repo '.git')) {
    Push-Location $repo
    try {
        $registered = (git worktree list --porcelain 2>$null |
            Where-Object { $_ -like 'worktree *' } |
            ForEach-Object { ($_ -replace '^worktree ', '').Replace('/', '\') })
    } finally { Pop-Location }
}

$stale = Get-ChildItem $Root -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like '_r*' -and $registered -notcontains $_.FullName }

if (-not $stale) { Info 'none' }
foreach ($d in $stale) {
    if ($PSCmdlet.ShouldProcess($d.FullName, 'Remove leftover worktree')) {
        try { Remove-Item $d.FullName -Recurse -Force -ErrorAction Stop; Info "removed $($d.Name)" }
        catch { Info "LOCKED  $($d.Name) - close Visual Studio/Explorer and re-run" }
    }
}

# Drop git's bookkeeping for worktrees whose folder is gone.
if (Test-Path (Join-Path $repo '.git')) {
    Push-Location $repo
    try { git worktree prune 2>$null | Out-Null } finally { Pop-Location }
}

# ── 2) dist retention ────────────────────────────────────────────────────────
# Keep the newest $Keep builds by VERSION (not timestamp - a rebuilt old version
# would otherwise outrank a newer one). Folders that are not "ThrowMe-x.y.z" are
# left alone; dist also holds hand-written material such as _멀티PC-설정.
Write-Host "dist retention (keep newest $Keep)"
if (-not (Test-Path $dist)) { Info 'no dist folder'; return }

$builds = Get-ChildItem $dist -Directory | ForEach-Object {
    $m = [regex]::Match($_.Name, '^ThrowMe-(\d+)\.(\d+)\.(\d+)$')
    if ($m.Success) {
        [pscustomobject]@{
            Dir     = $_
            Version = [version]::new([int]$m.Groups[1].Value, [int]$m.Groups[2].Value, [int]$m.Groups[3].Value)
        }
    }
} | Where-Object { $_ } | Sort-Object Version -Descending

if ($builds.Count -le $Keep) { Info "$($builds.Count) builds - nothing to remove"; return }

$keepSet = $builds | Select-Object -First $Keep
$dropSet = $builds | Select-Object -Skip $Keep
Info ("keeping: " + (($keepSet | ForEach-Object { $_.Version }) -join ', '))

$freed = 0
foreach ($b in $dropSet) {
    $size = (Get-ChildItem $b.Dir.FullName -Recurse -File -ErrorAction SilentlyContinue |
        Measure-Object Length -Sum).Sum
    if ($PSCmdlet.ShouldProcess($b.Dir.FullName, 'Remove old build')) {
        try {
            Remove-Item $b.Dir.FullName -Recurse -Force -ErrorAction Stop
            $freed += $size
            Info ("removed {0} ({1:N0} MB)" -f $b.Dir.Name, ($size / 1MB))
        } catch { Info "LOCKED  $($b.Dir.Name)" }
    }
}
Write-Host ("Freed {0:N0} MB" -f ($freed / 1MB))
