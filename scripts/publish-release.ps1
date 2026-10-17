#Requires -Version 7.4
<#
.SYNOPSIS
    Republishes the LayoutSync CLI and MCP server into release/ while running sessions hold the
    old files open.

.DESCRIPTION
    Every Claude Code session that started layoutsync-mcp keeps LayoutSync.Mcp.exe and the DLLs
    it loaded open, and Windows refuses to overwrite a file in use. A plain
    `dotnet publish -o release` therefore fails partway and can leave release/ with a new CLI on
    an old LayoutSync.Core.dll (#34). Windows does allow moving a file in use, so this script:

      1. builds both programs into a temporary staging folder;
      2. stops if release/ already reports the same version and commit as the build;
      3. compares each staged file byte for byte with its copy in release/;
      4. for each file that differs or is new, moves the old copy into
         release/.replaced-<yyyyMMdd-HHmmss>/, copies the new file in and checks its bytes;
      5. checks that the installed CLI reports the build's version;
      6. on any failure or Ctrl+C, removes the copied files and moves the originals back.

    A run that is killed outright cannot roll back. It leaves release/.publish-incomplete, and
    the next run then compares every file instead of trusting --version, which repairs the mix.

    Running sessions keep the files they already loaded; sessions started afterwards get the new
    build. Files the build no longer produces are left in release/.

    One publish at a time: a run holds release/.publish.lock open from before the build until it
    exits, and a second run is refused. The lock is the open handle, not the file's existence,
    so a run that is killed cannot leave a stale lock behind.

    Each run also deletes earlier .replaced-* folders, and *.stale-* files left by hand-made
    swaps, once no running process still has their files open. The rest stay for a later run.

.PARAMETER ReleaseDir
    The folder to publish into. Defaults to release/ in the repository's main checkout, also
    when the script runs from a worktree, because that is the folder on PATH.

.PARAMETER Version
    The version to stamp (-p:Version), for a build of a release tag. Without it the build
    reports 1.0.0+<commit>.

.PARAMETER Force
    Swap even when release/ already reports this build's version and commit, for example to
    install uncommitted changes. Byte-identical files are still left alone.

.EXAMPLE
    pwsh scripts/publish-release.ps1 -WhatIf

    Builds and lists the files that would change, without touching release/.

.EXAMPLE
    pwsh scripts/publish-release.ps1 -Version 1.2.5

    Publishes a build that reports 1.2.5+<commit>.

.NOTES
    Exit codes: 0 published, skipped or already up to date; 1 failed (the message says whether
    release/ was restored); 2 another publish is running.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $ReleaseDir,

    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$cliExe = if ($IsWindows) { 'LayoutSync.exe' } else { 'LayoutSync' }
$projects = 'LayoutSync/LayoutSync.csproj', 'LayoutSync.Mcp/LayoutSync.Mcp.csproj'

# Sharing read, write and delete lets these reads succeed whatever share mode a running process
# (or another publish) opened the same file with.
$readShare = [IO.FileShare]'ReadWrite, Delete'

function Get-Sha256([string] $Path) {
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, $readShare)
    try { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
    finally { $stream.Dispose() }
}

# The first line of `<cli> --version`, for example 1.2.5+<full commit hash>, or $null when the
# program is missing or does not run.
function Get-ReportedVersion([string] $Dir) {
    $exe = Join-Path $Dir $cliExe
    if (-not [IO.File]::Exists($exe)) { return $null }
    try {
        $lines = @(& $exe --version 2>$null)
        if ($LASTEXITCODE -ne 0 -or $lines.Count -eq 0) { return $null }
        return "$($lines[0])".Trim()
    }
    catch { return $null }
}

# Returns the open lock stream, or $null when another run holds it. Sharing read lets a refused
# run say who holds the lock; DeleteOnClose has Windows remove the file even if this process is
# killed, so the lock cannot go stale.
function Enter-PublishLock([string] $Path, [string] $Commit) {
    try {
        $stream = [IO.FileStream]::new($Path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::Read, 4096, [IO.FileOptions]::DeleteOnClose)
    }
    catch [UnauthorizedAccessException] {
        # The holder has just closed it and Windows has not finished deleting it.
        if ([IO.File]::Exists($Path)) { return $null }
        throw
    }
    catch [IO.IOException] {
        # 0x20 sharing violation, 0x21 lock violation: another run has the file open.
        if (($_.Exception.HResult -band 0xFFFF) -in 0x20, 0x21) { return $null }
        throw
    }
    $holder = "pid $PID on $([Environment]::MachineName), started $(Get-Date -Format s), building $Commit from $repoRoot"
    $bytes = [Text.Encoding]::UTF8.GetBytes($holder)
    $stream.SetLength(0)
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush()
    return $stream
}

function Get-LockHolder([string] $Path) {
    try {
        $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, $readShare)
        try { $text = [IO.StreamReader]::new($stream).ReadToEnd() } finally { $stream.Dispose() }
        if ($text) { return $text }
    }
    catch { }
    return 'holder unknown'
}

