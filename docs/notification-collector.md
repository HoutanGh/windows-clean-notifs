# Windows Notification Collector And Dashboard

This is a Windows notification collector and compact browser dashboard with three modes:

- terminal collector mode with `--listen`;
- loopback HTTP API and Server-Sent Events mode with `--serve`;
- compiled React dashboard served by the same `--serve` process at `http://127.0.0.1:4827/`.

It does not include `start.bat`, MSIX packaging, WebSockets, broad CORS, cloud services, telemetry, or AI filtering.

The dashboard includes an optional Discord view that derives channel presentation metadata from stored Discord notification text. That metadata is best-effort and does not affect capture, source selection, storage, deduplication, or retention.

The collector can persist enabled-source notifications to SQLite. Terminal content printing remains opt-in with `--print-content` because titles, bodies, and raw text elements may contain private information.

## Capture Finding

The unpackaged console app can read visible Windows toast notifications by polling `UserNotificationListener.GetNotificationsAsync(NotificationKinds.Toast)`.

The collector intentionally does not use `UserNotificationListener.NotificationChanged`. In the unpackaged spike that event failed during testing, while direct polling successfully captured visible notifications from multiple applications.

Polling is the V1 capture mechanism for this milestone. It can only observe notifications that are still present when a poll runs, so it must not be described as a guaranteed archive of every underlying app event.

## Database

By default, the SQLite database lives in the current Windows user's local application data folder:

```text
%LOCALAPPDATA%\WindowsCleanNotifs\notifications.db
```

In PowerShell, that is:

```powershell
Join-Path $env:LOCALAPPDATA "WindowsCleanNotifs\notifications.db"
```

The database stores:

- discovered sources, keyed by app ID / AUMID;
- source display name, enabled status, first seen timestamp, and last seen timestamp;
- notification content only for enabled sources;
- raw text elements as JSON, preserving order.

Newly discovered sources are disabled by default. Disabled sources keep source metadata only; notification title, body, and raw text are not stored.

Stored notifications are deduplicated by:

```text
app ID + Windows notification ID + creation timestamp
```

Retention cleanup deletes stored notifications older than 72 hours. It runs at startup and periodically while the collector is running. Source records are not deleted by retention cleanup.

## Prerequisites

- Windows 11.
- Windows PowerShell.
- .NET 10 SDK available to Windows PowerShell.
- Node.js 18 or newer for the React/Vite dashboard build.

The repository can stay in WSL2. This keeps one source checkout and avoids copying the project to `C:`.

The collector still runs as a Windows executable. Do not run the collector as a Linux/WSL process.

## One-Time Launcher Install

