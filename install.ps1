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

$backupDir = $null
if (Test-Path -LiteralPath $installDir) {
    Write-Host "$installDir already exists. Backing up existing folder..." -ForegroundColor Yellow
    # A running bridge, tray icon or watcher keeps files open, and Windows refuses to rename the folder.
    Get-ScheduledTask -TaskName "CodexMonitor Bridge Elevated" -ErrorAction SilentlyContinue | Stop-ScheduledTask -ErrorAction SilentlyContinue
    Get-Process CodexBridge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*Watch-PrimaryDisplay.ps1*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
    $backupDir = "$installDir-backup-$(Get-Date -Format 'yyyyMMddHHmmss')"
    Rename-Item -Path $installDir -NewName (Split-Path $backupDir -Leaf)
}

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

New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Copy-Item -Path "$windowsDir\*" -Destination $installDir -Recurse -Force

# Keep the user's settings across a reinstall.
if ($backupDir -and (Test-Path -LiteralPath (Join-Path $backupDir "config.json"))) {
    Copy-Item -LiteralPath (Join-Path $backupDir "config.json") -Destination (Join-Path $installDir "config.json") -Force
    Write-Host "Kept your settings from the previous installation." -ForegroundColor Green
}

if ($tag) {
    # The bridge built for this release, and the version the auto-updater compares against.
    $payloadExe = Join-Path $installDir "Deploy\Payload\CodexBridge\CodexBridge.exe"
    New-Item -ItemType Directory -Force -Path (Split-Path $payloadExe) | Out-Null
    Write-Host "Downloading CodexBridge.exe for $tag..." -ForegroundColor Yellow
    Invoke-WebRequest -Uri "https://github.com/$repo/releases/download/$tag/CodexBridge.exe" -OutFile $payloadExe -UseBasicParsing
    Set-Content -LiteralPath (Join-Path $installDir ".local_version") -Value $tag -Encoding UTF8
}

# Clean up temp files
if (Test-Path -LiteralPath $tempZip) { Remove-Item -LiteralPath $tempZip -Force }
if (Test-Path -LiteralPath $tempExtract) { Remove-Item -LiteralPath $tempExtract -Recurse -Force }

Write-Host "Repository downloaded and staged at $installDir!" -ForegroundColor Green

# Run the setup script in the cloned directory
$setupScript = Join-Path $installDir "Deploy\Setup-CodexMonitor.ps1"
if (Test-Path -LiteralPath $setupScript) {
    Write-Host "Handing off control to the setup launcher..." -ForegroundColor Cyan
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $setupScript
} else {
    Write-Error "Setup launcher script not found at $setupScript."
    Write-Host "Press any key to exit..."
    [void][System.Console]::ReadKey()
    exit 1
}