# Undoes a partial swap, newest step first. Returns what it could not undo.
function Undo-Swap($Moved, $Copied, $CreatedDirs, [string] $AsideDir) {
    $problems = [Collections.Generic.List[string]]::new()
    for ($i = $Copied.Count - 1; $i -ge 0; $i--) {
        $file = $Copied[$i]
        try { [IO.File]::Delete($file) }
        catch {
            # A session that started during the swap may have loaded the new file, so it cannot
            # be deleted; it can still be moved out of the original's way.
            try {
                $parked = Join-Path $AsideDir 'rolled-back' ([IO.Path]::GetRelativePath($ReleaseDir, $file))
                [void] [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($parked))
                [IO.File]::Move($file, $parked)
                Write-Host "  $file is in use, so it was moved to $parked."
            }
            catch { $problems.Add("remove the new $file ($($_.Exception.Message))") }
        }
    }
    for ($i = $Moved.Count - 1; $i -ge 0; $i--) {
        try { [IO.File]::Move($Moved[$i].Aside, $Moved[$i].Original) }
        catch { $problems.Add("move $($Moved[$i].Aside) back to $($Moved[$i].Original) ($($_.Exception.Message))") }
    }
    foreach ($dir in @($CreatedDirs | Sort-Object Length -Descending)) {
        # A folder that is not empty still holds a file listed in $problems.
        try { [IO.Directory]::Delete($dir) } catch { }
    }
    if ($problems.Count -eq 0 -and [IO.Directory]::Exists($AsideDir) -and
        @(Get-ChildItem -LiteralPath $AsideDir -Recurse -File -Force).Count -eq 0) {
        try { [IO.Directory]::Delete($AsideDir, $true) }
        catch { Write-Warning "Could not delete the empty folder $AsideDir ($($_.Exception.Message))." }
    }
    # A complete rollback restores what this run found, so the marker stays if that was already mixed.
    if ($problems.Count -eq 0 -and -not $wasIncomplete) { [IO.File]::Delete($markerPath) }
    return $problems.ToArray()
}

# Moves each old file aside, copies the new one in and checks it, then checks the installed CLI.
# A failure or Ctrl+C undoes every completed step. A killed process cannot undo anything, so the
# marker written first stays behind and stops the next run from trusting --version.
function Invoke-Swap($Changes, [string] $AsideDir, [string] $ExpectedVersion) {
    $moved = [Collections.Generic.List[object]]::new()
    $copied = [Collections.Generic.List[string]]::new()
    $createdDirs = [Collections.Generic.List[string]]::new()
    $settled = $false
    [IO.File]::WriteAllText($markerPath,
        "pid $PID started swapping in $ExpectedVersion at $(Get-Date -Format s); the old copies are in $AsideDir")
    try {
        foreach ($change in $Changes) {
            if (-not $change.IsNew) {
                $aside = Join-Path $AsideDir $change.Path
                [void] [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($aside))
                # Windows lets a file in use be moved (its process keeps the open copy), not overwritten.
                [IO.File]::Move($change.Target, $aside)
                $moved.Add(@{ Original = $change.Target; Aside = $aside })
            }
            for ($dir = [IO.Path]::GetDirectoryName($change.Target); -not [IO.Directory]::Exists($dir);
                $dir = [IO.Path]::GetDirectoryName($dir)) {
                $createdDirs.Add($dir)
            }
            [void] [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($change.Target))
            $copied.Add($change.Target)   # before the copy, so a partial copy is removed too
            [IO.File]::Copy($change.Source, $change.Target)
            if ((Get-Sha256 $change.Target) -ne $change.Hash) {
                throw "the copy of $($change.Path) in release/ does not match the build"
            }
        }
        $reported = Get-ReportedVersion $ReleaseDir
        if ($reported -ne $ExpectedVersion) {
            throw "after the swap, $cliExe --version reports '$reported' instead of '$ExpectedVersion'"
        }
        $settled = $true
    }
    catch {
        $settled = $true
        $cause = $_.Exception.Message
        Write-Host "Swap failed: $cause. Rolling back $($copied.Count) copied and $($moved.Count) moved file(s)."
        $problems = @(Undo-Swap $moved $copied $createdDirs $AsideDir)
        if ($problems.Count -eq 0) {
            throw "the swap failed and was rolled back, so $ReleaseDir is as this run found it. Cause: $cause"
        }
        throw ("the swap failed and the rollback is INCOMPLETE, so $ReleaseDir may mix old and new files. " +
            "Cause: $cause. Still to do by hand:`n  " + ($problems -join "`n  "))
    }
    finally {
        # Ctrl+C stops the script without running catch blocks, but finally blocks still run.
        if (-not $settled) {
            Write-Host "Interrupted. Rolling back $($copied.Count) copied and $($moved.Count) moved file(s)."
            $problems = @(Undo-Swap $moved $copied $createdDirs $AsideDir)
            if ($problems.Count -eq 0) { Write-Host "Rolled back, so $ReleaseDir is as this run found it." }
            else { Write-Host ("The rollback is INCOMPLETE. Still to do by hand:`n  " + ($problems -join "`n  ")) }
        }
    }
    [IO.File]::Delete($markerPath)
}

