# Technical Spike: Polling Windows Notification Collector

This is a terminal-only Windows notification collector. It does not include the React UI, HTTP API, SSE, Discord-specific parsing, MSIX packaging, or AI filtering.

The collector can persist enabled-source notifications to SQLite. Terminal content printing remains opt-in with `--print-content` because titles, bodies, and raw text elements may contain private information.

## Current Finding

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
- .NET SDK available to Windows PowerShell.

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
14. Stop with `Ctrl+C` and confirm shutdown is clean.

Because disabled-source content is intentionally not stored, use the automated tests to verify the SQLite privacy rule directly.
