using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace WindowsCleanNotifs.NotificationInspector;

public sealed class WindowsNotificationSnapshotProvider : INotificationSnapshotProvider
{
    private readonly UserNotificationListener _listener;

    public WindowsNotificationSnapshotProvider(UserNotificationListener listener)
    {
        _listener = listener;
    }

    public async Task<IReadOnlyList<CapturedNotification>> GetCurrentToastNotificationsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
        cancellationToken.ThrowIfCancellationRequested();

        return notifications
            .Select(ToCapturedNotification)
            .ToArray();
    }

    private static CapturedNotification ToCapturedNotification(UserNotification notification)
    {
        var rawText = ExtractRawText(notification);
        var title = rawText.FirstOrDefault() ?? string.Empty;
        var body = rawText.Count > 1 ? string.Join(Environment.NewLine, rawText.Skip(1)) : string.Empty;

        return new CapturedNotification(
            AppDisplayName: EmptyAsUnknown(notification.AppInfo.DisplayInfo.DisplayName),
            AppId: EmptyAsUnknown(notification.AppInfo.AppUserModelId),
            WindowsNotificationId: notification.Id,
            CreationTime: notification.CreationTime,
            Title: title,
            Body: body,
            RawTextElements: rawText);
    }

    private static IReadOnlyList<string> ExtractRawText(UserNotification notification)
    {
        var visual = notification.Notification.Visual;
        var toastBinding = visual.GetBinding(KnownNotificationBindings.ToastGeneric);
        if (toastBinding is not null)
        {
            return toastBinding.GetTextElements().Select(element => element.Text ?? string.Empty).ToArray();
        }

        var allText = new List<string>();
        foreach (var binding in visual.Bindings)
        {
            allText.AddRange(binding.GetTextElements().Select(element => element.Text ?? string.Empty));
        }

        return allText;
    }

    private static string EmptyAsUnknown(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<unknown>" : value;
    }
}
