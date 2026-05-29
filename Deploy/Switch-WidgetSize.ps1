param(
    [ValidateSet("Auto", "1080p", "4K")]
    [string]$Mode = "Auto",
    [string]$InstallRoot = "C:\CodexMonitor",
    [string]$ConfigPath = ""
)

$ErrorActionPreference = "Stop"

function Read-CodexConfig {
    param([string]$Path)
    $projectRoot = Split-Path -Parent $PSScriptRoot
    $configPath = if ($Path) { $Path } else { Join-Path $projectRoot "config.json" }
    $examplePath = Join-Path $projectRoot "config.example.json"
    if (Test-Path -LiteralPath $configPath) { return Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    if ((-not $Path) -and (Test-Path -LiteralPath $examplePath)) { return Get-Content -LiteralPath $examplePath -Raw -Encoding UTF8 | ConvertFrom-Json }
    return [pscustomobject]@{}
}

$config = Read-CodexConfig -Path $ConfigPath
if ($config.installRoot -and $InstallRoot -eq "C:\CodexMonitor") { $InstallRoot = $config.installRoot }
if ($Mode -eq "Auto" -and $config.profiles.default -and $config.profiles.default -ne "Auto") { $Mode = $config.profiles.default }

function Get-RainmeterSkinPath {
    if ($config.rainmeter.skinPath) { return $config.rainmeter.skinPath }
    $rainmeterIni = Join-Path $env:APPDATA "Rainmeter\Rainmeter.ini"
    if (Test-Path -LiteralPath $rainmeterIni) {
        $line = Get-Content -LiteralPath $rainmeterIni | Where-Object { $_ -match "^SkinPath=(.*)$" } | Select-Object -First 1
        if ($line -match "^SkinPath=(.*)$") { return $Matches[1] }
    }

    return Join-Path ([Environment]::GetFolderPath("MyDocuments")) "Rainmeter\Skins\"
}

function Set-IniKey {
    param(
        [System.Collections.Generic.List[string]]$Lines,
        [string]$Section,
        [string]$Key,
        [string]$Value
    )

    $sectionHeader = "[$Section]"
    $sectionIndex = -1
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i].Trim() -ieq $sectionHeader) {
            $sectionIndex = $i
            break
        }
    }

    if ($sectionIndex -lt 0) {
        if ($Lines.Count -gt 0 -and $Lines[$Lines.Count - 1].Trim() -ne "") { $Lines.Add("") }
        $Lines.Add($sectionHeader)
        $Lines.Add("$Key=$Value")
        return
    }

    $nextSection = $Lines.Count
    for ($i = $sectionIndex + 1; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i] -match "^\s*\[.+\]\s*$") {
            $nextSection = $i
            break
        }
    }

    for ($i = $sectionIndex + 1; $i -lt $nextSection; $i++) {
        if ($Lines[$i] -match "^\s*$([regex]::Escape($Key))=") {
            $Lines[$i] = "$Key=$Value"
            return
        }
    }

    $Lines.Insert($nextSection, "$Key=$Value")
}

function Get-WidgetWidth {
    param([string]$Path)
    $line = Get-Content -LiteralPath $Path | Where-Object { $_ -match "^W=(\d+)" } | Select-Object -First 1
    if ($line -match "^W=(\d+)") { return [int]$Matches[1] }
    return 430
}

