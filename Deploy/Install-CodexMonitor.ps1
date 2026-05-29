param(
    [string]$InstallRoot = "C:\CodexMonitor",
    [string]$RainmeterSkinPath = "",
    [string]$ConfigPath = "",
    [switch]$SkipRainmeterLayout,
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"

function Get-ProjectRoot {
    return (Split-Path -Parent $PSScriptRoot)
}

function Read-CodexConfig {
    param([string]$Path)
    $projectRoot = Get-ProjectRoot
    $configPath = if ($Path) { $Path } else { Join-Path $projectRoot "config.json" }
    $examplePath = Join-Path $projectRoot "config.example.json"
    if (Test-Path -LiteralPath $configPath) {
        return Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    if ((-not $Path) -and (Test-Path -LiteralPath $examplePath)) {
        return Get-Content -LiteralPath $examplePath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    return [pscustomobject]@{}
}

$config = Read-CodexConfig -Path $ConfigPath
if ($config.installRoot -and $InstallRoot -eq "C:\CodexMonitor") { $InstallRoot = $config.installRoot }
if ($config.rainmeter.skinPath -and -not $RainmeterSkinPath) { $RainmeterSkinPath = $config.rainmeter.skinPath }

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    $argsList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"")
    if ($InstallRoot -ne "C:\CodexMonitor") { $argsList += @("-InstallRoot", "`"$InstallRoot`"") }
    if ($RainmeterSkinPath) { $argsList += @("-RainmeterSkinPath", "`"$RainmeterSkinPath`"") }
    if ($ConfigPath) { $argsList += @("-ConfigPath", "`"$ConfigPath`"") }
    if ($SkipRainmeterLayout) { $argsList += "-SkipRainmeterLayout" }
    if ($NoStart) { $argsList += "-NoStart" }
    Start-Process powershell.exe -Verb RunAs -ArgumentList ($argsList -join " ")
    exit
}

function Get-IniValue {
    param([string]$Path, [string]$Key)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $line = Get-Content -LiteralPath $Path | Where-Object { $_ -match "^\s*$([regex]::Escape($Key))=(.*)$" } | Select-Object -First 1
    if ($line -match "^\s*$([regex]::Escape($Key))=(.*)$") { return $Matches[1] }
    return $null
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

function Get-PrimaryMonitorPosition {
    param([string]$SkinIni)

    Add-Type -AssemblyName System.Windows.Forms
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $widgetWidth = Get-WidgetWidth -Path $SkinIni
    $marginRight = if ($config.display.marginRight -ne $null) { [int]$config.display.marginRight } else { 24 }
    $marginTop = if ($config.display.marginTop -ne $null) { [int]$config.display.marginTop } else { 24 }
    return @{
        X = [int]($screen.X + $screen.Width - $widgetWidth - $marginRight)
        Y = [int]($screen.Y + $marginTop)
        Width = $screen.Width
        Height = $screen.Height
    }
}

$packageRoot = $PSScriptRoot
$payload = Join-Path $packageRoot "Payload"
$bridgeSource = Join-Path $payload "CodexBridge"
$skinSource = Join-Path $payload "RainmeterSkin\CodexMonitor"
$layoutSource = Join-Path $payload "RainmeterLayout-CodexMonitor.ini"

if (-not (Test-Path -LiteralPath $payload)) {
    throw "Payload folder not found: $payload"
}

$rainmeterIni = Join-Path $env:APPDATA "Rainmeter\Rainmeter.ini"
if (-not $RainmeterSkinPath) {
    $RainmeterSkinPath = Get-IniValue -Path $rainmeterIni -Key "SkinPath"
}
if (-not $RainmeterSkinPath) {
    $RainmeterSkinPath = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "Rainmeter\Skins\"
}

$skinTarget = Join-Path $RainmeterSkinPath "CodexMonitor"
$resourcesTarget = Join-Path $InstallRoot "@Resources"
$bridgeTarget = Join-Path $InstallRoot "CodexBridge"
$presetsTarget = Join-Path $InstallRoot "Presets"
$bridgeProject = Join-Path $bridgeTarget "CodexBridge.csproj"
$bridgeExe = Join-Path $bridgeTarget "bin\Release\net10.0\CodexBridge.exe"
$watcherScript = Join-Path $InstallRoot "Watch-PrimaryDisplay.ps1"
$configTarget = Join-Path $InstallRoot "config.json"
$rainmeterExe = if ($config.rainmeter.executable) { $config.rainmeter.executable } else { "C:\Program Files\Rainmeter\Rainmeter.exe" }
$taskName = if ($config.bridge.taskName) { $config.bridge.taskName } else { "CodexMonitor Bridge Elevated" }
$watcherShortcutName = if ($config.startup.watcherShortcutName) { $config.startup.watcherShortcutName } else { "CodexMonitor Display Watcher.lnk" }
$watcherShortcut = Join-Path ([Environment]::GetFolderPath("Startup")) $watcherShortcutName

Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue | Stop-ScheduledTask -ErrorAction SilentlyContinue
Get-Process CodexBridge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like "*Watch-PrimaryDisplay.ps1*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

$skinResourcesTarget = Join-Path $skinTarget "@Resources"
New-Item -ItemType Directory -Force -Path $InstallRoot, $resourcesTarget, $skinTarget, $presetsTarget, $skinResourcesTarget | Out-Null
Copy-Item -LiteralPath $bridgeSource -Destination $InstallRoot -Recurse -Force
Copy-Item -LiteralPath (Join-Path $payload "CodexMonitor.ini") -Destination (Join-Path $InstallRoot "CodexMonitor.ini") -Force
Copy-Item -LiteralPath (Join-Path $skinSource "CodexMonitor.ini") -Destination (Join-Path $skinTarget "CodexMonitor.ini") -Force
Copy-Item -LiteralPath (Join-Path $skinSource "CodexMonitor.1080p.ini") -Destination (Join-Path $presetsTarget "CodexMonitor.1080p.ini") -Force
Copy-Item -LiteralPath (Join-Path $skinSource "CodexMonitor.4K.ini") -Destination (Join-Path $presetsTarget "CodexMonitor.4K.ini") -Force
$watcherSource = [System.IO.Path]::GetFullPath((Join-Path (Get-ProjectRoot) "Watch-PrimaryDisplay.ps1"))
$watcherDest = [System.IO.Path]::GetFullPath($watcherScript)
if ($watcherSource -ine $watcherDest) {
    Copy-Item -LiteralPath $watcherSource -Destination $watcherScript -Force
}
if (Test-Path -LiteralPath (Join-Path $payload "@Resources")) {
    Copy-Item -LiteralPath (Join-Path $payload "@Resources") -Destination $skinTarget -Recurse -Force
}
$projectConfig = Join-Path (Get-ProjectRoot) "config.json"
$exampleConfig = Join-Path (Get-ProjectRoot) "config.example.json"
if (-not (Test-Path -LiteralPath $projectConfig) -and (Test-Path -LiteralPath $exampleConfig)) {
    Copy-Item -LiteralPath $exampleConfig -Destination $projectConfig -Force
}
if ((Test-Path -LiteralPath $projectConfig) -and ((Resolve-Path -LiteralPath $projectConfig).Path -ne $configTarget)) {
    Copy-Item -LiteralPath $projectConfig -Destination $configTarget -Force
}

if (Test-Path -LiteralPath $configTarget) {
    try {
        $configJson = Get-Content -LiteralPath $configTarget -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($configJson.bridge) {
            $configJson.bridge.outputFile = (Join-Path $skinTarget "@Resources\temps.txt")
            $configJson | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $configTarget -Encoding UTF8
        }
    }
    catch {
        Write-Warning "Failed to update outputFile in config.json: $_"
    }
}

if (-not (Test-Path -LiteralPath $bridgeExe)) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "dotnet SDK/runtime is not available. Run Deploy\Install-Prerequisites-And-Restore.ps1 first."
    }

    Write-Host "Building CodexBridge..."
    & dotnet build $bridgeProject -c Release
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $bridgeExe)) {
        throw "CodexBridge build failed."
    }
}

$sizeSwitcher = Join-Path $packageRoot "Switch-WidgetSize.ps1"
if (Test-Path -LiteralPath $sizeSwitcher) {
    $profileMode = if ($config.profiles.default) { $config.profiles.default } else { "Auto" }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sizeSwitcher -Mode $profileMode -InstallRoot $InstallRoot -ConfigPath $ConfigPath
}

if (-not (Test-Path -LiteralPath (Join-Path $skinResourcesTarget "temps.txt"))) {
    if (Test-Path -LiteralPath (Join-Path $payload "@Resources\temps.example.txt")) {
        Copy-Item -LiteralPath (Join-Path $payload "@Resources\temps.example.txt") -Destination (Join-Path $skinResourcesTarget "temps.txt") -Force
    }
}


$bridgeArgs = "--config `"$configTarget`""
$action = New-ScheduledTaskAction -Execute $bridgeExe -Argument $bridgeArgs -WorkingDirectory (Split-Path -Parent $bridgeExe)
$trigger = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Days 30) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($watcherShortcut)
$shortcut.TargetPath = "powershell.exe"
$shortcut.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$watcherScript`" -ConfigPath `"$configTarget`""
$shortcut.WorkingDirectory = $InstallRoot
$shortcut.WindowStyle = 7
$shortcut.Save()

if (-not $SkipRainmeterLayout -and (Test-Path -LiteralPath $rainmeterIni)) {
    Get-Process Rainmeter -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500

    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($line in (Get-Content -LiteralPath $rainmeterIni)) { $lines.Add($line) }
    Set-IniKey -Lines $lines -Section "Rainmeter" -Key "SkinPath" -Value $RainmeterSkinPath

    $layoutLines = Get-Content -LiteralPath $layoutSource
    foreach ($line in $layoutLines) {
        if ($line -match "^\s*\[.+\]\s*$" -or $line.Trim() -eq "") { continue }
        $split = $line.Split("=", 2)
        if ($split.Length -eq 2) {
            Set-IniKey -Lines $lines -Section "CodexMonitor" -Key $split[0] -Value $split[1]
        }
    }

    $position = Get-PrimaryMonitorPosition -SkinIni (Join-Path $skinTarget "CodexMonitor.ini")
    Set-IniKey -Lines $lines -Section "CodexMonitor" -Key "WindowX" -Value $position.X
    Set-IniKey -Lines $lines -Section "CodexMonitor" -Key "WindowY" -Value $position.Y
    Set-IniKey -Lines $lines -Section "CodexMonitor" -Key "AnchorX" -Value 0
    Set-IniKey -Lines $lines -Section "CodexMonitor" -Key "AnchorY" -Value 0
    Set-IniKey -Lines $lines -Section "CodexMonitor" -Key "AutoSelectScreen" -Value 0
    if ($config.rainmeter.desktopMode.alwaysOnTop -ne $null) { Set-IniKey -Lines $lines -Section "CodexMonitor" -Key "AlwaysOnTop" -Value $config.rainmeter.desktopMode.alwaysOnTop }
    if ($config.rainmeter.desktopMode.draggable -ne $null) { Set-IniKey -Lines $lines -Section "CodexMonitor" -Key "Draggable" -Value $config.rainmeter.desktopMode.draggable }
    if ($config.rainmeter.desktopMode.clickThrough -ne $null) { Set-IniKey -Lines $lines -Section "CodexMonitor" -Key "ClickThrough" -Value $config.rainmeter.desktopMode.clickThrough }
    if ($config.rainmeter.desktopMode.savePosition -ne $null) { Set-IniKey -Lines $lines -Section "CodexMonitor" -Key "SavePosition" -Value $config.rainmeter.desktopMode.savePosition }

    Set-Content -LiteralPath $rainmeterIni -Value $lines -Encoding Unicode
}

if (-not $NoStart) {
    Start-ScheduledTask -TaskName $taskName
    Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$watcherScript`" -ConfigPath `"$configTarget`"" -WindowStyle Hidden
    if (Test-Path -LiteralPath $rainmeterExe) {
        if (-not (Get-Process Rainmeter -ErrorAction SilentlyContinue)) {
            Start-Process -FilePath $rainmeterExe
            Start-Sleep -Seconds 2
        }
        & $rainmeterExe !Refresh "CodexMonitor"
        & $rainmeterExe !ActivateConfig "CodexMonitor" "CodexMonitor.ini"
    }
}

Write-Host "CodexMonitor installed."
Write-Host "InstallRoot: $InstallRoot"
Write-Host "Rainmeter skin: $skinTarget"
Write-Host "Scheduled task: $taskName"
Write-Host "Display watcher startup shortcut: $watcherShortcut"