Run this once from Windows PowerShell:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\scripts\install-launchers.ps1"
```

The installer resolves the repository path from its own script location and creates:

```text
%USERPROFILE%\notifs.cmd
%USERPROFILE%\build-notifs.cmd
```

It does not modify `PATH`, the PowerShell profile, or the repository location.

If the repository path changes, rerun `scripts\install-launchers.ps1` through the repository's new Windows path.

## Normal Windows Workflow

From Windows PowerShell in the user profile:

```powershell
cd $env:USERPROFILE
.\notifs.cmd
```

This runs the published Windows executable with:

```text
--serve --open-browser
```

The terminal stays attached. Press `Ctrl+C` in that terminal to stop the collector and server together.

The default dashboard URL is:

```text
http://127.0.0.1:4827/
```

Custom port:

```powershell
.\notifs.cmd --port 4900
```

Custom polling interval:

```powershell
.\notifs.cmd --poll-interval 0.5
```

If `notifs.cmd` says the published executable is missing, run:

```powershell
.\build-notifs.cmd
```

## Build After Code Changes

From Windows PowerShell in the user profile:

```powershell
cd $env:USERPROFILE
.\build-notifs.cmd
```

The build launcher invokes:

```text
scripts\build-dashboard.ps1
```

When the repository path is a WSL UNC path, the script runs frontend `npm` commands inside that WSL distro and runs the .NET tests/publish with Windows `dotnet.exe`.

That script:

- installs frontend dependencies with `npm ci`;
- runs frontend tests;
- builds the Vite frontend;
- runs the .NET test harness;
- publishes the Windows application;
- verifies the executable and compiled frontend assets exist.

Publish output:

```text
artifacts\notification-inspector-dashboard
```

Expected runnable executable:

```text
artifacts\notification-inspector-dashboard\NotificationInspector.exe
```

Expected frontend assets:

```text
artifacts\notification-inspector-dashboard\wwwroot\index.html
artifacts\notification-inspector-dashboard\wwwroot\assets\...
```

Stop any running `NotificationInspector.exe` before rebuilding over the same output folder, because Windows locks files that are currently executing.

If the build says `NotificationInspector.exe is currently running`, stop the existing app terminal with `Ctrl+C`, then rerun:

```powershell
.\build-notifs.cmd
```

## Frontend Install And Build

The launcher build is preferred. For frontend-only development, from WSL or another shell in the repository:

```bash
cd /home/houtang/GitHub/windows-clean-notifs/src/NotificationDashboard.Web
npm install
npm run build
```

`npm install` creates `package-lock.json`. `npm run build` writes compiled dashboard assets to:

```text
src/NotificationDashboard.Web/dist
```

The .NET project copies those compiled assets into `wwwroot` during build/publish.

Run frontend tests:

```bash
cd /home/houtang/GitHub/windows-clean-notifs/src/NotificationDashboard.Web
npm test
```

Run the Vite development server while the backend is already running:

```bash
cd /home/houtang/GitHub/windows-clean-notifs/src/NotificationDashboard.Web
npm run dev
```

Vite binds to `127.0.0.1` and proxies `/api` to:

```text
http://127.0.0.1:4827
```

No broad CORS is enabled.

## Direct Backend Build

The launcher build is preferred. For direct debugging from Windows PowerShell:

```powershell
dotnet publish "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\src\NotificationInspector\NotificationInspector.csproj" --configuration Debug --runtime win-x64 --self-contained true --output "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-dashboard"
```

This writes the Windows executable to an ignored artifact folder under the WSL repo:

```text
\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-dashboard\NotificationInspector.exe
```

## Check Or Grant Access

Check access:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-dashboard\NotificationInspector.exe" --check-access
```

Expected shape when access is available:

```text
Package identity: not detected (...). Running unpackaged console collector.
Database: C:\Users\<you>\AppData\Local\WindowsCleanNotifs\notifications.db
Access status: Allowed
Notification listener access is allowed.
```

Try the built-in prompt:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-dashboard\NotificationInspector.exe" --request-access
```

If Windows does not show a useful prompt, open Settings manually:

```powershell
start ms-settings:privacy-notifications
```

Then re-run `--check-access`. If access is denied or unspecified, the collector exits with an actionable message instead of treating the feed as empty.

## Source Commands

List discovered sources:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-dashboard\NotificationInspector.exe" --list-sources
```

Enable a discovered source:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-dashboard\NotificationInspector.exe" --enable-source "com.squirrel.Discord.Discord"
```

Disable a discovered source:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-dashboard\NotificationInspector.exe" --disable-source "com.squirrel.Discord.Discord"
```

Use the exact app ID printed by `--list-sources`.

## Listen And Store

Default one-second polling without terminal content printing:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-dashboard\NotificationInspector.exe" --listen
```

Default one-second polling with enabled-source terminal content printing:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-dashboard\NotificationInspector.exe" --listen --print-content
```

Include raw fields and Unicode diagnostics for enabled-source notifications:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-dashboard\NotificationInspector.exe" --listen --print-content --debug-raw
```

Custom interval, in seconds:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-dashboard\NotificationInspector.exe" --listen --print-content --poll-interval 0.5
```

Expected startup shape:

```text
Package identity: not detected (...). Running unpackaged console collector.
Database: C:\Users\<you>\AppData\Local\WindowsCleanNotifs\notifications.db
Access status: Allowed
Content printing is OFF. Enabled-source notifications will be stored but not printed.
Newly discovered sources are disabled by default.
Polling visible toast notifications every 1 seconds.
Press Ctrl+C to stop.

Startup snapshot visible to listener: 0
```

The startup snapshot seeds deduplication and source discovery. Existing notifications visible at startup can discover sources, but are not stored as newly captured notifications.

When a previously unknown application is inserted into the database, the collector prints:

```text
----
Source discovered
App name: Example App
App id: example.app.id
```

For enabled sources only, newly detected notifications are stored. If `--print-content` is supplied, those enabled-source notifications are also printed in concise display form:

```text
14:32:08 · Discord
Scanner Bot
NVDA breaking premarket high
```

Normal `--print-content` output intentionally excludes app ID, Windows notification ID, raw text indexes, event labels, and captured/created timestamp diagnostics. Use `--debug-raw` with `--print-content` to print the raw title, raw body, raw text elements, derived display fields, and Unicode code-point diagnostics.

Disabled-source notification content is neither stored nor printed.

Stop with `Ctrl+C`. Shutdown should print `Stopped.` without an unhandled exception.

## Serve The Local Dashboard And API

Server mode starts the polling collector, local ASP.NET Core API, Server-Sent Events stream, and compiled React dashboard in one process.

Default server mode:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-dashboard\NotificationInspector.exe" --serve
```

Open the dashboard in the default Windows browser after the server starts:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-dashboard\NotificationInspector.exe" --serve --open-browser
```

Expected startup shape:

```text
Package identity: not detected (...). Running unpackaged console collector.
Database: C:\Users\<you>\AppData\Local\WindowsCleanNotifs\notifications.db
Access status: Allowed
Content printing is OFF. Enabled-source notifications will be stored but not printed.
Newly discovered sources are disabled by default.
Polling visible toast notifications every 1 seconds.
Local dashboard: http://127.0.0.1:4827
Local API: http://127.0.0.1:4827/api/health
Press Ctrl+C to stop.
```

Override the port:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-dashboard\NotificationInspector.exe" --serve --port 4828
```

Override the polling interval:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-dashboard\NotificationInspector.exe" --serve --poll-interval 0.5
```

Server mode binds only to loopback:

```text
http://127.0.0.1:<port>
```

It does not bind to `0.0.0.0`, does not expose notification data to the LAN, does not enable unrestricted CORS, and does not serve arbitrary files.

Open the dashboard at:

```text
http://127.0.0.1:4827/
```

API routes remain under `/api`. Non-file browser routes fall back to `index.html` for the React app. File-like paths that are not compiled dashboard assets are not served.

If `wwwroot\index.html` is missing from the executable output, `/` returns a clear message telling you to run:

```bash
cd /home/houtang/GitHub/windows-clean-notifs/src/NotificationDashboard.Web
npm install
npm run build
```

Then publish or run `--serve` again.

If the port is already in use, startup prints a clear port-conflict message and exits with a non-zero status. It does not terminate other processes.

Example recovery:

```powershell
.\notifs.cmd --port 4900
```

## Browser Dashboard Behaviour

The dashboard is a compact single page with:

- a merged newest-first feed from enabled sources;
- an optional Discord view with channel columns when Discord is enabled;
- browser-local hide/show controls for Discord channel columns;
- a browser-local hidden-controls mode that reveals dashboard controls from a small right-edge hover/focus rail;
- explicit Light and Night theme controls stored in browser local storage;
- live updates through `GET /api/events`;
- a Sources panel backed by `GET /api/sources` and `PUT /api/sources/selection`;
- a `Load older` button backed by `GET /api/notifications?limit=100&beforeId=<oldest-id>`.

The UI renders only the API display fields:

```text
id
appId
sourceApp
timestamp
primaryText
messageText
discord
```

The optional `discord` object contains best-effort display metadata for Discord notifications. It is used only for grouping in the Discord view.

The UI does not render raw text elements, Windows notification IDs, captured timestamps, debug fields, or backend mapping internals.

On startup, the dashboard calls:

```text
GET /api/health
GET /api/notifications?limit=100
GET /api/sources
```

It records the highest loaded notification ID and connects to:

```text
GET /api/events?afterId=<highest-loaded-id>
```

If a source is enabled or disabled in the Sources panel, the dashboard reloads the source list, reloads notifications from the API, removes notifications from disabled sources, and reconnects SSE from the highest remaining notification ID.

Disabled-source notification content is still protected by the backend: disabled sources may be discovered and listed, but their title, body, and raw text are not stored or returned by the API.

## API Endpoints

All JSON uses camelCase.

### GET /api/health

PowerShell:

```powershell
Invoke-RestMethod http://127.0.0.1:4827/api/health
```

Example response:

```json
{
  "status": "ok",
  "listenerAccessStatus": "Allowed",
  "collectorRunning": true,
  "pollingInterval": "00:00:01",
  "retentionPeriod": "3.00:00:00"
}
```

### GET /api/sources

PowerShell:

```powershell
Invoke-RestMethod http://127.0.0.1:4827/api/sources
```

Example response:

```json
[
  {
    "appId": "com.squirrel.Discord.Discord",
    "displayName": "Discord",
    "enabled": true,
    "firstSeenAt": "2026-06-21T13:00:00.0000000Z",
    "lastSeenAt": "2026-06-21T13:30:00.0000000Z"
  }
]
```

Sources are sorted with enabled sources first, then alphabetically by display name.

### PUT /api/sources/selection

PowerShell:

```powershell
Invoke-RestMethod `
  -Method Put `
  -Uri http://127.0.0.1:4827/api/sources/selection `
  -ContentType "application/json" `
  -Body '{"appId":"com.squirrel.Discord.Discord","enabled":true}'
```

Example response:

```json
{
  "appId": "com.squirrel.Discord.Discord",
  "displayName": "Discord",
  "enabled": true,
  "firstSeenAt": "2026-06-21T13:00:00.0000000Z",
  "lastSeenAt": "2026-06-21T13:30:00.0000000Z"
}
```

Unknown app IDs return `404`. Malformed requests return `400`.

### GET /api/notifications

PowerShell:

```powershell
Invoke-RestMethod "http://127.0.0.1:4827/api/notifications?limit=100"
```

Cursor pagination:

```powershell
Invoke-RestMethod "http://127.0.0.1:4827/api/notifications?limit=100&beforeId=1234"
```

Rules:

- default `limit` is `100`;
- maximum `limit` is `500`;
- `beforeId` returns notifications with a lower SQLite notification ID;
- results are newest first;
- only currently enabled sources are returned;
- raw text elements and diagnostic metadata are not exposed.

Example response:

```json
[
  {
    "id": 1235,
    "appId": "com.squirrel.Discord.Discord",
    "sourceApp": "Discord",
    "timestamp": "2026-06-21T13:32:08.0000000Z",
    "primaryText": "Scanner Bot",
      "messageText": "NVDA breaking premarket high",
      "discord": {
        "sender": "Scanner Bot",
        "context": "Main Chat",
        "channel": "#stocks-and-options",
        "confidence": "parsed"
      }
  }
]
```

`sourceApp`, `timestamp`, `primaryText`, and `messageText` are produced through `NotificationDisplayMapper`. `discord` is `null` for non-Discord notifications. For Discord notifications that cannot be parsed, `discord.confidence` is `unknown` and the UI places the item in a fallback group.

### GET /api/events

Server-Sent Events stream for newly stored notifications.

PowerShell/curl:

```powershell
curl.exe -N http://127.0.0.1:4827/api/events
```

Replay newer stored notifications before continuing live:

```powershell
curl.exe -N "http://127.0.0.1:4827/api/events?afterId=1234"
```

Event shape:

```text
event: notification
id: 1235
data: {"id":1235,"appId":"com.squirrel.Discord.Discord","sourceApp":"Discord","timestamp":"2026-06-21T13:32:08.0000000Z","primaryText":"Scanner Bot","messageText":"NVDA breaking premarket high","discord":{"sender":"Scanner Bot","context":"Main Chat","channel":"#stocks-and-options","confidence":"parsed"}}
```

