param([string]$Uri = "")

# Target of the codexmonitor: URL protocol, i.e. the "Update now" button in the update
# notification (registered by Watch-PrimaryDisplay.ps1). It only leaves the request for the
# display watcher, which owns the install logic: codexmonitor:update/v2.1.0 -> update-request.txt.
if ($Uri -match '^codexmonitor:/*update/(?<tag>[\w.\-]+)/?$') {
    Set-Content -LiteralPath (Join-Path $PSScriptRoot "update-request.txt") -Value $Matches.tag -Encoding ASCII
}
