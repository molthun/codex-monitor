param(
    [switch]$SkipDotNet
)

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
Install-WingetPackage -Id "Rem0o.FanControl" -Name "FanControl"

if (-not $SkipDotNet) {
    $hasSdk10 = $false
    if (Test-Command "dotnet") {
        $hasSdk10 = [bool](dotnet --list-sdks | Select-String -Pattern "^10\.")
    }

    if (-not $hasSdk10) {
        Install-WingetPackage -Id "Microsoft.DotNet.SDK.10" -Name ".NET 10 SDK"
    }
    else {
        Write-Host ""
        Write-Host ".NET 10 SDK is already installed."
    }
}

$fanControlExe = "C:\Program Files (x86)\FanControl\FanControl.exe"
if (Test-Path -LiteralPath $fanControlExe) {
    Write-Host ""
    Write-Host "Starting FanControl..."
    Start-Process -FilePath $fanControlExe
    Start-Sleep -Seconds 3
}

$restoreScript = Join-Path $PSScriptRoot "Install-CodexMonitor.ps1"
Write-Host ""
Write-Host "Restoring CodexMonitor..."
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $restoreScript

Write-Host ""
Write-Host "Done."
