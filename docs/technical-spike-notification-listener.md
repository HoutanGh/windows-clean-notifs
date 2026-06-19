# Technical Spike: Windows Notification Listener

This spike is a terminal-only Windows notification inspector. It does not include the React UI, storage, source filtering, or Discord-specific parsing.

The inspector prints notification contents only when `--print-content` is supplied. Treat that flag as explicit debug consent because titles, bodies, and raw text elements may contain private information.

## Current Finding

The unpackaged console app can read visible Windows toast notifications by polling `UserNotificationListener.GetNotificationsAsync`.

The `UserNotificationListener.NotificationChanged` event currently fails when the app runs without package identity, so the spike falls back to polling once per second. That is enough to prove notification content capture, but it is not the final product architecture.

## Prerequisites

- Windows 11.
- Windows PowerShell.
- .NET 8 SDK available to Windows PowerShell.

Do not run the listener as a Linux/WSL process. The repo can stay in WSL, but the executable must be launched by Windows PowerShell.

## Build

From Windows PowerShell:

```powershell
dotnet publish "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\src\NotificationInspector\NotificationInspector.csproj" --configuration Debug --runtime win-x64 --self-contained true
```

This writes the Windows executable under the WSL repo:

```text
\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\src\NotificationInspector\bin\Debug\net8.0-windows10.0.19041.0\win-x64\publish\NotificationInspector.exe
```

## Check Access

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\src\NotificationInspector\bin\Debug\net8.0-windows10.0.19041.0\win-x64\publish\NotificationInspector.exe" --check-access
```

Expected shape:

```text
Package identity: not detected (...). Running unpackaged console spike.
Access status: Allowed
Notification listener access is allowed.
```

## Listen And Print

```powershell
& "\\wsl.localhost\Debian\home\houtang\GitHub\windows-clean-notifs\src\NotificationInspector\bin\Debug\net8.0-windows10.0.19041.0\win-x64\publish\NotificationInspector.exe" --listen --print-content
```

Expected startup shape:

```text
Package identity: not detected (...). Running unpackaged console spike.
Access status: Allowed
Content printing is ON for this process because --print-content was supplied.
Listening for toast notifications. Press Ctrl+C to stop.
NotificationChanged event subscription failed: COMException: ...
Falling back to polling visible toast notifications once per second.
Current toast notifications visible to listener: 0
```

For each detected toast notification, the terminal prints:

- app name;
- app id / AUMID;
- Windows notification id;
- Windows creation timestamp;
- capture timestamp;
- title;
- body/message;
- raw text elements in order.

Notifications are deduplicated in-process by app id plus Windows notification id.

## How To Verify

1. Run `--check-access` and confirm it prints an access status.
2. Run `--listen --print-content`.
3. Trigger a Windows notification from Discord.
4. Trigger notifications from at least two other apps.
5. Confirm each printed item includes app name, app id, timestamp, title/body, and raw text elements.
6. Minimize one source app and trigger another notification.
7. Send a longer plain-text notification and confirm the body remains readable.
8. Stop with `Ctrl+C`.

## Spike Boundary

This proves whether notification content is visible to a local Windows process. Because the unpackaged app currently falls back to polling, it does not prove final live-event behavior, permission UX, storage, filtering, SSE, or frontend behavior.
