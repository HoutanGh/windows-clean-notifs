namespace WindowsCleanNotifs.NotificationInspector;

public sealed record StoredNotificationSource(
    string AppId,
    string AppDisplayName,
    bool Enabled,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

public sealed record StoredNotificationRecord(
    long Id,
    string AppId,
    uint WindowsNotificationId,
    DateTimeOffset CreationTime,
    string Title,
    string Body,
    IReadOnlyList<string> RawTextElements,
    DateTimeOffset CapturedAt);

public sealed record StoredNotificationWithSource(
    StoredNotificationRecord Notification,
    StoredNotificationSource Source);

public sealed record SourceUpsertResult(
    bool Inserted,
    StoredNotificationSource Source);

public enum NotificationInsertStatus
{
    Stored,
    Duplicate,
    SourceDisabled,
    SourceMissing
}

public sealed record NotificationInsertResult(
    NotificationInsertStatus Status,
    StoredNotificationRecord? Notification);

public sealed record StoredNotificationOutcome(
    CapturedNotification Notification,
    NotificationInsertStatus Status,
    StoredNotificationRecord? StoredNotification);

public sealed record PersistedCollectorResult(
    IReadOnlyList<NotificationSource> DiscoveredSources,
    IReadOnlyList<StoredNotificationOutcome> NotificationOutcomes);
