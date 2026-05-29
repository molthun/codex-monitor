$ErrorActionPreference = "Stop"

function Test-Command {
    param([string]$Name)
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

function Install-WingetPackage {
    param(
        [string]$Id,
        [string]$Name
    )

    Write-Host ""
    Write-Host "Installing/checking $Name ($Id)..."
    winget install --id $Id --exact --accept-source-agreements --accept-package-agreements
}

if (-not (Test-Command "winget")) {
    throw "winget is not available. Install App Installer from Microsoft Store first, then run this script again."
}

Install-WingetPackage -Id "Rainmeter.Rainmeter" -Name "Rainmeter"

Write-Host ""
Write-Host "CodexMonitor reads sensor telemetry directly through LibreHardwareMonitor and does not control fan behavior."

$restoreScript = Join-Path $PSScriptRoot "Install-CodexMonitor.ps1"
Write-Host ""
Write-Host "Restoring CodexMonitor..."
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $restoreScript

Write-Host ""
Write-Host "Done."
