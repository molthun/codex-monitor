# CodexMonitor Configuration Wizard
# This script guides the user through setting up the config.json for their specific computer hardware.

$ErrorActionPreference = "Stop"

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "      CodexMonitor Configuration Wizard      " -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host ""

# Get the script root and config paths
$projectRoot = Split-Path -Parent $PSScriptRoot
$configPath = Join-Path $projectRoot "config.json"
$examplePath = Join-Path $projectRoot "config.example.json"

# Load base configuration
if (Test-Path -LiteralPath $configPath) {
    Write-Host "Loading existing configuration..." -ForegroundColor Green
    $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
} elseif (Test-Path -LiteralPath $examplePath) {
    Write-Host "No config.json found. Creating new configuration from example template..." -ForegroundColor Yellow
    $config = Get-Content -LiteralPath $examplePath -Raw -Encoding UTF8 | ConvertFrom-Json
} else {
    Write-Error "Configuration template config.example.json not found in $projectRoot."
    exit 1
}

# Update default paths based on current repository location
$config.installRoot = $projectRoot
$config.bridge.outputFile = Join-Path $projectRoot "@Resources\temps.txt"

# 1. Scale Profile Mode
Write-Host ""
Write-Host "1. Select Widget Scaling Mode:" -ForegroundColor White
Write-Host "   [1] Auto (Detects based on primary display height, threshold 1440px) [Recommended]"
Write-Host "   [2] 1080p (Compact size)"
Write-Host "   [3] 4K (Large size)"
$profileChoice = Read-Host "Enter option [1-3] (Default: 1)"
switch ($profileChoice) {
    "2" {
        $config.profiles.default = "1080p"
        $config.profiles.auto = $false
    }
    "3" {
        $config.profiles.default = "4K"
        $config.profiles.auto = $false
    }
    default {
        $config.profiles.default = "Auto"
        $config.profiles.auto = $true
    }
}
Write-Host "Selected Mode: $($config.profiles.default)" -ForegroundColor Green

# 2. Disk Selection
Write-Host ""
Write-Host "2. Scanning local disk drives..." -ForegroundColor White
$availableDrives = Get-PSDrive -PSProvider FileSystem | Sort-Object Name
$driveLetters = @()
foreach ($d in $availableDrives) {
    if ($d.Name.Length -eq 1) {
        $driveLetters += "$($d.Name):"
    }
}

Write-Host "Detected drives: $($driveLetters -join ", ")"
Write-Host "Enter the drive letters you want to display on the widget (comma-separated, e.g. C:, D:)"
$selectedDrivesInput = Read-Host "Selected drives (Default: C:, D:)"
if ($selectedDrivesInput.Trim() -ne "") {
    $selectedDrives = $selectedDrivesInput.Split(",") | ForEach-Object { $_.Trim().ToUpper() } | Where-Object { $_ -ne "" }
    # Ensure they have trailing colons
    $formattedDrives = @()
    foreach ($sd in $selectedDrives) {
        if ($sd.EndsWith(":")) { $formattedDrives += $sd } else { $formattedDrives += "$sd`:" }
    }
    $config.disks = $formattedDrives
} else {
    # Default to C: and D: if present, or whatever exists
    $defaultDrives = @()
    if ($driveLetters -contains "C:") { $defaultDrives += "C:" }
    if ($driveLetters -contains "D:") { $defaultDrives += "D:" }
    if ($defaultDrives.Count -eq 0) { $defaultDrives = $driveLetters[0..1] }
    $config.disks = $defaultDrives
}
Write-Host "Configured disks: $($config.disks -join ", ")" -ForegroundColor Green

# 3. Network Interfaces Exclusions (Optional Customization)
Write-Host ""
Write-Host "3. Network Interfaces:" -ForegroundColor White
$adapters = Get-NetAdapter -ErrorAction SilentlyContinue
if ($adapters) {
    Write-Host "Detected active adapters:"
    foreach ($a in $adapters) {
        Write-Host "   - Name: $($a.Name) (Status: $($a.Status))"
    }
}
$excludeInput = Read-Host "Would you like to exclude virtual/WSL adapters? [Y/n] (Default: Y)"
if ($excludeInput.Trim().ToLower() -eq "n") {
    $config.network.ignoreAdaptersContaining = @()
} else {
    $config.network.ignoreAdaptersContaining = @("hyper-v", "virtual switch", "wsl", "teredo", "wan miniport", "bluetooth")
}

# 4. Auto-Updater Enable
Write-Host ""
Write-Host "4. Auto-Update Settings:" -ForegroundColor White
$updateInput = Read-Host "Enable background automatic Git updates? [Y/n] (Default: Y)"
if ($updateInput.Trim().ToLower() -eq "n") {
    $config.display | Add-Member -NotePropertyName "autoUpdate" -NotePropertyValue $false -Force
    Write-Host "Auto-updates: Disabled" -ForegroundColor Yellow
} else {
    $config.display | Add-Member -NotePropertyName "autoUpdate" -NotePropertyValue $true -Force
    Write-Host "Auto-updates: Enabled" -ForegroundColor Green
}

# Write back to config.json
$jsonString = ConvertTo-Json -InputObject $config -Depth 10
# Format the JSON nicely (PowerShell's default depth formatting is fine, but let's make it pretty)
[System.IO.File]::WriteAllText($configPath, $jsonString, [System.Text.Encoding]::UTF8)

Write-Host ""
Write-Host "=============================================" -ForegroundColor Green
Write-Host "Configuration saved to: $configPath" -ForegroundColor Green
Write-Host "=============================================" -ForegroundColor Green
Write-Host ""
