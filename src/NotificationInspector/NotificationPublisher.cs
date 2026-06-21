namespace WindowsCleanNotifs.NotificationInspector;

public static class NotificationPublisher
{
    public static async Task<bool> PublishStoredNotificationAsync(
        INotificationStore store,
        NotificationEventHub eventHub,
        StoredNotificationRecord notification,
        CancellationToken cancellationToken)
    {
        var source = await store.GetSourceAsync(notification.AppId, cancellationToken);
        if (source is null || !source.Enabled)
        {
            return false;
        }

        var response = NotificationApiMapper.ToNotificationResponse(
            new StoredNotificationWithSource(notification, source));
        await eventHub.PublishAsync(response, cancellationToken);
        return true;
    }
}
