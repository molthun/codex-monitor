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
$configChanged = $false
if (-not $config.installRoot -or $config.installRoot -ne $projectRoot) {
    $config.installRoot = $projectRoot
    $configChanged = $true
}
$expectedOutFile = Join-Path $projectRoot "@Resources\temps.txt"
if (-not $config.bridge -or -not $config.bridge.outputFile -or $config.bridge.outputFile -ne $expectedOutFile) {
    if (-not $config.bridge) { $config | Add-Member -NotePropertyName "bridge" -NotePropertyValue @{} -Force }
    $config.bridge.outputFile = $expectedOutFile
    $configChanged = $true
}

if ($configChanged) {
    $jsonString = ConvertTo-Json -InputObject $config -Depth 10
    [System.IO.File]::WriteAllText($configPath, $jsonString, [System.Text.Encoding]::UTF8)
}

# Locate CodexBridge.exe
$bridgeExe = Join-Path $projectRoot "CodexBridge\CodexBridge.exe"
if (-not (Test-Path -LiteralPath $bridgeExe)) {
    $bridgeExe = Join-Path $projectRoot "Deploy\Payload\CodexBridge\CodexBridge.exe"
}

if (-not (Test-Path -LiteralPath $bridgeExe)) {
    # Fallback to search in bin/Release/publish or similar
    $devPath = Get-ChildItem -Path $projectRoot -Filter "CodexBridge.exe" -Recurse -File -ErrorAction SilentlyContinue | 
        Where-Object { $_.FullName -like "*publish*" } | Select-Object -First 1
    if ($devPath) {
        $bridgeExe = $devPath.FullName
    }
}

if (-not (Test-Path -LiteralPath $bridgeExe)) {
    Write-Error "CodexBridge.exe was not found. Please make sure the project is compiled or installed."
    exit 1
}

Write-Host "Launching Graphical Settings Wizard..." -ForegroundColor Cyan
# Launch CodexBridge.exe in settings mode and wait for it to exit
$process = Start-Process -FilePath $bridgeExe -ArgumentList "--settings", "--config `"$configPath`"" -Wait -NoNewWindow -PassThru

if ($process.ExitCode -ne 0) {
    Write-Warning "Settings wizard exited with code $($process.ExitCode)."
} else {
    Write-Host "Configuration completed successfully!" -ForegroundColor Green
}
