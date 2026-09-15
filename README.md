# singC

Windows WinUI desktop client for sing-box.

## Build

Requires Windows and a .NET SDK capable of building the .NET 8 Windows target.

```powershell
dotnet build -p Platform=x64
./Scripts/Build-Release.ps1
```

Publish from a fresh checkout into a new output directory. Package the output
before running the application. Do not package a directory used to run the app,
which may contain local runtime data.

## Personal services

Open Settings > Personal services to enter the full HTTP/HTTPS URL for the
traffic endpoint. It defaults to empty; no traffic request is sent without a
configured endpoint. Credentials in URL user-info are rejected.

Settings are stored locally in `%LOCALAPPDATA%/singC/settings.json` and are not
distributed with the application. Do not put API keys or passwords into source
files or release assets.

Configure your sing-box executable and configuration file in Settings separately.
Provider API keys belong on the traffic server, not in this client.

## Automatic updates

The source and updates use the same public repository: `EvcINgithub/singC`.
Settings > Application updates shows the current version, release notes, a manual
check button, and an automatic-check switch (enabled by default, once per 24 hours).
Only stable Windows x64 releases newer than the installed version are accepted.
Downloads start after confirmation and can be cancelled before installation.

The updater checks the archive SHA-256 and every file against the package manifest.
It stops the managed proxy after downloading, waits for singC to exit, backs up
managed files, replaces them, and restarts the app. The proxy resumes if it was
running before the update. Failed file replacement or startup triggers rollback.
Settings, external sing-box binaries, and user configurations are not managed by
the updater. Keep the application in a writable directory. Recovery logs and
backups are under `%LOCALAPPDATA%/singC/updates/<operation-id>`; these support manual
recovery after interruption such as power loss.

Install version 1.0.1 manually once to obtain the updater. Earlier builds do not
have an update client. Always extract the entire release ZIP, including
`singC.Updater.exe` and `update-manifest.json`.

### Publishing

1. Set `Version` in `singC.csproj` to the next three-part version.
2. Run `dotnet run --project Tests/UpdateTests.csproj -c Release`.
3. Commit and push the changes, then push a matching tag such as `v1.0.2`.
4. The Release workflow tests, builds from a fresh source directory, and publishes
   `singC-<version>-win-x64.zip` and its `.sha256` companion to GitHub Releases.

The workflow uses GitHub's repository token; no credentials are embedded in the
client. Build-Release.ps1 also produces these files locally in `artifacts/releases`.
Do not replace assets for an existing release; publish a new version instead.

## Source and release hygiene

Do not commit build output, WebView2 data, personal configuration, certificates,
or IDE user files. If browser session data has been shared, revoke affected
sessions on the corresponding service; deleting local files does not revoke them.
