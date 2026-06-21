# Local Windows Notification Dashboard — MVP Product Spec

## Product goal
Build a lightweight, local Windows 11 dashboard that displays notifications only from applications selected by the user.

**Primary personal use case:** Keep Discord minimised while viewing its Windows notifications in a clean browser feed or Discord-focused view.

**Success condition:** I can work with Discord closed to the taskbar or minimised and still see its notifications in a compact live feed.

The product remains a local Windows notification dashboard first. Capture, source selection, storage, retention, and privacy must stay generic. Discord may receive an optional enhanced presentation because Discord Windows notifications expose useful sender, channel, and server text.

## Core user flow

1. The user runs `start.bat` in a terminal.
2. The local collector and web server start.
3. The terminal prints a URL such as `http://localhost:4827`.
4. The user opens the URL in a normal browser tab.
5. On first use, the app requests Windows notification-access permission.
6. The user opens **Sources** and enables applications such as Discord.
7. New notifications from enabled applications appear automatically.
8. The user can use the generic **Feed** view for all enabled sources.
9. When Discord is enabled, the user can switch to a Discord-focused view with server tabs and channel columns.

Closing the browser tab does not stop collection. Closing the terminal process stops the application.

## Source discovery and filtering

The product does not begin with a fixed list of installed applications.

- On startup, inspect notifications currently available through Windows.
- Listen for newly added Windows notifications while running.
- When an unseen application sends a notification, add it to the source list.
- Deduplicate applications using their Windows application identifier.
- New sources are disabled by default.
- Store only source metadata for disabled applications, not their notification content.
- Store and display notification content only for enabled applications.
- Source selections persist after restart.

Example Sources panel:

```text
Selected
☑ Discord

Other detected apps
☐ Outlook
☐ Teams
☐ Spotify
```

The list grows only when a previously unseen application produces a Windows notification.

## Views

The app has a generic notification view and may have source-aware enhanced views.

### Generic Feed

Use one merged, vertically scrolling feed for all enabled applications.

Each notification shows:

- exact local arrival time, including seconds;
- application name;
- notification title, when available;
- full available notification body.

Behaviour:

- Newest notifications appear at the top.
- Text wraps and is not intentionally truncated.
- New items appear without refreshing the page.
- No avatars, images, reactions, reply controls, buttons, pop-ups, or click-through actions.
- Non-Discord notifications remain in the generic feed unless a future source-aware view is explicitly specified.

Example:

```text
14:32:08 · Discord
Scanner Bot
NVDA breaking premarket high

14:30:41 · Outlook
Meeting reminder
Trading review begins in 10 minutes
```

### Discord View

When Discord is enabled, the app may offer an explicit **Discord** view. This view is presentation-only and derives structure from stored Discord notification text.

Layout:

- Discord server names appear as tabs.
- Within the selected server tab, channels appear as columns.
- Each column shows newest-first notifications for that channel.
- Direct messages, group DMs, or unparsable Discord notifications appear in an **Ungrouped** or equivalent fallback area.
- Switching view modes does not change source selection, storage, retention, or notification capture.

Derived Discord fields:

```ts
type DiscordNotificationContext = {
  sender?: string;
  server?: string;
  channel?: string;
  confidence: "parsed" | "unknown";
};
```

The parser is best-effort. It may use observed Discord Windows notification title shapes such as:

```text
Sender (#channel, Server Name)
```

Parsing rules:

- Preserve raw captured notification data unchanged.
- Derive Discord context only when reading data for API, SSE, terminal display, or UI display.
- Do not require Discord context for storing or deduplicating notifications.
- Do not hide or drop notifications when parsing fails.
- Do not rely on Discord server IDs, channel IDs, bot tokens, or Discord APIs in V1.
- Treat server and channel names as display labels, not stable identifiers.
- Strip invisible formatting marks only from derived grouping keys when needed; do not mutate stored raw text.

Discord view limitations:

