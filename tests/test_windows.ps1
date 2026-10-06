$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root "windows/Deploy/Install-Transaction.ps1")

function Assert($Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Read-Ast([string]$Path) {
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    Assert (-not $errors) "PowerShell syntax errors in $Path : $errors"
    return $ast
}

Get-ChildItem -LiteralPath $root -Filter *.ps1 -Recurse | ForEach-Object { [void](Read-Ast $_.FullName) }
$watcher = Read-Ast (Join-Path $root "windows/Watch-PrimaryDisplay.ps1")
$functions = $watcher.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)
foreach ($name in @("Read-CodexConfig", "Update-CodexConfig", "Get-UpdateMode", "Get-TargetPosition", "Invoke-CheckedCommand", "Format-CommandOutput", "Get-RainmeterSkinPath", "Install-Release")) {
    $definition = ($functions | Where-Object Name -eq $name).Extent.Text
    $definition = $definition.Replace('$PSScriptRoot', '$script:root')
    Invoke-Expression $definition
}
# The bootstrap stays self-contained for installations of older release tags.
$bootstrap = Read-Ast (Join-Path $root "install.ps1")
$embedded = $bootstrap.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq "Invoke-CodexInstallTransaction" }, $false)
$helper = (Read-Ast (Join-Path $root "windows/Deploy/Install-Transaction.ps1")).FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)
Assert ($embedded.Extent.Text -eq $helper.Extent.Text) "Bootstrap transaction must match the deploy helper"

