using System.Text;

namespace WindowsCleanNotifs.NotificationInspector;

internal static class DiscordNotificationContextMapper
{
    private const string DiscordAppId = "com.squirrel.Discord.Discord";
    private const string ParsedConfidence = "parsed";
    private const string UnknownConfidence = "unknown";

    public static DiscordNotificationContextResponse? Map(
        string appId,
        string sourceApp,
        string? title)
    {
        if (!IsDiscordApp(appId, sourceApp))
        {
            return null;
        }

        var normalizedTitle = NormalizeGroupingText(title);
        if (TryParseDiscordTitle(
            normalizedTitle,
            out var sender,
            out var channel,
            out var context))
        {
            return new DiscordNotificationContextResponse(
                Sender: sender,
                Context: context,
                Channel: channel,
                Confidence: ParsedConfidence);
        }

        return new DiscordNotificationContextResponse(
            Sender: null,
            Context: null,
            Channel: null,
            Confidence: UnknownConfidence);
    }

    private static bool IsDiscordApp(string appId, string sourceApp)
    {
        return string.Equals(appId, DiscordAppId, StringComparison.OrdinalIgnoreCase)
            || (sourceApp.Contains("Discord", StringComparison.OrdinalIgnoreCase)
                && appId.Contains("Discord", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseDiscordTitle(
        string? title,
        out string sender,
        out string channel,
        out string contextLabel)
    {
        sender = string.Empty;
        channel = string.Empty;
        contextLabel = string.Empty;

        if (string.IsNullOrWhiteSpace(title) || title[^1] != ')')
        {
            return false;
        }

        var openIndex = title.LastIndexOf(" (", StringComparison.Ordinal);
        if (openIndex <= 0)
        {
            return false;
        }

        var context = title[(openIndex + 2)..^1].Trim();
        var commaIndex = context.IndexOf(',', StringComparison.Ordinal);
        if (commaIndex <= 0 || commaIndex >= context.Length - 1)
        {
            return false;
        }

        sender = title[..openIndex].Trim();
        channel = context[..commaIndex].Trim();
        contextLabel = context[(commaIndex + 1)..].Trim();

        return sender.Length > 0 && channel.Length > 0 && contextLabel.Length > 0;
    }

    private static string? NormalizeGroupingText(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '\u200e' or '\u200f'
                or '\u202a' or '\u202b' or '\u202c' or '\u202d' or '\u202e'
                or '\u2066' or '\u2067' or '\u2068' or '\u2069')
            {
                continue;
            }

            builder.Append(character is '\r' or '\n' ? ' ' : character);
        }

        var normalized = builder.ToString().Trim();
        return normalized.Length == 0 ? null : normalized;
    }
}
