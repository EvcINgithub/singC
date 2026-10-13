param(
    [string]$OutputDirectory = (Join-Path $env:USERPROFILE 'Desktop\singC-proxy-diagnostics'),
    [switch]$SnapshotOnly,
    [ValidateRange(1, 120)][int]$WatchMinutes = 30,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'

function Protect-Value($Value) {
    if ($null -eq $Value) { return $null }
    $text = [string]$Value
    # Report endpoints, not URL credentials, paths, queries or fragments.
    $text = [regex]::Replace($text, '(?i)(https?|socks5h?|socks4)://[^\s;]+', {
        param($match)
        $uri = $null
        if ([Uri]::TryCreate($match.Value, [UriKind]::Absolute, [ref]$uri)) {
            return $uri.GetLeftPart([UriPartial]::Authority) -replace '//[^/@]+@', '//[redacted]@'
        }
        return '[unparseable URL redacted]'
    })
    return ($text -replace '[^\s;=/]+:[^\s;=@]+@', '[redacted]@')
}

function Read-ProxyRegistry([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return @{ Status = 'absent' } }
    try {
        $entry = Get-ItemProperty -LiteralPath $Path
        $result = [ordered]@{ Status = 'read' }
        foreach ($key in @('ProxyEnable', 'ProxyServer', 'ProxyOverride', 'AutoConfigURL', 'AutoDetect',
            'ProxyMode', 'ProxyPacUrl', 'ProxyBypassList', 'ProxySettingsPerUser')) {
            if ($null -ne $entry.PSObject.Properties[$key]) { $result[$key] = Protect-Value $entry.$key }
        }
        return $result
    } catch { return @{ Status = 'unavailable'; Reason = $_.Exception.GetType().Name } }
}

function Get-ObservedProcesses {
    $result = @()
    foreach ($name in @('singC', 'sing-box', 'chrome', 'msedge', 'firefox')) {
        foreach ($process in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            $start = $null; $version = $null
            try { $start = $process.StartTime.ToUniversalTime().ToString('o') } catch {}
            if ($name -eq 'singC') { try { $version = $process.MainModule.FileVersionInfo.FileVersion } catch {} }
            $result += [pscustomobject]@{ Name = $name; Id = $process.Id; StartUtc = $start; Version = $version }
            $process.Dispose()
        }
    }
    return $result
}

function Read-SettingsSummary {
    $path = Join-Path $env:LOCALAPPDATA 'singC\settings.json'
    if (-not (Test-Path -LiteralPath $path)) { return @{ Status = 'absent' } }
    try {
        $data = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
        return @{ Status = 'read'; ProxyMode = $data.ProxyMode }
    } catch { return @{ Status = 'unavailable'; Reason = $_.Exception.GetType().Name } }
}

function Read-JournalSummary {
    $path = Join-Path $env:LOCALAPPDATA 'singC\system-proxy-backup.json'
    if (-not (Test-Path -LiteralPath $path)) { return @{ Status = 'absent' } }
    $file = Get-Item -LiteralPath $path
    $result = [ordered]@{ Bytes = $file.Length; ModifiedUtc = $file.LastWriteTimeUtc.ToString('o') }
    try {
        if ($file.Length -eq 0) { $result.Status = 'empty'; return $result }
        $data = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
        $result.Status = 'read'
        foreach ($key in @('Original', 'Applied')) {
            $settings = $data.$key
            if ($null -ne $settings) {
                $result[$key] = @{ Flags = $settings.Flags; Server = Protect-Value $settings.Server;
                    Bypass = Protect-Value $settings.Bypass; AutoConfigUrl = Protect-Value $settings.AutoConfigUrl }
            }
        }
    } catch { $result.Status = 'locked-or-unreadable'; $result.Reason = $_.Exception.GetType().Name }
    return $result
}

function Read-BrowserPreferences {
    $result = @()
    foreach ($browser in @(
        @{ Name = 'Chrome'; Root = (Join-Path $env:LOCALAPPDATA 'Google\Chrome\User Data') },
        @{ Name = 'Edge'; Root = (Join-Path $env:LOCALAPPDATA 'Microsoft\Edge\User Data') }
    )) {
        if (-not (Test-Path -LiteralPath $browser.Root)) { continue }
        $index = 0
        foreach ($profile in @(Get-ChildItem -LiteralPath $browser.Root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq 'Default' -or $_.Name -match '^Profile \d+$' })) {
            $index++
            $path = Join-Path $profile.FullName 'Preferences'
            $entry = [ordered]@{ Browser = $browser.Name; Profile = $index; Status = 'no-proxy-field' }
            try {
                $data = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($null -ne $data.proxy) {
                    $entry.Status = 'read'; $entry.Proxy = @{}
                    foreach ($key in @('mode', 'server', 'pac_url', 'bypass_list')) {
                        if ($null -ne $data.proxy.PSObject.Properties[$key]) { $entry.Proxy[$key] = Protect-Value $data.proxy.$key }
                    }
                }
            } catch { $entry.Status = 'unavailable'; $entry.Reason = $_.Exception.GetType().Name }
            $result += $entry
        }
    }
    $firefoxRoot = Join-Path $env:APPDATA 'Mozilla\Firefox\Profiles'
    if (Test-Path -LiteralPath $firefoxRoot) {
        $index = 0
        foreach ($profile in @(Get-ChildItem -LiteralPath $firefoxRoot -Directory -ErrorAction SilentlyContinue)) {
            $index++
            foreach ($fileName in @('prefs.js', 'user.js')) {
                $path = Join-Path $profile.FullName $fileName
                if (-not (Test-Path -LiteralPath $path)) { continue }
                $values = @{}
                try {
                    foreach ($line in Get-Content -LiteralPath $path -Encoding UTF8) {
                        if ($line -match '^user_pref\("(network\.proxy\.(?:type|http|http_port|ssl|ssl_port|socks|socks_port|socks_version|socks_remote_dns|no_proxies_on|autoconfig_url|share_proxy_settings))",\s*(.+)\);$') {
                            $values[$matches[1]] = Protect-Value $matches[2]
                        }
                    }
                    $result += @{ Browser = 'Firefox'; Profile = $index; File = $fileName; Status = 'read'; Proxy = $values }
                } catch { $result += @{ Browser = 'Firefox'; Profile = $index; Status = 'unavailable' } }
            }
        }
    }
    return $result
}

