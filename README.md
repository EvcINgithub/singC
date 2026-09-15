# singC

Windows WinUI desktop client for sing-box.

## Build

Requires Windows and a .NET SDK capable of building the .NET 8 Windows target.

```powershell
dotnet build -p Platform=x64
dotnet publish -p Platform=x64 -c Release -o artifacts/publish-win-x64
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

## Source and release hygiene

Do not commit build output, WebView2 data, personal configuration, certificates,
or IDE user files. If browser session data has been shared, revoke affected
sessions on the corresponding service; deleting local files does not revoke them.