# Trying to delete each file is the test of whether a running process still uses it: a process
# keeps reporting the path it started from, so the process list cannot tell old servers from new.
# Returns the names of the leftovers that could not be removed.
function Remove-Leftovers($Leftovers) {
    foreach ($item in $Leftovers) {
        if ($item.PSIsContainer) {
            foreach ($file in @(Get-ChildItem -LiteralPath $item.FullName -Recurse -File -Force)) {
                try { $file.Delete() } catch { }   # still in use: the folder delete below reports it
            }
        }
        try { if ($item.PSIsContainer) { $item.Delete($true) } else { $item.Delete() } }
        catch { $item.Name }
    }
}

$lock = $null
$staging = $null
$asideDir = $null
try {
    if (-not $ReleaseDir) {
        $commonDir = git -C $repoRoot rev-parse --path-format=absolute --git-common-dir
        if ($LASTEXITCODE -ne 0) { throw 'could not locate the main checkout with git; pass -ReleaseDir' }
        $ReleaseDir = Join-Path (Split-Path -Parent $commonDir) 'release'
    }
    $ReleaseDir = [IO.Path]::GetFullPath($ReleaseDir, $PWD.ProviderPath)
    $head = git -C $repoRoot rev-parse HEAD 2>$null
    if ($LASTEXITCODE -ne 0) { $head = 'an unknown commit' }

    # A dry run takes no lock, so it never writes to release/.
    $dryRun = [bool] $WhatIfPreference
    $staging = Join-Path ([IO.Path]::GetTempPath()) "layoutsync-publish-$PID-$(Get-Random)"

    if (-not $dryRun) {
        [void] [IO.Directory]::CreateDirectory($ReleaseDir)
        $lockPath = Join-Path $ReleaseDir '.publish.lock'
        $lock = Enter-PublishLock $lockPath $head
        if (-not $lock) {
            $host.UI.WriteErrorLine("publish-release: another publish into $ReleaseDir is running " +
                "($(Get-LockHolder $lockPath)). Nothing was changed; run again once it has finished.")
            exit 2
        }
    }

    Write-Host "Building $head from $repoRoot"
    $publishArgs = @('-c', 'Release', '--self-contained', 'false', '-o', $staging, '--nologo', '-v', 'minimal')
    if ($Version) { $publishArgs += "-p:Version=$Version" }
    foreach ($project in $projects) {
        & dotnet publish (Join-Path $repoRoot $project) @publishArgs | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project failed (exit $LASTEXITCODE); $ReleaseDir is unchanged" }
    }
    # RavenDB client caches; a stray one in the build must not overwrite the copy in release/.
    Get-ChildItem -LiteralPath $staging -Recurse -File -Force -Filter '*.raven-database-topology' |
        ForEach-Object { $_.Delete() }

    $builtVersion = Get-ReportedVersion $staging
    if (-not $builtVersion) { throw "the staged $cliExe does not report a version, so the build is unusable" }
    $installedVersion = Get-ReportedVersion $ReleaseDir

    # Left by a run that was killed mid-swap: the CLI may already report the new build while the
    # MCP server files are still old, so --version proves nothing until every file is compared.
    $markerPath = Join-Path $ReleaseDir '.publish-incomplete'
    $wasIncomplete = [IO.File]::Exists($markerPath)
    if ($wasIncomplete) {
        Write-Warning ("An earlier publish stopped partway through its swap ($(Get-Content -Raw -LiteralPath $markerPath)). " +
            'Comparing every file instead of trusting --version.')
    }

    # Byte comparison cannot spot a second build of the same commit: every DLL records the folder
    # it was built in. The version string carries the commit hash, so it can.
    if ($installedVersion -eq $builtVersion -and -not $Force -and -not $wasIncomplete) {
        Write-Host "$ReleaseDir already reports $builtVersion, the same version and commit as this build. Nothing to swap (pass -Force to swap anyway)."
    }
    else {
        $changes = @(foreach ($file in Get-ChildItem -LiteralPath $staging -Recurse -File -Force | Sort-Object FullName) {
            $path = [IO.Path]::GetRelativePath($staging, $file.FullName)
            $target = Join-Path $ReleaseDir $path
            $hash = Get-Sha256 $file.FullName
            $isNew = -not [IO.File]::Exists($target)
            if ($isNew -or (Get-Sha256 $target) -ne $hash) {
                [pscustomobject]@{ Path = $path; Source = $file.FullName; Target = $target; Hash = $hash; IsNew = $isNew }
            }
        })

        if ($changes.Count -eq 0) {
            Write-Host "$ReleaseDir already matches this build byte for byte. Nothing to swap."
            if ($wasIncomplete -and -not $dryRun) { [IO.File]::Delete($markerPath) }   # whole again
        }
        else {
            Write-Host "$($changes.Count) file(s) differ from $ReleaseDir ($(if ($installedVersion) { $installedVersion } else { 'no installed version' }) -> $builtVersion):"
            foreach ($change in $changes) { Write-Host ("  {0,-7} {1}" -f $(if ($change.IsNew) { 'new' } else { 'changed' }), $change.Path) }

            if ($PSCmdlet.ShouldProcess($ReleaseDir, "Replace $($changes.Count) file(s)")) {
                $asideDir = Join-Path $ReleaseDir ".replaced-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
                if ([IO.Directory]::Exists($asideDir)) { throw "$asideDir already exists; run again in a second" }
                Invoke-Swap $changes $asideDir $builtVersion
                $replaced = @($changes | Where-Object { -not $_.IsNew }).Count
                Write-Host "Swapped $($changes.Count) file(s) ($($changes.Count - $replaced) new)$(if ($replaced) { "; the old copies are in $(Split-Path -Leaf $asideDir)" })."
                Write-Host "$cliExe --version now reports $builtVersion. Running sessions keep the server they started; new sessions get this build."
            }
        }
    }

    if ([IO.Directory]::Exists($ReleaseDir)) {
        $leftovers = @(Get-ChildItem -LiteralPath $ReleaseDir -Force | Where-Object {
            if ($_.PSIsContainer) { $_.Name -like '.replaced-*' -and $_.FullName -ne $asideDir } else { $_.Name -like '*.stale-*' }
        })
        if ($leftovers.Count -gt 0 -and
            $PSCmdlet.ShouldProcess($ReleaseDir, "Delete those of $($leftovers.Count) earlier set-aside copies that no running process still uses")) {
            $kept = @(Remove-Leftovers $leftovers)
            $keptNote = if ($kept.Count) { "; kept $($kept.Count) still in use: $($kept -join ', ')" } else { '' }
            Write-Host "Deleted $($leftovers.Count - $kept.Count) of $($leftovers.Count) earlier set-aside copies$keptNote."
        }
    }
}
catch {
    $host.UI.WriteErrorLine("publish-release: $($_.Exception.Message)")
    exit 1
}
finally {
    if ($lock) { $lock.Dispose() }
    if ($staging -and [IO.Directory]::Exists($staging)) {
        try { [IO.Directory]::Delete($staging, $true) }
        catch { Write-Warning "Could not delete the staging folder $staging ($($_.Exception.Message))." }
    }
}
exit 0