function Write-Snapshot([string]$Label) {
    $policies = [ordered]@{}
    foreach ($scope in @('HKCU', 'HKLM')) {
        foreach ($browser in @('Google\Chrome', 'Microsoft\Edge', 'Mozilla\Firefox\Proxy')) {
            $policies["$scope/$browser"] = Read-ProxyRegistry "${scope}:\Software\Policies\$browser"
        }
    }
    $connectionHashes = @{}
    try {
        $connections = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings\Connections'
        foreach ($key in @('DefaultConnectionSettings', 'SavedLegacySettings')) {
            if ($connections.$key -is [byte[]]) {
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $connectionHashes[$key] = [BitConverter]::ToString($sha.ComputeHash($connections.$key)).Replace('-', '') }
                finally { $sha.Dispose() }
            }
        }
    } catch {}
    try { $winhttp = @(& netsh.exe winhttp show proxy 2>&1 | ForEach-Object { Protect-Value $_ }) }
    catch { $winhttp = @('query-unavailable: ' + $_.Exception.GetType().Name) }
    $data = [ordered]@{
        Schema = 1; Label = $Label; TimestampUtc = [DateTime]::UtcNow.ToString('o')
        Processes = @(Get-ObservedProcesses)
        SystemProxy = Read-ProxyRegistry 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
        MachineProxy = Read-ProxyRegistry 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
        ConnectionSettingHashes = $connectionHashes; WinHttp = $winhttp
        BrowserPolicies = $policies; BrowserPreferences = @(Read-BrowserPreferences)
        SingC = Read-SettingsSummary; RecoveryJournal = Read-JournalSummary
    }
    $path = Join-Path $runDirectory "$Label.json"
    $data | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding UTF8
    Write-Host "Saved $Label.json"
}

if ($SelfTest) {
    $actual = Protect-Value 'https://alice:secret@example.test/pac?token=private#fragment'
    if ($actual -match 'alice|secret|private|fragment|/pac' -or $actual -notmatch 'example.test') { throw 'URL redaction failed' }
    if ((Protect-Value 'alice:secret@127.0.0.1:7890') -match 'alice|secret') { throw 'Proxy credentials leaked' }
    if ((Protect-Value 'http=127.0.0.1:7890;https=127.0.0.1:7890') -ne 'http=127.0.0.1:7890;https=127.0.0.1:7890') { throw 'Endpoint changed' }
    Write-Host 'PASS: 3 redaction checks. No system configuration collected or changed.'
    return
}

$runDirectory = Join-Path $OutputDirectory ([DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
Write-Host "Read-only diagnostic capture. Output: $runDirectory"
$observed = @(Get-ObservedProcesses | Where-Object { $_.Name -eq 'singC' })
Write-Snapshot '01-initial'
if ($SnapshotOnly) { return }
if ($observed.Count -eq 0) {
    Write-Host 'singC is not running. Start singC, enable the affected mode, then run this script again.'
    return
}
Write-Host 'Now close singC normally. This script waits for all observed singC processes to exit.'
$deadline = [DateTime]::UtcNow.AddMinutes($WatchMinutes)
while ([DateTime]::UtcNow -lt $deadline) {
    $current = @(Get-ObservedProcesses | Where-Object { $_.Name -eq 'singC' })
    $alive = @($current | Where-Object {
        $candidate = $_
        @($observed | Where-Object { $_.Id -eq $candidate.Id -and ($null -eq $_.StartUtc -or $_.StartUtc -eq $candidate.StartUtc) }).Count -gt 0
    })
    if ($alive.Count -eq 0) {
        Write-Snapshot '02-exited'
        Start-Sleep -Seconds 2
        Write-Snapshot '03-after-2s'
        Start-Sleep -Seconds 8
        Write-Snapshot '04-after-10s'
        Write-Host "Finished. Review the JSON files before sending them to the developer: $runDirectory"
        return
    }
    Start-Sleep -Milliseconds 500
}
Write-Snapshot '02-watch-timeout'
Write-Host 'Timed out; singC exit was not observed. This is not evidence of successful cleanup.'