- Discord can change Windows notification text formatting.
- Server or channel renames may create new display groups.
- Channels with the same display name in the same server may be indistinguishable.
- The view only contains notifications that Windows exposes and the collector captures.

## Retention and privacy

- Store data locally in SQLite.
- Default retention: 72 hours.
- Default maximum: 2,000 notification records.
- Delete expired records automatically.
- Bind the web server to loopback only; do not expose it to the local network.
- No account, cloud sync, analytics, telemetry, or external API calls.

## Technical architecture

```text
Windows notifications
  → C#/.NET notification collector
  → local SQLite store
  → local HTTP API and Server-Sent Events
  → React/TypeScript frontend on localhost
```

Recommended components:

- **Collector:** C#/.NET using Windows `UserNotificationListener`.
- **Permission:** packaged Windows component with the User Notification Listener capability and one-time user approval.
- **Backend:** local .NET HTTP server.
- **Live updates:** Server-Sent Events.
- **Frontend:** React + TypeScript + Vite, compiled and served by the backend.
- **Storage:** SQLite.

The collector should read current notifications on startup and resynchronise when Windows reports that a notification was added or removed.

## Data model

```ts
type NotificationSource = {
  sourceId: string;
  displayName: string;
  enabled: boolean;
  firstSeenAt: string;
  lastSeenAt: string;
};

type NotificationRecord = {
  id: string;
  sourceId: string;
  sourceName: string;
  windowsNotificationId: number;
  receivedAt: string;
  title?: string;
  body?: string;
  rawText: string[];
  discord?: DiscordNotificationContext;
};
```

Use the application identifier plus Windows notification ID for deduplication.

## Minimal API

```text
GET  /api/sources
PUT  /api/sources/:sourceId
GET  /api/notifications
GET  /api/stream
GET  /api/health
```

`PUT /api/sources/:sourceId` enables or disables that application.

## Required technical spike

Before polishing the UI, build a raw notification inspector that records:

- originating application identifier and display name;
- Windows notification ID;
- creation or capture time;
- all available text elements in order.

Test with Discord and at least two other applications. Confirm that:

- each application can be identified consistently;
- repeated notifications are not duplicated;
- notifications still arrive while the source app is minimised;
- long plain-text notifications remain readable;
- rapid notifications appear in the correct order.

## Reliability boundary

The dashboard displays notifications that applications successfully publish to Windows and that Windows exposes to the listener. It is not a complete application-message archive and must not promise guaranteed capture of every underlying event or message.

## Acceptance criteria

- `start.bat` starts the collector, API, database, and frontend, then prints the localhost URL.
- First use provides a clear Windows permission flow.
- A previously unseen notifying application appears once in Sources and is disabled by default.
- Enabling Discord causes future Discord Windows notifications to appear in the feed.
- Notifications from disabled applications are not displayed or stored as content.
- Source selections survive restart.
- Feed updates live and orders items newest first.
- Full available title/body text wraps cleanly.
- Generic feed mode remains available for all enabled sources.
- When Discord is enabled, an explicit Discord view can group parsed Discord notifications by server and channel.
- Discord notifications that cannot be parsed still remain visible in a fallback group.
- Records survive restart and expire after 72 hours or when the 2,000-record limit is exceeded.
- All content remains on the local computer.

## Out of scope for V1

- Discord API integration, bot tokens, or guaranteed Discord message archive behaviour.
- Discord replies, reactions, avatars, attachments, or click-through actions.
- Persistent server/channel selection settings.
- Source-aware layouts for applications other than Discord unless explicitly specified.
- AI or keyword filtering.
- Generic notification replies, actions, links, or opening the source application.
- Removing or suppressing native Windows notifications.
- Enumerating every installed application before it sends a notification.
- Cloud sync, accounts, telemetry, mobile, macOS, or Linux support.
- Automatic startup with Windows.
- Guaranteed capture of every notification or underlying message.
