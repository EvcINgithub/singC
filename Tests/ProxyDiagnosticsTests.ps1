$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $sourceRoot 'Scripts\Collect-ProxyDiagnostics.ps1') -SelfTest
$fixtureRoot = Join-Path $sourceRoot ('artifacts\validation\proxy-diagnostics-20261013\fixtures-' + [guid]::NewGuid().ToString('N'))
$savedLocal = $env:LOCALAPPDATA
$savedRoaming = $env:APPDATA
$script:passed = 0
function Assert($Condition, $Message) {
    if (-not $Condition) { throw $Message }
    $script:passed++
    Write-Host "PASS: $Message"
}
try {
    $env:LOCALAPPDATA = Join-Path $fixtureRoot 'local'
    $env:APPDATA = Join-Path $fixtureRoot 'roaming'
    $chrome = Join-Path $env:LOCALAPPDATA 'Google\Chrome\User Data\Default'
    $firefox = Join-Path $env:APPDATA 'Mozilla\Firefox\Profiles\fixture'
    $singc = Join-Path $env:LOCALAPPDATA 'singC'
    foreach ($directory in @($chrome, $firefox, $singc)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    '{"proxy":{"mode":"fixed_servers","server":"http://name:password@127.0.0.1:7890","pac_url":"https://example.test/private?token=secret"},"account":{"token":"DO_NOT_EXPORT"}}' |
        Set-Content -LiteralPath (Join-Path $chrome 'Preferences') -Encoding UTF8
    @('user_pref("network.proxy.type", 1);', 'user_pref("network.proxy.http", "127.0.0.1");',
        'user_pref("network.proxy.http_port", 7890);', 'user_pref("browser.private_value", "DO_NOT_EXPORT");') |
        Set-Content -LiteralPath (Join-Path $firefox 'prefs.js') -Encoding UTF8
    $browsers = @(Read-BrowserPreferences)
    $json = $browsers | ConvertTo-Json -Depth 8
    Assert ($browsers.Count -eq 2) 'Chrome and Firefox fixture profiles are collected'
    Assert ($json -notmatch 'password|DO_NOT_EXPORT|token=secret|/private') 'Credentials and unrelated preferences are excluded'
    Assert ($json -match '127.0.0.1:7890' -and $json -match 'network.proxy.http_port') 'Useful proxy endpoints remain'
    '{"ProxyMode":"SystemProxy:7890","ConfigPath":"PRIVATE_PATH","Secret":"DO_NOT_EXPORT"}' |
        Set-Content -LiteralPath (Join-Path $singc 'settings.json') -Encoding UTF8
    $settings = Read-SettingsSummary
    Assert ($settings.ProxyMode -eq 'SystemProxy:7890') 'Mode and port are captured'
    Assert (($settings | ConvertTo-Json) -notmatch 'PRIVATE_PATH|DO_NOT_EXPORT') 'User configuration path and unrelated settings are excluded'
    Assert ((Read-JournalSummary).Status -eq 'absent') 'Absent journal is distinguished'
    $journalPath = Join-Path $singc 'system-proxy-backup.json'
    [IO.File]::WriteAllText($journalPath, '')
    Assert ((Read-JournalSummary).Status -eq 'empty') 'Empty journal is distinguished'
    [IO.File]::WriteAllText($journalPath, '{invalid')
    Assert ((Read-JournalSummary).Status -eq 'locked-or-unreadable') 'Malformed journal is reported without exporting raw content'
    [IO.File]::WriteAllText($journalPath, '{"Original":{"Flags":1,"Server":"","AutoConfigUrl":"https://example.test/pac?secret=yes"},"Applied":{"Flags":3,"Server":"http=127.0.0.1:7890"}}')
    $journal = Read-JournalSummary
    Assert ($journal.Status -eq 'read' -and $journal.Applied.Flags -eq 3) 'Journal proxy flags are captured'
    Assert (($journal | ConvertTo-Json -Depth 5) -notmatch '/pac|secret=yes') 'Journal PAC secrets are removed'
    $stream = [IO.File]::Open($journalPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { Assert ((Read-JournalSummary).Status -eq 'locked-or-unreadable') 'Exclusive journal lock does not imply missing record' }
    finally { $stream.Dispose() }
    '{invalid' | Set-Content -LiteralPath (Join-Path $chrome 'Preferences') -Encoding UTF8
    Assert (@(Read-BrowserPreferences | Where-Object { $_.Browser -eq 'Chrome' })[0].Status -eq 'unavailable') 'Malformed browser preferences do not stop other captures'
    Write-Host "Fixture checks passed: $script:passed. Fixtures retained at $fixtureRoot"
} finally {
    $env:LOCALAPPDATA = $savedLocal
    $env:APPDATA = $savedRoaming
}
