namespace WindowsCleanNotifs.NotificationInspector;

public sealed record NotificationDisplayItem(
    string SourceApp,
    DateTimeOffset Timestamp,
    string? PrimaryText,
    string? MessageText);

public static class NotificationDisplayMapper
{
    public static NotificationDisplayItem Map(CapturedNotification notification)
    {
        return Map(
            notification.AppDisplayName,
            notification.CreationTime,
            notification.Title,
            notification.Body,
            notification.RawTextElements);
    }

    public static NotificationDisplayItem Map(
        StoredNotificationRecord notification,
        StoredNotificationSource source)
    {
        return Map(
            source.AppDisplayName,
            notification.CreationTime,
            notification.Title,
            notification.Body,
            notification.RawTextElements);
    }

    public static NotificationDisplayItem Map(
        string sourceApp,
        DateTimeOffset creationTime,
        string title,
        string body,
        IReadOnlyList<string> rawTextElements)
    {
        var raw = rawTextElements
            .Select((value, index) => new RawDisplayText(index, NormalizeDisplayText(value)))
            .Where(item => item.Text is not null)
            .ToArray();

        var primaryText = NormalizeDisplayText(title);
        int? primaryRawIndex = null;

        if (primaryText is null)
        {
            var firstRaw = raw.FirstOrDefault();
            if (firstRaw is not null)
            {
                primaryText = firstRaw.Text;
                primaryRawIndex = firstRaw.Index;
            }
        }
        else
        {
            var matchingRaw = raw.FirstOrDefault(item => item.Text == primaryText);
            if (matchingRaw is not null)
            {
                primaryRawIndex = matchingRaw.Index;
            }
        }

        var messageText = NormalizeDisplayText(body);
        if (messageText is null)
        {
            var remainingRaw = raw
                .Where(item => item.Index != primaryRawIndex)
                .Select(item => item.Text!)
                .ToArray();

            messageText = remainingRaw.Length == 0 ? null : string.Join('\n', remainingRaw);
        }

        if (primaryText is not null && messageText is not null && primaryText == messageText)
        {
            messageText = null;
        }

        return new NotificationDisplayItem(
            SourceApp: NormalizeDisplayText(sourceApp) ?? "<unknown>",
            Timestamp: creationTime,
            PrimaryText: primaryText,
            MessageText: messageText);
    }

    private static string? NormalizeDisplayText(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Trim();

        return normalized.Length == 0 ? null : normalized;
    }

    private sealed record RawDisplayText(int Index, string? Text);
}
