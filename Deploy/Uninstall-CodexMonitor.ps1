param(
    [switch]$RemoveFiles,
    [switch]$KeepRainmeterSkin
)

$ErrorActionPreference = "Stop"
$taskName = "CodexMonitor Bridge Elevated"
$watcherShortcut = Join-Path ([Environment]::GetFolderPath("Startup")) "CodexMonitor Display Watcher.lnk"

Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue | Stop-ScheduledTask -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $watcherShortcut) {
    Remove-Item -LiteralPath $watcherShortcut -Force
}
Get-Process CodexBridge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like "*Watch-PrimaryDisplay.ps1*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

$rainmeterExe = "C:\Program Files\Rainmeter\Rainmeter.exe"
if (Test-Path -LiteralPath $rainmeterExe) {
    & $rainmeterExe !DeactivateConfig "CodexMonitor" "CodexMonitor.ini"
}

if ($RemoveFiles) {
    if (-not $KeepRainmeterSkin) {
        $rainmeterIni = Join-Path $env:APPDATA "Rainmeter\Rainmeter.ini"
        $skinPath = $null
        if (Test-Path -LiteralPath $rainmeterIni) {
            $skinLine = Get-Content -LiteralPath $rainmeterIni | Where-Object { $_ -match "^SkinPath=(.*)$" } | Select-Object -First 1
            if ($skinLine -match "^SkinPath=(.*)$") { $skinPath = $Matches[1] }
        }
        if ($skinPath) {
            $skinDir = Join-Path $skinPath "CodexMonitor"
            if (Test-Path -LiteralPath $skinDir) {
                Remove-Item -LiteralPath $skinDir -Recurse -Force
            }
        }
    }

    if (Test-Path -LiteralPath "C:\CodexMonitor") {
        Remove-Item -LiteralPath "C:\CodexMonitor" -Recurse -Force
    }
}

Write-Host "CodexMonitor scheduled task stopped and removed."
if ($RemoveFiles) { Write-Host "Files removed." }
