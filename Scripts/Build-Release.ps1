param([string]$Version)
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path $PSScriptRoot -Parent
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $sourceRoot 'singC.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
$Version = $Version.TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version' }
$projectVersion = ([xml](Get-Content (Join-Path $sourceRoot 'singC.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($projectVersion -ne $Version) { throw 'Release tag must match singC.csproj Version' }
$buildRoot = Join-Path $sourceRoot ('artifacts/release-' + $Version + '-' + [guid]::NewGuid().ToString('N'))
$checkout = Join-Path $buildRoot 'source'
$publish = Join-Path $buildRoot 'publish'
$helperPublish = Join-Path $buildRoot 'updater'
New-Item -ItemType Directory -Path $checkout -Force | Out-Null
$rootFiles = @('.gitignore', 'README.md', 'App.xaml', 'App.xaml.cs', 'MainWindow.xaml', 'MainWindow.xaml.cs', 'app.manifest', 'Package.appxmanifest', 'singC.csproj')
foreach ($name in $rootFiles) { Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination $checkout }
foreach ($folder in @('Assets', 'Converters', 'Helper', 'Models', 'Pages', 'Properties', 'UpdateCore', 'Updater')) {
    $folderRoot = Join-Path $sourceRoot $folder
    foreach ($file in Get-ChildItem -LiteralPath $folderRoot -Recurse -File) {
        $relative = $file.FullName.Substring($sourceRoot.Length + 1)
        if ($relative -match '(^|[\\/])(bin|obj|artifacts)([\\/]|$)' -or $relative -like '*.user') { continue }
        $target = Join-Path $checkout $relative
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
dotnet publish (Join-Path $checkout 'singC.csproj') -p Platform=x64 -c Release -o $publish -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed' }
dotnet publish (Join-Path $checkout 'Updater/singC.Updater.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $helperPublish
if ($LASTEXITCODE -ne 0) { throw 'Updater publish failed' }
Copy-Item -LiteralPath (Join-Path $helperPublish 'singC.Updater.exe') -Destination $publish
$entries = @(Get-ChildItem -LiteralPath $publish -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($publish.Length + 1).Replace('\', '/')
    if ($relative -match '\.exe\.WebView2/|\.(pdb|db|user|pfx|key)$|(^|/)(settings|config)\.json$') { throw 'Runtime or private data in publish output' }
    @{ Path = $relative; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
@{ Version = $Version; Architecture = 'win-x64'; Files = $entries } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $publish 'update-manifest.json') -Encoding utf8
$releaseDir = Join-Path $sourceRoot 'artifacts/releases'
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
$asset = Join-Path $releaseDir "singC-$Version-win-x64.zip"
if (Test-Path -LiteralPath $asset) { throw 'Release asset already exists; do not overwrite a published version' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$zipStream = [System.IO.File]::Open($asset, [System.IO.FileMode]::CreateNew)
try {
    $archive = [System.IO.Compression.ZipArchive]::new($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $publish -Recurse -File) {
            $entryName = $file.FullName.Substring($publish.Length + 1).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archive.Dispose() }
} finally { $zipStream.Dispose() }
((Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + (Split-Path $asset -Leaf)) | Set-Content -LiteralPath ($asset + '.sha256') -Encoding ascii
$assetName = Split-Path $asset -Leaf
$releaseAssets = @($assetName, ($assetName + '.sha256')) | ForEach-Object {
    @{ name = $_; browser_download_url = "https://github.com/EvcINgithub/singC/releases/download/v$Version/$_" }
}
$notesPath = Join-Path $sourceRoot 'RELEASE_NOTES.md'
$notes = if (Test-Path -LiteralPath $notesPath) { [System.IO.File]::ReadAllText($notesPath) } else { "singC $Version" }
@{ tag_name = "v$Version"; draft = $false; prerelease = $false; body = $notes; assets = @($releaseAssets) } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $releaseDir 'update.json') -Encoding utf8
Write-Output "Release package: $asset"
