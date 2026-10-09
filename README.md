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

## Quick mode switching

The Home page offers **跟随配置**, **TUN**, and **系统代理**. The default follows
the original configuration. Select a mode while stopped to use it on the next
start, or switch while running to validate the new configuration and restart
sing-box automatically. Connections are briefly interrupted. Startup or settings
save failures restore the previous runtime configuration. TUN requires running
singC as administrator; insufficient privileges are detected before stopping the
current process.

System proxy mode replaces the TUN inbound with a loopback HTTP/SOCKS mixed
listener, retaining the TUN tag for inbound-based rules. Its default port is
7890 and can be changed with **应用端口**. If a port is occupied, choose another
port. Configurations with multiple TUN inbounds require manual consolidation.
Windows HTTP/HTTPS proxy settings are enabled only after the listener is ready.
TUN mode temporarily disables Windows manual/PAC/auto-detect proxying. Stopping,
switching or a core exit restores the prior Windows settings; settings changed
by another application are preserved. Only applications that honor Windows
system proxy settings use system proxy mode automatically.

The source JSON is not modified. Temporary configurations live under
`%LOCALAPPDATA%/singC/runtime` and use the source configuration's working directory
for relative paths. Mode and port are remembered locally. An exclusive recovery
journal at `%LOCALAPPDATA%/singC/system-proxy-backup.json` prevents concurrent
ownership and restores abandoned settings on the next launch after a crash.
When adding a missing TUN inbound, singC uses the current
[TUN configuration fields](https://sing-box.sagernet.org/configuration/inbound/tun/)
and checks the result using the configured sing-box executable. See also the
[mixed inbound documentation](https://sing-box.sagernet.org/configuration/inbound/mixed/).

## Local traffic statistics

Open **流量统计** to view upload/download speeds, usage for the current sing-box
run, today, this month, all recorded time, and the last seven days. Collection
continues when switching pages. Enable sing-box's Clash API in the configuration.
Collection uses the controller address and optional secret from the running
configuration, including custom ports. Connection snapshots are limited to 16 MiB.

Statistics use the API's cumulative upload/download counters, including closed
connections, direct traffic and proxy traffic. They are not provider billing
figures or system-wide network usage. Speeds are calculated from consecutive
samples; reconnects retain the counter baseline and stale samples show zero speed.
The first sample includes traffic since the managed sing-box process started.

Daily history is stored in `%LOCALAPPDATA%/singC/traffic-statistics.json`, saved
every 15 seconds when traffic changes and flushed on stop/exit. An abrupt exit
may lose unsaved samples. Dates use local time; increments spanning midnight
or a connection gap are assigned to the date they are received. Traffic after
the last received sample cannot be recovered if the core stops before another
sample arrives. Restarting sing-box starts a new session and preserves history.

## Network diagnostics

Open **网络测试** and select **一键诊断** to check the selected URL and, by default,
Baidu, GitHub and Google in parallel. Results appear as each probe completes.
Disable the comparison checkbox to test only your URL. The host and port are
derived from that URL. The selected target and any comparison site that fails
to respond also receive DNS and TCP checks under **连接与环境详情**.

HTTP error responses such as 403, 429 and 502 are shown as website warnings,
with suggestions, separately from connection failures. Advanced options control
the timeout and optional exit-IP lookup. Neither a stopped sing-box process nor
an unavailable Clash API prevents website testing. The controller check reads
the endpoint and secret from the selected sing-box configuration.

**连续请求 10 次** measures only the selected URL, showing HTTP 2xx success rate
and successful response times. Each request starts a new connection; timing ends
at the response headers. This is not a bandwidth test or an ICMP packet-loss test.
HTTP follows the system proxy and current TUN/routing configuration; DNS/TCP use
the system network. These checks cannot prove a request passed through a proxy,
and the exit-IP result applies only to the queried service.

Cancellation preserves completed results. Reports capture the tested settings,
not subsequent input edits; the last ten reports can be reopened from history.

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
Version checks download `update.json` from the latest Release directly, avoiding
GitHub REST API anonymous rate limits. No GitHub login is needed.
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

Install the latest release manually once to obtain the updater. Earlier builds do not
have an update client. Always extract the entire release ZIP, including
`singC.Updater.exe` and `update-manifest.json`.

### Publishing

1. Set `Version` in `singC.csproj` to the next three-part version and update `RELEASE_NOTES.md`.
2. Run `dotnet run --project Tests/UpdateTests.csproj -c Release`.
3. Commit and push the changes, then push a matching tag such as `v1.0.2`.
4. The Release workflow tests, builds from a fresh source directory, and publishes
   `singC-<version>-win-x64.zip`, its `.sha256` companion, and `update.json` to GitHub Releases.

The workflow uses GitHub's repository token; no credentials are embedded in the
client. Build-Release.ps1 also produces these files locally in `artifacts/releases`.
Do not replace assets for an existing release; publish a new version instead.

## Source and release hygiene

Do not commit build output, WebView2 data, personal configuration, certificates,
or IDE user files. If browser session data has been shared, revoke affected
sessions on the corresponding service; deleting local files does not revoke them.
