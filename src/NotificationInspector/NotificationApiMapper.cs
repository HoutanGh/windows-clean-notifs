using System.Globalization;

namespace WindowsCleanNotifs.NotificationInspector;

public static class NotificationApiMapper
{
    public static SourceResponse ToSourceResponse(StoredNotificationSource source)
    {
        return new SourceResponse(
            AppId: source.AppId,
            DisplayName: source.AppDisplayName,
            Enabled: source.Enabled,
            FirstSeenAt: FormatUtc(source.FirstSeenAt),
            LastSeenAt: FormatUtc(source.LastSeenAt));
    }

    public static NotificationResponse ToNotificationResponse(StoredNotificationWithSource item)
    {
        var display = NotificationDisplayMapper.Map(item.Notification, item.Source);

        return new NotificationResponse(
            Id: item.Notification.Id,
            AppId: item.Notification.AppId,
            SourceApp: display.SourceApp,
            Timestamp: FormatUtc(display.Timestamp),
            PrimaryText: display.PrimaryText,
            MessageText: display.MessageText,
            Discord: DiscordNotificationContextMapper.Map(
                item.Notification.AppId,
                display.SourceApp,
                display.PrimaryText));
    }

    public static string FormatUtc(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    }
}
