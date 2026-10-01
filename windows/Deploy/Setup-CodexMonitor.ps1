# CodexMonitor Self-Elevating Onboarding Installer
# This script installs all prerequisites, runs the wizard, and deploys the widget.

$ErrorActionPreference = "Stop"

# Self-elevate to Administrator context
$myWindowsID = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$myWindowsPrincipal = New-Object System.Security.Principal.WindowsPrincipal($myWindowsID)
$adminRole = [System.Security.Principal.WindowsBuiltInRole]::Administrator
if (-not $myWindowsPrincipal.IsInRole($adminRole)) {
    Write-Host "Elevating setup to Administrator privilege..." -ForegroundColor Yellow
    $newArguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    Start-Process -FilePath "powershell.exe" -ArgumentList $newArguments -Verb RunAs
    exit
}

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "      CodexMonitor Dependency Installer      " -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host ""

function Test-Command {
    param([string]$Name)
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

function Install-WingetPackage {
    param(
        [string]$Id,
        [string]$Name
    )

    Write-Host "Checking/Installing $Name..." -ForegroundColor Yellow
    # Accept agreements and run installer silently
    & winget install --id $Id --exact --accept-source-agreements --accept-package-agreements --silent
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "winget failed to install $Name ($Id). Please install it manually if not already present."
    } else {
        Write-Host "Successfully installed $Name!" -ForegroundColor Green
    }
}

# Ensure winget is available
if (-not (Test-Command "winget")) {
    Write-Error "winget is not available on this computer. Please install 'App Installer' from the Microsoft Store, then run this setup script again."
    Write-Host "Press any key to exit..."
    [void][System.Console]::ReadKey()
    exit 1
}

# Record the latest release tag as the version baseline for the auto-updater.
$projectRoot = Split-Path -Parent $PSScriptRoot
try {
    $headers = @{ "User-Agent" = "CodexMonitor-Setup" }
    $response = Invoke-RestMethod -Uri "https://api.github.com/repos/molthun/codex-monitor/releases/latest" -Headers $headers -TimeoutSec 10
    $remoteTag = $response.tag_name
    if ($remoteTag) {
        $versionFile = Join-Path $projectRoot ".local_version"
        Set-Content -LiteralPath $versionFile -Value $remoteTag.Trim() -Encoding UTF8
        Write-Host "Initialized .local_version with latest release tag $remoteTag." -ForegroundColor Green
    }
} catch {}

Install-WingetPackage -Id "Rainmeter.Rainmeter" -Name "Rainmeter"


Write-Host ""
Write-Host "CodexMonitor reads hardware sensors directly through LibreHardwareMonitor and does not control fan behavior." -ForegroundColor Green

# Run the Configuration Wizard
$wizardScript = Join-Path $PSScriptRoot "Configure-CodexMonitor.ps1"
if (Test-Path -LiteralPath $wizardScript) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $wizardScript
}

# Run the main Installer to deploy skins and tasks
$installScript = Join-Path $PSScriptRoot "Install-CodexMonitor.ps1"
if (Test-Path -LiteralPath $installScript) {
    Write-Host "Deploying CodexMonitor widget and registering background tasks..." -ForegroundColor Cyan
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installScript
}

Write-Host ""
Write-Host "=============================================" -ForegroundColor Green
Write-Host "CodexMonitor Installation and Setup Complete!" -ForegroundColor Green
Write-Host "=============================================" -ForegroundColor Green
Write-Host ""
Write-Host "Press any key to exit..."
[void][System.Console]::ReadKey()
