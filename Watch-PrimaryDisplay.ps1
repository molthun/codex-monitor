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

function Test-Command {
    param([string]$Name)
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

function Show-Notification {
    param([string]$Title, [string]$Message)
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $notification = New-Object System.Windows.Forms.NotifyIcon
        $notification.Icon = [System.Drawing.SystemIcons]::Information
        $notification.BalloonTipIcon = [System.Windows.Forms.ToolTipIcon]::Info
        $notification.BalloonTipTitle = $Title
        $notification.BalloonTipText = $Message
        $notification.Visible = $true
        $notification.ShowBalloonTip(5000)
        Start-Sleep -Seconds 1
        $notification.Dispose()
    } catch {
        # Ignore errors if forms assembly fails
    }
}

function Check-ForUpdates {
    if ($config.display.autoUpdate -eq $false) { return }

    $gitDir = Join-Path $InstallRoot ".git"
    if (-not (Test-Path -LiteralPath $gitDir)) { return }
    if (-not (Test-Command "git")) { return }

    try {
        # Fetch remote updates
        $null = git -C $InstallRoot fetch origin 2>&1
        $local = (git -C $InstallRoot rev-parse HEAD).Trim()
        $remote = (git -C $InstallRoot rev-parse origin/main).Trim()

        if ($local -ne $remote) {
            Write-Host "New version found. Pulling updates..."
            Show-Notification "CodexMonitor Update" "Installing the latest updates from GitHub..."

            # Pull changes
            $null = git -C $InstallRoot pull origin main 2>&1

            # Stop the elevated bridge task if running to allow file updates
            Stop-Process -Name "CodexBridge" -Force -ErrorAction SilentlyContinue

            # Rebuild the bridge
            if (Test-Command "dotnet") {
                & dotnet build (Join-Path $InstallRoot "CodexBridge\CodexBridge.csproj") -c Release | Out-Null
            }

            # Copy updated presets/payload to the active Rainmeter skin target
            $skinPath = Get-RainmeterSkinPath
            $skinTarget = Join-Path $skinPath "CodexMonitor"
            if (Test-Path -LiteralPath $skinTarget) {
                $payloadIcons = Join-Path $InstallRoot "Deploy\Payload\@Resources\Icons"
                $targetIcons = Join-Path $skinTarget "@Resources\Icons"
                if (Test-Path -LiteralPath $payloadIcons) {
                    New-Item -ItemType Directory -Force -Path $targetIcons | Out-Null
                    Copy-Item -LiteralPath "$payloadIcons\*" -Destination $targetIcons -Force
                }
                
                $mode = Get-AutoProfileMode -ScreenHeight [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height
                $preset = Get-PresetPath -Mode $mode
                if ($preset) {
                    Copy-Item -LiteralPath $preset -Destination (Join-Path $skinTarget "CodexMonitor.ini") -Force
                }
            }

            # Restart the elevated bridge task
            schtasks.exe /run /tn "CodexMonitor Bridge Elevated" | Out-Null

            # Refresh Rainmeter
            $rainmeterExe = if ($config.rainmeter.executable) { $config.rainmeter.executable } else { "C:\Program Files\Rainmeter\Rainmeter.exe" }
            if (Test-Path -LiteralPath $rainmeterExe) {
                & $rainmeterExe !Refresh "CodexMonitor"
            }

            Show-Notification "CodexMonitor Updated" "Widget has been updated to the latest version successfully!"
        }
    }
    catch {
        # Log failure
    }
}

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

function Get-IniNumber {
    param(
        [string]$Path,
        [string]$Key,
        [int]$Default
    )

    if (Test-Path -LiteralPath $Path) {
        $escapedKey = [regex]::Escape($Key)
        $line = Get-Content -LiteralPath $Path | Where-Object { $_ -match "^$escapedKey=(\d+)" } | Select-Object -First 1
        if ($line -match "^$escapedKey=(\d+)") { return [int]$Matches[1] }
    }

    return $Default
}

function Get-AutoProfileMode {
    param([int]$ScreenHeight)

    if ($config.profiles.default -and $config.profiles.default -ne "Auto") {
        return $config.profiles.default
    }

    $threshold = if ($config.display.autoProfileHeightThreshold -ne $null) { [int]$config.display.autoProfileHeightThreshold } else { 1440 }
    $largeProfile = if ($config.profiles.large) { $config.profiles.large } else { "4K" }
    $compactProfile = if ($config.profiles.compact) { $config.profiles.compact } else { "1080p" }
    if ($ScreenHeight -ge $threshold) { return $largeProfile }
    return $compactProfile
}

function Get-PresetPath {
    param([string]$Mode)

    $presetName = if ($Mode -eq "4K") { "CodexMonitor.4K.ini" } else { "CodexMonitor.1080p.ini" }
    $candidates = @(
        (Join-Path $InstallRoot "Presets\$presetName"),
        (Join-Path $InstallRoot "Deploy\Payload\RainmeterSkin\CodexMonitor\$presetName")
    )

    return $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

function Switch-ProfileIfNeeded {
    param([System.Drawing.Rectangle]$ScreenBounds)

    if ($config.profiles.auto -eq $false) { return }

    $mode = Get-AutoProfileMode -ScreenHeight $ScreenBounds.Height
    $preset = Get-PresetPath -Mode $mode
    if (-not $preset) { return }

    $skinPath = Get-RainmeterSkinPath
    $skinIni = Join-Path $skinPath "CodexMonitor\CodexMonitor.ini"
    $currentWidth = Get-IniNumber -Path $skinIni -Key "W" -Default 0
    $currentHeight = Get-IniNumber -Path $skinIni -Key "H" -Default 0
    $targetWidth = Get-IniNumber -Path $preset -Key "W" -Default $currentWidth
    $targetHeight = Get-IniNumber -Path $preset -Key "H" -Default $currentHeight

    if ($currentWidth -eq $targetWidth -and $currentHeight -eq $targetHeight) { return }

    $switcher = Join-Path $InstallRoot "Deploy\Switch-WidgetSize.ps1"
    if (-not (Test-Path -LiteralPath $switcher)) { return }

    $args = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $switcher,
        "-Mode", $mode,
        "-InstallRoot", $InstallRoot
    )
    if ($ConfigPath) { $args += @("-ConfigPath", $ConfigPath) }
    & powershell.exe @args | Out-Null
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
$lastUpdateCheck = [System.DateTime]::MinValue

while ($true) {
    try {
        if ((Get-Date) -gt $lastUpdateCheck.AddHours(6)) {
            Check-ForUpdates
            $lastUpdateCheck = Get-Date
        }

        Add-Type -AssemblyName System.Windows.Forms
        $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        Switch-ProfileIfNeeded -ScreenBounds $screen
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


