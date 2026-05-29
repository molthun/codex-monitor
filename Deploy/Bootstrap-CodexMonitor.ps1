# CodexMonitor GitHub Bootstrap Launcher
# This script ensures Git is installed, clones the repository, and runs the main setup.

$ErrorActionPreference = "Stop"

# Self-elevate to Administrator context
$myWindowsID = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$myWindowsPrincipal = New-Object System.Security.Principal.WindowsPrincipal($myWindowsID)
$adminRole = [System.Security.Principal.WindowsBuiltInRole]::Administrator
if (-not $myWindowsPrincipal.IsInRole($adminRole)) {
    Write-Host "Elevating setup bootstrap to Administrator privilege..." -ForegroundColor Yellow
    $newArguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
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
$zipUrl = "https://github.com/molthun/codex-monitor/archive/refs/heads/main.zip"
$tempZip = Join-Path $env:TEMP "codex-monitor-bootstrap.zip"
$tempExtract = Join-Path $env:TEMP "codex-monitor-bootstrap-extract"

if (Test-Path -LiteralPath $tempZip) { Remove-Item -LiteralPath $tempZip -Force }
if (Test-Path -LiteralPath $tempExtract) { Remove-Item -LiteralPath $tempExtract -Recurse -Force }

if (Test-Path -LiteralPath $installDir) {
    Write-Host "$installDir already exists. Backing up existing folder..." -ForegroundColor Yellow
    $backupDir = "$installDir-backup-$(Get-Date -Format 'yyyyMMddHHmmss')"
    Rename-Item -Path $installDir -NewName (Split-Path $backupDir -Leaf)
}

Write-Host "Downloading CodexMonitor repository from $zipUrl..." -ForegroundColor Yellow
Invoke-WebRequest -Uri $zipUrl -OutFile $tempZip -UseBasicParsing

Write-Host "Extracting repository source archive..." -ForegroundColor Yellow
Expand-Archive -Path $tempZip -DestinationPath $tempExtract -Force

$extractedDir = Join-Path $tempExtract "codex-monitor-main"
if (-not (Test-Path -LiteralPath $extractedDir)) {
    $extractedDir = Get-ChildItem -Path $tempExtract -Directory | Select-Object -First 1
    if (-not $extractedDir) {
        Write-Error "Failed to locate extracted files."
        Write-Host "Press any key to exit..."
        [void][System.Console]::ReadKey()
        exit 1
    }
    $extractedDir = $extractedDir.FullName
}

New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Copy-Item -LiteralPath "$extractedDir\*" -Destination $installDir -Recurse -Force

# Fetch latest remote SHA from GitHub API and write to C:\CodexMonitor\.local_version
try {
    Write-Host "Fetching remote version signature from GitHub API..." -ForegroundColor Yellow
    $headers = @{ "User-Agent" = "CodexMonitor-Bootstrap" }
    $response = Invoke-RestMethod -Uri "https://api.github.com/repos/molthun/codex-monitor/commits/main" -Headers $headers -TimeoutSec 10
    $remoteSha = $response.sha
    if ($remoteSha) {
        Set-Content -LiteralPath (Join-Path $installDir ".local_version") -Value $remoteSha.Trim() -Encoding UTF8
    }
} catch {
    Write-Warning "Failed to query latest SHA signature: $_. Version tracking will initialize on the next run."
}

# Clean up temp files
Remove-Item -LiteralPath $tempZip -Force
Remove-Item -LiteralPath $tempExtract -Recurse -Force

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
