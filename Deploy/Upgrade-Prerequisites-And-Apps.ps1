param(
    [switch]$Auto
)

$ErrorActionPreference = "Stop"

# Self-elevate to Administrator context
$myWindowsID = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$myWindowsPrincipal = New-Object System.Security.Principal.WindowsPrincipal($myWindowsID)
$adminRole = [System.Security.Principal.WindowsBuiltInRole]::Administrator
if (-not $myWindowsPrincipal.IsInRole($adminRole)) {
    Write-Host "Elevating script to Administrator privilege..." -ForegroundColor Yellow
    $newArguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($Auto) { $newArguments += " -Auto" }
    Start-Process -FilePath "powershell.exe" -ArgumentList $newArguments -Verb RunAs
    exit
}

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "      CodexMonitor Dependency Upgrader       " -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host ""

# Get config
$projectRoot = Split-Path -Parent $PSScriptRoot
$configPath = Join-Path $projectRoot "config.json"
$config = if (Test-Path -LiteralPath $configPath) {
    Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
} else {
    [pscustomobject]@{}
}

function Test-Command {
    param([string]$Name)
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

if (-not (Test-Command "winget")) {
    Write-Error "winget is not available on this computer."
    Write-Host "Press any key to exit..."
    [void][System.Console]::ReadKey()
    exit 1
}

# 1. Stop active processes to prevent file locking
Write-Host "Stopping CodexBridge and Rainmeter if running..." -ForegroundColor Yellow

# Track if they were running so we can restart them
$wasRainmeterRunning = [bool](Get-Process Rainmeter -ErrorAction SilentlyContinue)

# Stop bridge task and processes
Get-ScheduledTask -TaskName "CodexMonitor Bridge Elevated" -ErrorAction SilentlyContinue | Stop-ScheduledTask -ErrorAction SilentlyContinue
Stop-Process -Name "CodexBridge" -Force -ErrorAction SilentlyContinue
Stop-Process -Name "Rainmeter" -Force -ErrorAction SilentlyContinue

# Terminate startup watcher if running
Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like "*Watch-PrimaryDisplay.ps1*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

Start-Sleep -Seconds 2

# 2. Perform Winget Upgrades
function Upgrade-App {
    param(
        [string]$Id,
        [string]$Name
    )

    Write-Host "Checking/Upgrading $Name ($Id)..." -ForegroundColor Yellow
    # Accept agreements and run upgrade
    & winget upgrade --id $Id --exact --accept-source-agreements --accept-package-agreements --silent
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Successfully processed $Name!" -ForegroundColor Green
    } elseif ($LASTEXITCODE -eq -2145898557) {
        Write-Host "$Name is already up-to-date." -ForegroundColor Green
    } else {
        Write-Warning "winget processed $Name with exit code $LASTEXITCODE. It may already be up-to-date or require manual action."
    }
}

Upgrade-App -Id "Rainmeter.Rainmeter" -Name "Rainmeter"
Upgrade-App -Id "Microsoft.DotNet.SDK.10" -Name ".NET 10 SDK"

# 3. Restart processes
Write-Host ""
Write-Host "Restarting applications..." -ForegroundColor Green

# Restart Bridge Scheduled Task
schtasks.exe /run /tn "CodexMonitor Bridge Elevated" | Out-Null
Write-Host "Started CodexBridge background task." -ForegroundColor Green

# Restart Rainmeter
$rainmeterExe = if ($config.rainmeter.executable) { $config.rainmeter.executable } else { "C:\Program Files\Rainmeter\Rainmeter.exe" }
if ($wasRainmeterRunning -and (Test-Path -LiteralPath $rainmeterExe)) {
    Start-Process -FilePath $rainmeterExe
    Write-Host "Started Rainmeter." -ForegroundColor Green
}

# Restart Display Watcher
$watcherScript = Join-Path $projectRoot "Watch-PrimaryDisplay.ps1"
if (Test-Path -LiteralPath $watcherScript) {
    Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$watcherScript`" -ConfigPath `"$configPath`"" -WindowStyle Hidden
    Write-Host "Started Display Watcher." -ForegroundColor Green
}

Write-Host ""
Write-Host "=============================================" -ForegroundColor Green
Write-Host "   Dependencies Upgraded & Widgets Restored  " -ForegroundColor Green
Write-Host "=============================================" -ForegroundColor Green
Write-Host ""
if (-not $Auto) {
    Write-Host "Press any key to exit..."
    [void][System.Console]::ReadKey()
}