SSE behaviour:

- publishes only after a genuinely new notification is successfully stored;
- streams only currently enabled sources;
- uses the same response model and `NotificationDisplayMapper` as `GET /api/notifications`;
- `afterId` replays stored notifications with a higher SQLite notification ID before live events;
- if `afterId` is absent, the `Last-Event-ID` request header is used for replay;
- if both are present, `afterId` takes precedence;
- subscriber queues are bounded;
- heartbeat comments are sent periodically;
- client disconnects clean up subscribers.

## Automated Tests

The collector and storage logic are tested without real Windows notifications.

From Windows PowerShell:

```powershell
dotnet run --project "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\tests\NotificationInspector.Tests\NotificationInspector.Tests.csproj"
```

The tests cover:

- duplicate snapshots;
- newly appearing notifications;
- multiple notifications from one app;
- multiple applications;
- notifications disappearing from later snapshots;
- bounded in-memory dedupe cleanup;
- newly discovered sources defaulting to disabled;
- source metadata surviving database reopen;
- enabling and disabling source selection;
- rediscovery updating metadata without resetting selection;
- disabled-source content not being stored;
- enabled-source content being stored;
- duplicate notification insert protection;
- raw text element round-trip order;
- startup snapshot notifications not being stored;
- retention deleting old notifications while preserving sources;
- derived display mapping and fallback rules;
- preservation of URLs, tickers, prices, percentages, emoji, symbols, and multiline text in display mapping;
- normal terminal output excluding debug metadata;
- debug raw output including raw values and Unicode code-point diagnostics;
- health endpoint;
- source listing and source selection API;
- notification pagination and enabled-source filtering;
- API display mapping and raw-field exclusion;
- SSE live delivery, `afterId` and `Last-Event-ID` replay, disabled-source exclusion, and subscriber cleanup;
- compiled frontend serving at `/`, SPA fallback, API route preservation, and repository-file non-exposure;
- `--open-browser` argument handling, one-shot browser opening, non-fatal browser launch failure, and actionable occupied-port messaging.

Frontend tests are run separately from the dashboard folder:

```bash
cd /home/houtang/GitHub/windows-clean-notifs/src/NotificationDashboard.Web
npm test
```

They cover initial loading, rendering, missing fields, multiline text, newest-first ordering, SSE insertion/deduplication/cleanup, loading older notifications, source toggles, API errors, and raw diagnostic field exclusion.

## Manual Verification

From Windows PowerShell starting in `C:\Users\<user>`:

1. Run `& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\scripts\install-launchers.ps1"` once.
2. Confirm `%USERPROFILE%\notifs.cmd` and `%USERPROFILE%\build-notifs.cmd` exist.
3. Run `.\build-notifs.cmd`.
4. Confirm `artifacts\notification-inspector-dashboard\NotificationInspector.exe` exists.
5. Confirm `artifacts\notification-inspector-dashboard\wwwroot\index.html` and `wwwroot\assets\...` exist.
6. Run `.\notifs.cmd`.
7. Confirm the dashboard opens at `http://127.0.0.1:4827/`.
8. Open Sources and enable Discord or another detected source.
9. Trigger an enabled-source notification and confirm it appears once at the top of the feed without refreshing.
10. Press `Ctrl+C` and confirm the server and collector stop cleanly.
11. Run `.\notifs.cmd --port 4900` and confirm `http://127.0.0.1:4900/` works.
12. Run `.\notifs.cmd --poll-interval 0.5` and confirm startup reports the custom interval.
13. Occupy a port, run `.\notifs.cmd --port <occupied-port>`, and confirm the error is concise and suggests another port.
14. If the repository path changes, rerun `scripts\install-launchers.ps1` through the repository's new Windows path and confirm the wrappers point to the new location.

Because disabled-source content is intentionally not stored, use the automated tests to verify the SQLite privacy rule directly.
