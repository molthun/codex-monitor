param(
    [string]$InstallRoot = "C:\CodexMonitor",
    [int]$IntervalSeconds = 5,
    [string]$ConfigPath = ""
)

$ErrorActionPreference = "SilentlyContinue"

function Read-CodexConfig {
    param([string]$Path)
    $root = $PSScriptRoot
    $configPath = if ($Path) { $Path } else { Join-Path $root "config.json" }
    $examplePath = Join-Path $root "config.example.json"
    if (Test-Path -LiteralPath $configPath) { return Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    if ((-not $Path) -and (Test-Path -LiteralPath $examplePath)) { return Get-Content -LiteralPath $examplePath -Raw -Encoding UTF8 | ConvertFrom-Json }
    return [pscustomobject]@{}
}

$config = Read-CodexConfig -Path $ConfigPath
if ($config.installRoot -and $InstallRoot -eq "C:\CodexMonitor") { $InstallRoot = $config.installRoot }
if ($config.display.watchIntervalSeconds -ne $null -and $IntervalSeconds -eq 5) { $IntervalSeconds = [int]$config.display.watchIntervalSeconds }

function Get-RainmeterSkinPath {
    if ($config.rainmeter.skinPath) { return $config.rainmeter.skinPath }
    $rainmeterIni = Join-Path $env:APPDATA "Rainmeter\Rainmeter.ini"
    if (Test-Path -LiteralPath $rainmeterIni) {
        $line = Get-Content -LiteralPath $rainmeterIni | Where-Object { $_ -match "^SkinPath=(.*)$" } | Select-Object -First 1
        if ($line -match "^SkinPath=(.*)$") { return $Matches[1] }
    }

    return Join-Path ([Environment]::GetFolderPath("MyDocuments")) "Rainmeter\Skins\"
}

function Get-WidgetWidth {
    $skinPath = Get-RainmeterSkinPath
    $skinIni = Join-Path $skinPath "CodexMonitor\CodexMonitor.ini"
    if (Test-Path -LiteralPath $skinIni) {
        $line = Get-Content -LiteralPath $skinIni | Where-Object { $_ -match "^W=(\d+)" } | Select-Object -First 1
        if ($line -match "^W=(\d+)") { return [int]$Matches[1] }
    }

    $fallback = Join-Path $InstallRoot "CodexMonitor.ini"
    if (Test-Path -LiteralPath $fallback) {
        $line = Get-Content -LiteralPath $fallback | Where-Object { $_ -match "^W=(\d+)" } | Select-Object -First 1
        if ($line -match "^W=(\d+)") { return [int]$Matches[1] }
    }

    return 430
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

function Save-Position {
    param([int]$X, [int]$Y)

    $rainmeterIni = Join-Path $env:APPDATA "Rainmeter\Rainmeter.ini"
    if (-not (Test-Path -LiteralPath $rainmeterIni)) { return }

    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($line in (Get-Content -LiteralPath $rainmeterIni)) { $lines.Add($line) }

    Set-IniKey -Lines $lines -Section "CodexMonitor" -Key "WindowX" -Value $X
    Set-IniKey -Lines $lines -Section "CodexMonitor" -Key "WindowY" -Value $Y
    Set-IniKey -Lines $lines -Section "CodexMonitor" -Key "AnchorX" -Value 0
    Set-IniKey -Lines $lines -Section "CodexMonitor" -Key "AnchorY" -Value 0
    Set-IniKey -Lines $lines -Section "CodexMonitor" -Key "AutoSelectScreen" -Value 0
    Set-IniKey -Lines $lines -Section "CodexMonitor" -Key "SavePosition" -Value 0

    Set-Content -LiteralPath $rainmeterIni -Value $lines -Encoding Unicode
}

function Move-WidgetToPrimary {
    Add-Type -AssemblyName System.Windows.Forms
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $width = Get-WidgetWidth
    $marginRight = if ($config.display.marginRight -ne $null) { [int]$config.display.marginRight } else { 24 }
    $marginTop = if ($config.display.marginTop -ne $null) { [int]$config.display.marginTop } else { 24 }
    $x = [int]($screen.X + $screen.Width - $width - $marginRight)
    $Y = [int]($screen.Y + $marginTop)
    $signature = "$($screen.X),$($screen.Y),$($screen.Width),$($screen.Height),$width"

    $rainmeterExe = if ($config.rainmeter.executable) { $config.rainmeter.executable } else { "C:\Program Files\Rainmeter\Rainmeter.exe" }
    if (Test-Path -LiteralPath $rainmeterExe) {
        & $rainmeterExe !Move $x $y "CodexMonitor"
    }

    Save-Position -X $x -Y $y
    return $signature
}

$lastSignature = ""

while ($true) {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $width = Get-WidgetWidth
        $signature = "$($screen.X),$($screen.Y),$($screen.Width),$($screen.Height),$width"
        if ($signature -ne $lastSignature) {
            $lastSignature = Move-WidgetToPrimary
        }
    }
    catch {
        # Keep the watcher alive across transient display/Rainmeter startup changes.
    }

    Start-Sleep -Seconds $IntervalSeconds
}


