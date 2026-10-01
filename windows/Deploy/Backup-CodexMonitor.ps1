param(
    [string]$BackupRoot = "C:\CodexMonitor\Backups"
)

$ErrorActionPreference = "Stop"

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupDir = Join-Path $BackupRoot "CodexMonitor-$stamp"
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

$items = @(
    "C:\CodexMonitor\CodexMonitor.ini",
    "C:\CodexMonitor\CodexBridge\Program.cs",
    "C:\CodexMonitor\CodexBridge\CodexBridge.csproj",
    "C:\CodexMonitor\@Resources\temps.txt",
    "$env:APPDATA\Rainmeter\Rainmeter.ini"
)

foreach ($item in $items) {
    if (Test-Path -LiteralPath $item) {
        $target = Join-Path $backupDir ($item -replace "[:\\]", "_")
        Copy-Item -LiteralPath $item -Destination $target -Force
    }
}

$skinPath = $null
$rainmeterIni = Join-Path $env:APPDATA "Rainmeter\Rainmeter.ini"
if (Test-Path -LiteralPath $rainmeterIni) {
    $skinLine = Get-Content -LiteralPath $rainmeterIni | Where-Object { $_ -match "^SkinPath=(.*)$" } | Select-Object -First 1
    if ($skinLine -match "^SkinPath=(.*)$") { $skinPath = $Matches[1] }
}

if ($skinPath) {
    $skinIni = Join-Path $skinPath "CodexMonitor\CodexMonitor.ini"
    if (Test-Path -LiteralPath $skinIni) {
        Copy-Item -LiteralPath $skinIni -Destination (Join-Path $backupDir "ActiveSkin-CodexMonitor.ini") -Force
    }
}

schtasks /Query /TN "CodexMonitor Bridge Elevated" /XML > (Join-Path $backupDir "CodexMonitor Bridge Elevated.xml") 2>$null

$zip = "$backupDir.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -LiteralPath $backupDir -DestinationPath $zip -Force

Write-Host "Backup created:"
Write-Host $zip
