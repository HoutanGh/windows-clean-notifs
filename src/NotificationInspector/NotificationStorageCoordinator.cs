namespace WindowsCleanNotifs.NotificationInspector;

public sealed class NotificationStorageCoordinator
{
    private readonly INotificationStore _store;

    public NotificationStorageCoordinator(INotificationStore store)
    {
        _store = store;
    }

    public async Task<PersistedCollectorResult> ApplyAsync(
        CollectorSnapshotResult collectorResult,
        bool storeNewNotifications,
        CancellationToken cancellationToken)
    {
        var insertedSources = new List<NotificationSource>();

        foreach (var source in collectorResult.SeenSources)
        {
            var result = await _store.UpsertSourceAsync(source, collectorResult.ObservedAt, cancellationToken);
            if (result.Inserted)
            {
                insertedSources.Add(source);
            }
        }

        var notificationOutcomes = new List<StoredNotificationOutcome>();
        if (storeNewNotifications)
        {
            foreach (var notification in collectorResult.NewNotifications)
            {
                var result = await _store.TryInsertNotificationAsync(
                    notification,
                    collectorResult.ObservedAt,
                    cancellationToken);

                notificationOutcomes.Add(new StoredNotificationOutcome(notification, result.Status));
            }
        }

        return new PersistedCollectorResult(insertedSources, notificationOutcomes);
    }
}
