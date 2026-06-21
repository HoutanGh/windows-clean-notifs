namespace WindowsCleanNotifs.NotificationInspector;

public interface INotificationStore
{
    string DatabasePath { get; }

    Task InitializeAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredNotificationSource>> ListSourcesAsync(CancellationToken cancellationToken);

    Task<StoredNotificationSource?> GetSourceAsync(string appId, CancellationToken cancellationToken);

    Task<SourceUpsertResult> UpsertSourceAsync(
        NotificationSource source,
        DateTimeOffset seenAt,
        CancellationToken cancellationToken);

    Task<bool> SetSourceEnabledAsync(
        string appId,
        bool enabled,
        CancellationToken cancellationToken);

    Task<NotificationInsertResult> TryInsertNotificationAsync(
        CapturedNotification notification,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredNotificationRecord>> ListNotificationsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredNotificationWithSource>> ListEnabledNotificationsAsync(
        int limit,
        long? beforeId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredNotificationWithSource>> ListEnabledNotificationsAfterIdAsync(
        long afterId,
        CancellationToken cancellationToken);

    Task<int> DeleteNotificationsOlderThanAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken);
}
