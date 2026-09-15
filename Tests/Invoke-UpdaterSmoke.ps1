param([Parameter(Mandatory=$true)][string]$UpdaterPath)
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $sourceRoot 'artifacts/update-fixture'
$work = Join-Path $sourceRoot ('artifacts/updater-smoke-' + [guid]::NewGuid().ToString('N'))
$target = Join-Path $work 'installed'
$stage = Join-Path $work 'stage'
New-Item -ItemType Directory -Path $target, $stage -Force | Out-Null
foreach ($directory in @($target, $stage)) {
    Get-ChildItem -LiteralPath $fixture -File | Where-Object Extension -ne '.pdb' | Copy-Item -Destination $directory
    Copy-Item -LiteralPath $UpdaterPath -Destination (Join-Path $directory 'singC.Updater.exe')
    Set-Content -LiteralPath (Join-Path $directory 'singC.pri') -Value 'fixture resource'
}
Set-Content -LiteralPath (Join-Path $target 'settings.json') -Value 'preserve this user setting'
$entries = @(Get-ChildItem -LiteralPath $stage -File | ForEach-Object { @{ Path = $_.Name; Sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash } })
@{ Version = '1.0.1'; Architecture = 'win-x64'; Files = $entries } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'update-manifest.json') -Encoding utf8
Copy-Item -LiteralPath $UpdaterPath -Destination (Join-Path $work 'singC.Updater.exe')
$parent = $null
$helper = $null
try {
    $parent = Start-Process -FilePath (Join-Path $target 'singC.exe') -ArgumentList '--fixture-parent' -WindowStyle Hidden -PassThru
    $request = @{ TargetDirectory = $target; Version = '1.0.1'; ParentId = $parent.Id; ParentStartTicks = $parent.StartTime.ToUniversalTime().Ticks; ResumeProxy = $false }
    $requestPath = Join-Path $work 'request.json'
    $request | ConvertTo-Json | Set-Content -LiteralPath $requestPath -Encoding utf8
    $helper = Start-Process -FilePath (Join-Path $work 'singC.Updater.exe') -ArgumentList ('"' + $requestPath + '"') -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath (Join-Path $work 'helper-ready'))) {
        if ($helper.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Helper handshake failed' }
        Start-Sleep -Milliseconds 100
    }
    $parent.Kill()
    if (-not $helper.WaitForExit(55000)) { throw 'Updater did not finish' }
    if ($helper.ExitCode -ne 0) { throw 'Updater failed' }
    if (-not (Test-Path -LiteralPath (Join-Path $work 'app-ready'))) { throw 'New app did not acknowledge startup' }
    if ((Get-Content -LiteralPath (Join-Path $target 'settings.json') -Raw).Trim() -ne 'preserve this user setting') { throw 'User setting changed' }
    if (-not (Test-Path -LiteralPath (Join-Path $work 'backup/rollback.json'))) { throw 'Missing rollback record' }
    Write-Output 'PASS real updater: handshake, parent exit, replacement, app restart, startup acknowledgement, user data preservation'
} finally {
    if ($parent -and -not $parent.HasExited) { $parent.Kill() }
    if ($helper -and -not $helper.HasExited) { $helper.Kill() }
    $ready = Join-Path $work 'app-ready'
    if (Test-Path -LiteralPath $ready) {
        $childId = [int](Get-Content -LiteralPath $ready -Raw)
        $child = Get-Process -Id $childId -ErrorAction SilentlyContinue
        if ($child -and $child.Path -eq (Join-Path $target 'singC.exe')) { $child.Kill() }
    }
}