function Set-PrimaryMonitorPosition {
    param([string]$SkinIni)

    Add-Type -AssemblyName System.Windows.Forms
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $widgetWidth = Get-WidgetWidth -Path $SkinIni
    $marginRight = if ($config.display.marginRight -ne $null) { [int]$config.display.marginRight } else { 24 }
    $marginTop = if ($config.display.marginTop -ne $null) { [int]$config.display.marginTop } else { 24 }
    $x = [int]($screen.X + $screen.Width - $widgetWidth - $marginRight)
    $Y = [int]($screen.Y + $marginTop)
    $rainmeterIni = Join-Path $env:APPDATA "Rainmeter\Rainmeter.ini"

    if (Test-Path -LiteralPath $rainmeterIni) {
        $lines = [System.Collections.Generic.List[string]]::new()
        foreach ($line in (Get-Content -LiteralPath $rainmeterIni)) { $lines.Add($line) }
        Set-IniKey -Lines $lines -Section "CodexMonitor" -Key "WindowX" -Value $x
        Set-IniKey -Lines $lines -Section "CodexMonitor" -Key "WindowY" -Value $y
        Set-IniKey -Lines $lines -Section "CodexMonitor" -Key "AnchorX" -Value 0
        Set-IniKey -Lines $lines -Section "CodexMonitor" -Key "AnchorY" -Value 0
        Set-IniKey -Lines $lines -Section "CodexMonitor" -Key "AutoSelectScreen" -Value 0
        Set-Content -LiteralPath $rainmeterIni -Value $lines -Encoding Unicode
    }

    return @{ X = $x; Y = $y; Width = $screen.Width; Height = $screen.Height }
}

$packageRoot = $PSScriptRoot
$resolvedMode = $Mode
if ($Mode -eq "Auto") {
    Add-Type -AssemblyName System.Windows.Forms
    $height = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height
    $threshold = if ($config.display.autoProfileHeightThreshold -ne $null) { [int]$config.display.autoProfileHeightThreshold } else { 1440 }
    $largeProfile = if ($config.profiles.large) { $config.profiles.large } else { "4K" }
    $compactProfile = if ($config.profiles.compact) { $config.profiles.compact } else { "1080p" }
    $resolvedMode = if ($height -ge $threshold) { $largeProfile } else { $compactProfile }
    Write-Host "Primary screen height: $height px. Auto-selected: $resolvedMode."
}

$presetName = if ($resolvedMode -eq "4K") { "CodexMonitor.4K.ini" } else { "CodexMonitor.1080p.ini" }
$presetCandidates = @(
    (Join-Path $InstallRoot "Presets\$presetName"),
    (Join-Path $packageRoot "Payload\RainmeterSkin\CodexMonitor\$presetName")
)

$preset = $presetCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $preset) {
    throw "Preset not found: $presetName"
}

$skinPath = Get-RainmeterSkinPath
$skinTarget = Join-Path $skinPath "CodexMonitor\CodexMonitor.ini"

Copy-Item -LiteralPath $preset -Destination $skinTarget -Force

# Automatically write BridgeExe variable and Context Menu items for easy settings GUI invocation
$lines = [System.Collections.Generic.List[string]]::new()
foreach ($line in (Get-Content -LiteralPath $skinTarget)) { $lines.Add($line) }
$bridgeExePath = Join-Path $InstallRoot "CodexBridge\CodexBridge.exe"
Set-IniKey -Lines $lines -Section "Variables" -Key "BridgeExe" -Value "`"$bridgeExePath`""
Set-IniKey -Lines $lines -Section "Rainmeter" -Key "ContextTitle" -Value "Configure CodexMonitor"
Set-IniKey -Lines $lines -Section "Rainmeter" -Key "ContextAction" -Value "[`"#BridgeExe#`" --settings]"
Set-Content -LiteralPath $skinTarget -Value $lines -Encoding UTF8

$position = Set-PrimaryMonitorPosition -SkinIni $skinTarget

$rainmeterExe = if ($config.rainmeter.executable) { $config.rainmeter.executable } else { "C:\Program Files\Rainmeter\Rainmeter.exe" }
if (Test-Path -LiteralPath $rainmeterExe) {
    & $rainmeterExe !Refresh "CodexMonitor"
    & $rainmeterExe !Move $position.X $position.Y "CodexMonitor"
}

Write-Host "CodexMonitor widget size switched to $resolvedMode."
Write-Host "Primary monitor: $($position.Width)x$($position.Height). Position: $($position.X),$($position.Y)."


