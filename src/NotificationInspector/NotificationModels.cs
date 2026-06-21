namespace WindowsCleanNotifs.NotificationInspector;

public sealed record CapturedNotification(
    string AppDisplayName,
    string AppId,
    uint WindowsNotificationId,
    DateTimeOffset CreationTime,
    string Title,
    string Body,
    IReadOnlyList<string> RawTextElements)
{
    public NotificationIdentity Identity => new(AppId, WindowsNotificationId, CreationTime);
}

public readonly record struct NotificationIdentity(
    string AppId,
    uint WindowsNotificationId,
    DateTimeOffset CreationTime);

public sealed record NotificationSource(
    string AppDisplayName,
    string AppId);

public sealed record CollectorSnapshotResult(
    DateTimeOffset ObservedAt,
    int SnapshotCount,
    IReadOnlyList<NotificationSource> SeenSources,
    IReadOnlyList<NotificationSource> DiscoveredSources,
    IReadOnlyList<CapturedNotification> NewNotifications);
