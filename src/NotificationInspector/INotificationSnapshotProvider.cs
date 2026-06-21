namespace WindowsCleanNotifs.NotificationInspector;

public interface INotificationSnapshotProvider
{
    Task<IReadOnlyList<CapturedNotification>> GetCurrentToastNotificationsAsync(CancellationToken cancellationToken);
}