$sandbox = Join-Path ([IO.Path]::GetTempPath()) ("codex-regression-" + [guid]::NewGuid())
$previousTemp = $env:TEMP
$previousAppData = $env:APPDATA
New-Item -ItemType Directory -Path $sandbox | Out-Null
try {
    $ConfigPath = Join-Path $sandbox "config.json"
    $InstallRoot = Join-Path $sandbox "install"
    $intervalArgument = 5
    $script:configJson = ""
    function Get-PhysicalPrimaryBounds { return @{ X=0; Y=0; Width=1920; Height=1080 } }
    function Set-UpdateStatus { param($Tag) $script:updateStatus = $Tag }
    Set-Content -LiteralPath $ConfigPath '{"display":{"autoUpdate":true,"marginRight":24}}'
    Update-CodexConfig
    Assert ((Get-UpdateMode) -eq "install") "Initial update mode"
    Set-Content -LiteralPath $ConfigPath '{"display":{"autoUpdate":false,"marginRight":100,"watchIntervalSeconds":9}}'
    Update-CodexConfig
    Assert ((Get-UpdateMode) -eq "off") "Disabling auto-update must apply immediately"
    Assert ((Get-TargetPosition).X -eq 1820) "Margins must apply immediately"
    Assert ($IntervalSeconds -eq 9) "Watch interval must reload"
    Assert ($updateStatus -eq "") "Disabling updates must clear the offer"
    Set-Content -LiteralPath $ConfigPath '{broken'
    Update-CodexConfig
    Assert ((Get-UpdateMode) -eq "off") "Malformed edits must keep the last valid config"

    # Exercise the real updater with fake downloads and Windows process commands.
    $env:TEMP = Join-Path $sandbox "temp"
    $env:APPDATA = Join-Path $sandbox "appdata"
    $skin = Join-Path $sandbox "skins/CodexMonitor"
    $source = Join-Path $sandbox "release/windows"
    New-Item -ItemType Directory -Path $env:TEMP, "$env:APPDATA/Rainmeter", $InstallRoot,
        "$InstallRoot/CodexBridge", "$skin/@Resources/Icons", "$source/CodexBridge",
        "$source/Deploy/Payload/CodexBridge", "$source/Deploy/Payload/@Resources/Icons" -Force | Out-Null
    Set-Content "$source/Deploy/Switch-WidgetSize.ps1" "# fixture"
    Set-Content "$source/Deploy/Payload/@Resources/Icons/net.png" "new icon"
    Set-Content "$InstallRoot/CodexBridge/CodexBridge.exe" "old bridge"
    Set-Content "$InstallRoot/.local_version" "v2.2.0"
    Set-Content "$InstallRoot/config.json" "user settings"
    Set-Content "$skin/@Resources/Icons/net.png" "old icon"
    Set-Content "$skin/CodexMonitor.ini" "old skin"
    Set-Content "$env:APPDATA/Rainmeter/Rainmeter.ini" "old position"
    $script:fixtureZip = Join-Path $sandbox "release.zip"
    Compress-Archive -LiteralPath (Join-Path $sandbox "release") -DestinationPath $fixtureZip
    $script:fixtureExe = Join-Path $sandbox "new.exe"
    [IO.File]::WriteAllBytes($fixtureExe, (New-Object byte[] 1048576))
    $rainmeter = Join-Path $sandbox "rainmeter.ps1"
    Set-Content $rainmeter '$global:LASTEXITCODE = 0'
    $config = [pscustomobject]@{ rainmeter = [pscustomobject]@{ skinPath = (Split-Path $skin); executable = $rainmeter } }
    $ConfigPath = ""
    function Show-Notification { param($Title, $Message) }
    function Restart-AfterUpdate { param($BridgeExe) }
    function Stop-Process { param($Name, [switch]$Force, $ErrorAction) }
    function Start-Process { param($FilePath, $ArgumentList) }
    function schtasks.exe { $global:LASTEXITCODE = 0 }
    function powershell.exe {
        if ($script:fault -eq "rebuild") { $global:LASTEXITCODE = 42 } else { $global:LASTEXITCODE = 0 }
    }
    function Invoke-WebRequest {
        param($Uri, $OutFile, [switch]$UseBasicParsing)
        if ($script:fault -eq "download") { throw "simulated offline download" }
        $fixture = if ($Uri.EndsWith(".zip")) { $script:fixtureZip } else { $script:fixtureExe }
        Copy-Item -LiteralPath $fixture -Destination $OutFile
    }

    foreach ($failurePoint in @("download", "rebuild")) {
        $script:fault = $failurePoint
        $failed = $false
        try { Install-Release "v2.2.1" } catch { $failed = $true }
        Assert $failed "$failurePoint must fail visibly"
        Assert ((Get-Content "$InstallRoot/CodexBridge/CodexBridge.exe" -Raw).Trim() -eq "old bridge") "$failurePoint must keep the old binary"
        Assert ((Get-Content "$InstallRoot/.local_version" -Raw).Trim() -eq "v2.2.0") "$failurePoint must keep the old version"
        Assert ((Get-Content "$InstallRoot/config.json" -Raw).Trim() -eq "user settings") "$failurePoint must preserve settings"
        Assert ((Get-Content "$skin/@Resources/Icons/net.png" -Raw).Trim() -eq "old icon") "$failurePoint must restore icons"
        Assert ((Get-Content "$skin/CodexMonitor.ini" -Raw).Trim() -eq "old skin") "$failurePoint must restore skin"
    }
    $script:fault = ""
    Install-Release "v2.2.1"
    Assert ((Get-Content "$InstallRoot/.local_version" -Raw).Trim() -eq "v2.2.1") "Successful update must commit the version"
    Assert ((Get-Item "$InstallRoot/CodexBridge/CodexBridge.exe").Length -eq 1048576) "Successful update must install the new binary"
    Assert ((Get-Content "$skin/@Resources/Icons/net.png" -Raw).Trim() -eq "new icon") "Successful update must copy icons"
    Assert ((Get-Content "$InstallRoot/config.json" -Raw).Trim() -eq "user settings") "Successful update must preserve config"
    Write-Output "PowerShell regression checks passed."
} finally {
    $env:TEMP = $previousTemp
    $env:APPDATA = $previousAppData
    Remove-Item -LiteralPath $sandbox -Recurse -Force
}
