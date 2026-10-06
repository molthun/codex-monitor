# CodexMonitor installer for Windows.
#
#   Easiest: download Install-CodexMonitor.cmd from a release on GitHub and double-click it.
#   From PowerShell:
#     irm https://raw.githubusercontent.com/molthun/codex-monitor/main/install.ps1 | iex
#   From a clone:
#     .\install.ps1
#
# Installs a release: its scripts and skin together with the CodexBridge.exe built for it, so they
# always match. The latest release by default; another one (e.g. a beta) with -Version v2.2.0-beta.1
# or $env:CODEXMONITOR_VERSION. From a clone it installs the clone's windows folder instead.
# Asks for administrator rights by itself. Hands off to windows\Deploy\Setup-CodexMonitor.ps1.
# Linux: install.sh.
param([string]$Version = $env:CODEXMONITOR_VERSION)

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

$ErrorActionPreference = "Stop"
$repo = "molthun/codex-monitor"
$Version = "$Version".Trim()

# Self-elevate to Administrator context
$myWindowsID = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$myWindowsPrincipal = New-Object System.Security.Principal.WindowsPrincipal($myWindowsID)
$adminRole = [System.Security.Principal.WindowsBuiltInRole]::Administrator
if (-not $myWindowsPrincipal.IsInRole($adminRole)) {
    $script = $PSCommandPath
    if (-not $script) {
        # Piped through iex: save the installer to a file so it can be started again elevated.
        $script = Join-Path $env:TEMP "codexmonitor-install.ps1"
        $ref = if ($Version) { $Version } else { "main" }
        Invoke-WebRequest -Uri "https://raw.githubusercontent.com/$repo/$ref/install.ps1" -OutFile $script -UseBasicParsing
    }
    Write-Host "Elevating setup bootstrap to Administrator privilege..." -ForegroundColor Yellow
    $newArguments = "-NoProfile -ExecutionPolicy Bypass -File `"$script`""
    if ($Version) { $newArguments += " -Version $Version" }
    Start-Process -FilePath "powershell.exe" -ArgumentList $newArguments -Verb RunAs
    exit
}

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "      CodexMonitor Bootstrap Installer       " -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host ""

function Test-Command {
    param([string]$Name)
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

# Ensure winget is available
if (-not (Test-Command "winget")) {
    Write-Error "winget is not available on this computer. Please install 'App Installer' from the Microsoft Store, then run this setup script again."
    Write-Host "Press any key to exit..."
    [void][System.Console]::ReadKey()
    exit 1
}

$installDir = "C:\CodexMonitor"
$headers = @{ "User-Agent" = "CodexMonitor-Bootstrap" }
$tempZip = Join-Path $env:TEMP "codex-monitor-bootstrap.zip"
$tempExtract = Join-Path $env:TEMP "codex-monitor-bootstrap-extract"

if (Test-Path -LiteralPath $tempZip) { Remove-Item -LiteralPath $tempZip -Force }
if (Test-Path -LiteralPath $tempExtract) { Remove-Item -LiteralPath $tempExtract -Recurse -Force }

$localWindows = if ($PSScriptRoot) { Join-Path $PSScriptRoot "windows" } else { "" }
if ($localWindows -and (Test-Path -LiteralPath (Join-Path $localWindows "Deploy"))) {
    Write-Host "Installing from the local checkout $PSScriptRoot..." -ForegroundColor Yellow
    $windowsDir = $localWindows
}
else {
    $requested = $Version
    $releaseUrl = if ($requested) { "https://api.github.com/repos/$repo/releases/tags/$requested" } else { "https://api.github.com/repos/$repo/releases/latest" }
    $release = Invoke-RestMethod -Uri $releaseUrl -Headers $headers -TimeoutSec 20
    $tag = $release.tag_name
    if (-not ($release.assets | Where-Object { $_.name -eq "CodexBridge.exe" })) {
        Write-Error "Release $tag has no CodexBridge.exe yet (it is built a few minutes after tagging). Try again shortly."
        exit 1
    }
    $zipUrl = "https://github.com/$repo/archive/refs/tags/$tag.zip"
    Write-Host "Installing CodexMonitor $tag..." -ForegroundColor Yellow
    Write-Host "Downloading CodexMonitor repository from $zipUrl..." -ForegroundColor Yellow
    Invoke-WebRequest -Uri $zipUrl -OutFile $tempZip -UseBasicParsing

    Write-Host "Extracting repository source archive..." -ForegroundColor Yellow
    Expand-Archive -Path $tempZip -DestinationPath $tempExtract -Force

    $extractedDir = Get-ChildItem -Path $tempExtract -Directory | Select-Object -First 1
    if (-not $extractedDir) {
        Write-Error "Failed to locate extracted files."
        Write-Host "Press any key to exit..."
        [void][System.Console]::ReadKey()
        exit 1
    }
    # Releases before the move to windows\ keep the Windows files at the archive root.
    $windowsDir = Join-Path $extractedDir.FullName "windows"
    if (-not (Test-Path -LiteralPath (Join-Path $windowsDir "Deploy"))) { $windowsDir = $extractedDir.FullName }
}


# Prepare the complete release before stopping or moving the working installation.
$stage = "$installDir-stage-$([guid]::NewGuid().ToString('N'))"
try {
    New-Item -ItemType Directory -Path $stage | Out-Null
    Get-ChildItem -LiteralPath $windowsDir -Force | Copy-Item -Destination $stage -Recurse -Force
    $payloadExe = Join-Path $stage "Deploy\Payload\CodexBridge\CodexBridge.exe"
    if ($tag) {
        New-Item -ItemType Directory -Force -Path (Split-Path $payloadExe) | Out-Null
        Write-Host "Downloading CodexBridge.exe for $tag..." -ForegroundColor Yellow
        Invoke-WebRequest -Uri "https://github.com/$repo/releases/download/$tag/CodexBridge.exe" -OutFile $payloadExe -UseBasicParsing
    } elseif (-not (Test-Path -LiteralPath $payloadExe)) {
        $builtExe = Join-Path $windowsDir "CodexBridge\bin\Release\net10.0-windows\win-x64\publish\CodexBridge.exe"
        if (Test-Path -LiteralPath $builtExe) {
            Copy-Item -LiteralPath $builtExe -Destination $payloadExe -Force
        } else {
            $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers $headers -TimeoutSec 20
            $tag = $release.tag_name
            Invoke-WebRequest -Uri "https://github.com/$repo/releases/download/$tag/CodexBridge.exe" -OutFile $payloadExe -UseBasicParsing
        }
    }
    if (-not (Test-Path -LiteralPath $payloadExe) -or (Get-Item -LiteralPath $payloadExe).Length -lt 1MB) {
        throw "Prepared CodexBridge.exe is missing or too small."
    }
    $setupScript = Join-Path $stage "Deploy\Setup-CodexMonitor.ps1"
    if (-not (Test-Path -LiteralPath $setupScript)) { throw "Setup launcher was not found." }

    $existingConfig = Join-Path $installDir "config.json"
    $taskName = "CodexMonitor Bridge Elevated"
    if (Test-Path -LiteralPath $existingConfig) {
        $oldConfig = Get-Content -LiteralPath $existingConfig -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($oldConfig.bridge.taskName) { $taskName = $oldConfig.bridge.taskName }
    }
    $stopInstalledProcesses = {
        Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue | Stop-ScheduledTask -ErrorAction SilentlyContinue
        Get-Process CodexBridge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -like "*Watch-PrimaryDisplay.ps1*" } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Seconds 1
    }
    # Embedded below because this bootstrap also installs older tags without the helper file.
    Invoke-CodexInstallTransaction -InstallRoot $installDir -StageRoot $stage -Stop {
        & $stopInstalledProcesses
        if ((Test-Path -LiteralPath $stage) -and (Test-Path -LiteralPath $existingConfig)) {
            Copy-Item -LiteralPath $existingConfig -Destination (Join-Path $stage "config.json") -Force
        }
    } -Apply {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $installDir "Deploy\Setup-CodexMonitor.ps1")
        if ($LASTEXITCODE -ne 0) { throw "Setup failed with exit code $LASTEXITCODE." }
        if ($tag) { Set-Content -LiteralPath (Join-Path $installDir ".local_version") -Value $tag -Encoding UTF8 }
    } -Recover {
        if (Test-Path -LiteralPath (Join-Path $installDir "Deploy\Install-CodexMonitor.ps1")) {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $installDir "Deploy\Install-CodexMonitor.ps1")
            if ($LASTEXITCODE -ne 0) { throw "Restoring the previous deployment failed." }
        }
    }
} finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    if (Test-Path -LiteralPath $tempZip) { Remove-Item -LiteralPath $tempZip -Force }
    if (Test-Path -LiteralPath $tempExtract) { Remove-Item -LiteralPath $tempExtract -Recurse -Force }
}
