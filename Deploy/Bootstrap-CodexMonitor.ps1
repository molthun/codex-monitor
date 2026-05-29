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

# Install Git if not present
if (-not (Test-Command "git")) {
    Write-Host "Git is not installed. Installing Git via winget..." -ForegroundColor Yellow
    & winget install --id Git.Git --exact --accept-source-agreements --accept-package-agreements --silent
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "winget failed to install Git. Please install Git manually."
    }
    
    # Append Git cmd directory to current session's PATH
    $gitCmdPath = "C:\Program Files\Git\cmd"
    if (Test-Path -LiteralPath $gitCmdPath) {
        $env:PATH += ";$gitCmdPath"
    }
} else {
    Write-Host "Git is already installed." -ForegroundColor Green
}

if (-not (Test-Command "git")) {
    Write-Error "Git could not be verified on the PATH. Please install Git and try again."
    Write-Host "Press any key to exit..."
    [void][System.Console]::ReadKey()
    exit 1
}

$installDir = "C:\CodexMonitor"
$repoUrl = "https://github.com/molthun/codex-monitor.git"

if (-not (Test-Path -LiteralPath $installDir)) {
    Write-Host "Cloning CodexMonitor repository into $installDir..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    & git clone $repoUrl $installDir
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to clone repository from $repoUrl."
        Write-Host "Press any key to exit..."
        [void][System.Console]::ReadKey()
        exit 1
    }
    Write-Host "Repository cloned successfully!" -ForegroundColor Green
} else {
    Write-Host "$installDir already exists. Verifying repository..." -ForegroundColor Yellow
    if (Test-Path -LiteralPath (Join-Path $installDir ".git")) {
        Write-Host "Pulling latest updates..." -ForegroundColor Green
        & git -C $installDir pull
    } else {
        Write-Warning "Directory $installDir exists but is not a Git repository."
        Write-Host "Cloning repository..." -ForegroundColor Yellow
        # Back up existing files
        $backupDir = "$installDir-backup-$(Get-Date -Format 'yyyyMMddHHmmss')"
        Rename-Item -Path $installDir -NewName (Split-Path $backupDir -Leaf)
        & git clone $repoUrl $installDir
    }
}

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
