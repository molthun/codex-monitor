# StageRoot must contain a complete installation on the same volume as InstallRoot.
# Downloading and validation happen before entering this transaction.
function Invoke-CodexInstallTransaction {
    param(
        [Parameter(Mandatory)][string]$InstallRoot,
        [Parameter(Mandatory)][string]$StageRoot,
        [Parameter(Mandatory)][scriptblock]$Stop,
        [Parameter(Mandatory)][scriptblock]$Apply,
        [Parameter(Mandatory)][scriptblock]$Recover
    )

    $ErrorActionPreference = "Stop"
    if (-not (Test-Path -LiteralPath $StageRoot -PathType Container)) {
        throw "Prepared installation was not found: $StageRoot"
    }
    $backup = "$InstallRoot-backup-$([guid]::NewGuid().ToString('N'))"
    $movedOld = $false
    $movedNew = $false
    try {
        & $Stop
        if (Test-Path -LiteralPath $InstallRoot) {
            Move-Item -LiteralPath $InstallRoot -Destination $backup
            $movedOld = $true
        }
        Move-Item -LiteralPath $StageRoot -Destination $InstallRoot
        $movedNew = $true
        & $Apply
    }
    catch {
        $failure = $_
        # Stop any new processes before moving their executables out of the way.
        try { & $Stop } catch { Write-Warning "Could not stop the failed installation: $_" }
        if ($movedNew) {
            Move-Item -LiteralPath $InstallRoot -Destination $StageRoot
        }
        if ($movedOld) {
            Move-Item -LiteralPath $backup -Destination $InstallRoot
        }
        try { & $Recover } catch { Write-Warning "Files restored; process recovery failed: $_" }
        throw $failure
    }
    # Keep the previous working installation for manual recovery.
    if ($movedOld) { Write-Host "Previous installation kept at $backup" }
}
