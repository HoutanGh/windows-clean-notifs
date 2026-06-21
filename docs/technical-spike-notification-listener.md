# Technical Spike: Polling Windows Notification Collector

This is a terminal-only Windows notification collector. It does not include the React UI, HTTP API, storage, source filtering, Discord-specific parsing, MSIX packaging, or AI filtering.

The collector prints notification contents only when `--print-content` is supplied. Treat that flag as explicit debug consent because titles, bodies, and raw text elements may contain private information.

## Current Finding

The unpackaged console app can read visible Windows toast notifications by polling `UserNotificationListener.GetNotificationsAsync(NotificationKinds.Toast)`.

The collector intentionally does not use `UserNotificationListener.NotificationChanged`. In the unpackaged spike that event failed during testing, while direct polling successfully captured visible notifications from multiple applications.

Polling is the V1 capture mechanism for this milestone. It can only observe notifications that are still present when a poll runs, so it must not be described as a guaranteed archive of every underlying app event.

## Prerequisites

- Windows 11.
- Windows PowerShell.
- .NET 8 SDK available to Windows PowerShell.

The repository can stay in WSL, but the executable must be built and launched by Windows PowerShell. Do not run the listener as a Linux/WSL process.

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

## Check Access

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-polling\NotificationInspector.exe" --check-access
```

Expected shape when access is available:

```text
Package identity: not detected (...). Running unpackaged console collector.
Access status: Allowed
Notification listener access is allowed.
```

## Grant Access

Try the built-in prompt first:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-polling\NotificationInspector.exe" --request-access
```

If Windows does not show a useful prompt, open Settings manually:

```powershell
start ms-settings:privacy-notifications
```

Then re-run `--check-access`. If access is denied or unspecified, the collector exits with an actionable message instead of treating the feed as empty.

## Listen And Print

Default one-second polling:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-polling\NotificationInspector.exe" --listen --print-content
```

Custom interval, in seconds:

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\artifacts\notification-inspector-polling\NotificationInspector.exe" --listen --print-content --poll-interval 0.5
```

Expected startup shape:

```text
Package identity: not detected (...). Running unpackaged console collector.
Access status: Allowed
Content printing is ON for this process because --print-content was supplied.
Polling visible toast notifications every 1 seconds.
Press Ctrl+C to stop.

Startup snapshot visible to listener: 0
```

The startup snapshot seeds deduplication and source discovery. Existing notifications visible at startup are not printed as newly received notifications.

When a previously unseen application is detected, the collector prints:

```text
----
Source discovered
Event: poll
App name: Example App
App id: example.app.id
```

For each newly detected notification after startup, the collector prints:

- app display name;
- app ID / AUMID;
- Windows notification ID;
- creation timestamp;
- title;
- body/message;
- raw text elements in order.

Stop with `Ctrl+C`. Shutdown should print `Stopped.` without an unhandled exception.

## Deduplication

Notification identity is:

```text
app ID + Windows notification ID + creation timestamp
```

The collector does not print the same identity repeatedly on every poll. Dedupe state is kept in memory and pruned after an identity has not appeared for 10 minutes.

Applications are deduplicated by app ID, not display name.

## Manual Verification

1. Run `--check-access` and confirm it prints `Allowed`.
2. Run `--listen --print-content`.
3. Leave any existing notifications visible before startup and confirm they do not print as `New notification`.
4. Trigger a notification from one app and confirm one `Source discovered` message plus one `New notification`.
5. Leave that notification visible for several polls and confirm it is not printed repeatedly.
6. Trigger multiple notifications from the same app and confirm only one source discovery appears for that app ID.
7. Trigger notifications from at least two apps and confirm each app ID is discovered separately.
8. Trigger a long plain-text notification and confirm the body and raw text elements remain readable.
9. Stop with `Ctrl+C` and confirm shutdown is clean.

## Automated Tests

The collector logic is tested without real Windows notifications through `INotificationSnapshotProvider`.

From Windows PowerShell:

```powershell
dotnet run --project "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\tests\NotificationInspector.Tests\NotificationInspector.Tests.csproj"
```

The tests cover duplicate snapshots, newly appearing notifications, multiple notifications from one app, multiple applications, notifications disappearing from later snapshots, and bounded deduplication cleanup.
