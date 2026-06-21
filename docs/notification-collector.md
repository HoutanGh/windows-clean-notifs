# Windows Notification Collector

This is a Windows notification collector with two modes:

- terminal collector mode with `--listen`;
- loopback HTTP API and Server-Sent Events mode with `--serve`.

It does not include the React UI, browser assets, `start.bat`, Discord-specific parsing, MSIX packaging, or AI filtering.

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

The repository can stay in WSL, but the executable must be built and launched by Windows PowerShell. Do not run the collector as a Linux/WSL process.

## Build

From Windows PowerShell:

```powershell
dotnet publish "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\src\NotificationInspector\NotificationInspector.csproj" --configuration Debug --runtime win-x64 --self-contained true --output "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-polling"
```

This writes the Windows executable to an ignored artifact folder under the WSL repo:

```text
\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-polling\NotificationInspector.exe
```

Stop any running `NotificationInspector.exe` before publishing over the same output folder, because Windows locks files that are currently executing.

## Check Or Grant Access

Check access:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-polling\NotificationInspector.exe" --check-access
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
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-polling\NotificationInspector.exe" --request-access
```

If Windows does not show a useful prompt, open Settings manually:

```powershell
start ms-settings:privacy-notifications
```

Then re-run `--check-access`. If access is denied or unspecified, the collector exits with an actionable message instead of treating the feed as empty.

## Source Commands

List discovered sources:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-polling\NotificationInspector.exe" --list-sources
```

Enable a discovered source:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-polling\NotificationInspector.exe" --enable-source "com.squirrel.Discord.Discord"
```

Disable a discovered source:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-polling\NotificationInspector.exe" --disable-source "com.squirrel.Discord.Discord"
```

Use the exact app ID printed by `--list-sources`.

## Listen And Store

Default one-second polling without terminal content printing:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-polling\NotificationInspector.exe" --listen
```

Default one-second polling with enabled-source terminal content printing:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-polling\NotificationInspector.exe" --listen --print-content
```

Include raw fields and Unicode diagnostics for enabled-source notifications:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-polling\NotificationInspector.exe" --listen --print-content --debug-raw
```

Custom interval, in seconds:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-polling\NotificationInspector.exe" --listen --print-content --poll-interval 0.5
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

## Serve The Local API

Server mode starts the same polling collector and a local ASP.NET Core minimal API in one process.

Default server mode:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-polling\NotificationInspector.exe" --serve
```

Expected startup shape:

```text
Package identity: not detected (...). Running unpackaged console collector.
Database: C:\Users\<you>\AppData\Local\WindowsCleanNotifs\notifications.db
Access status: Allowed
Content printing is OFF. Enabled-source notifications will be stored but not printed.
Newly discovered sources are disabled by default.
Polling visible toast notifications every 1 seconds.
Local API: http://127.0.0.1:4827
Press Ctrl+C to stop.
```

Override the port:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-polling\NotificationInspector.exe" --serve --port 4828
```

Override the polling interval:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-polling\NotificationInspector.exe" --serve --poll-interval 0.5
```

Server mode binds only to loopback:

```text
http://127.0.0.1:<port>
```

It does not bind to `0.0.0.0`, does not expose notification data to the LAN, does not enable unrestricted CORS, and does not serve arbitrary files.

If the port is already in use, startup prints a clear port-conflict message and exits.

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
    "messageText": "NVDA breaking premarket high"
  }
]
```

`sourceApp`, `timestamp`, `primaryText`, and `messageText` are produced through `NotificationDisplayMapper`.

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
data: {"id":1235,"appId":"com.squirrel.Discord.Discord","sourceApp":"Discord","timestamp":"2026-06-21T13:32:08.0000000Z","primaryText":"Scanner Bot","messageText":"NVDA breaking premarket high"}
```

SSE behaviour:

- publishes only after a genuinely new notification is successfully stored;
- streams only currently enabled sources;
- uses the same response model and `NotificationDisplayMapper` as `GET /api/notifications`;
- `afterId` replays stored notifications with a higher SQLite notification ID before live events;
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
- debug raw output including raw values and Unicode code-point diagnostics.
- health endpoint;
- source listing and source selection API;
- notification pagination and enabled-source filtering;
- API display mapping and raw-field exclusion;
- SSE live delivery, `afterId` replay, disabled-source exclusion, and subscriber cleanup.

## Manual Verification

1. Build the executable with the publish command above.
2. Run `--check-access` and confirm it prints `Allowed`.
3. Run `--listen` with no `--print-content`.
4. Trigger a Discord notification and one notification from another app.
5. Stop with `Ctrl+C`.
6. Run `--list-sources` and confirm both apps appear as `Enabled: false`.
7. Enable Discord using the exact Discord app ID from `--list-sources`.
8. Run `--listen --print-content`.
9. Trigger a new Discord notification and confirm it prints once in concise display form.
10. Trigger a notification from the still-disabled other app and confirm its content does not print.
11. Leave the same Discord toast visible for several polls and confirm it does not print repeatedly.
12. Trigger rapid Discord notifications and confirm each visible notification appears once.
13. Run with `--listen --print-content --debug-raw`, trigger one enabled-source notification, and confirm raw fields plus Unicode code points print only in debug mode.
14. Start server mode with `--serve`.
15. Open `http://127.0.0.1:4827/api/health` and confirm JSON is returned.
16. Run `curl.exe -N http://127.0.0.1:4827/api/events`.
17. Trigger an enabled-source notification and confirm one `event: notification` SSE event is printed.
18. Stop with `Ctrl+C` and confirm shutdown is clean.

Because disabled-source content is intentionally not stored, use the automated tests to verify the SQLite privacy rule directly.
