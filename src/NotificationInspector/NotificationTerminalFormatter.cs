using System.Globalization;
using System.Text;

namespace WindowsCleanNotifs.NotificationInspector;

public static class NotificationTerminalFormatter
{
    public static IReadOnlyList<string> FormatNotification(
        CapturedNotification notification,
        bool includeDebugRaw)
    {
        var display = NotificationDisplayMapper.Map(notification);
        var lines = new List<string>
        {
            "----",
            $"{display.Timestamp.ToLocalTime():HH:mm:ss} · {display.SourceApp}"
        };

        if (display.PrimaryText is not null)
        {
            lines.Add(display.PrimaryText);
        }

        if (display.MessageText is not null)
        {
            lines.Add(display.MessageText);
        }

        if (includeDebugRaw)
        {
            lines.AddRange(FormatDebugRaw(notification, display));
        }

        lines.Add(string.Empty);
        return lines;
    }

    public static IReadOnlyList<string> FormatDebugRaw(
        CapturedNotification notification,
        NotificationDisplayItem display)
    {
        var lines = new List<string>
        {
            "Debug raw:",
            $"  App id: {notification.AppId}",
            $"  Windows notification id: {notification.WindowsNotificationId.ToString(CultureInfo.InvariantCulture)}",
            $"  Creation timestamp: {notification.CreationTime.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff zzz}",
            $"  Raw title: {OneLineOrEmpty(notification.Title)}",
            $"  Raw title code points: {UnicodeDiagnostics.DescribeSuspiciousCodePoints(notification.Title)}",
            $"  Raw body: {OneLineOrEmpty(notification.Body)}",
            $"  Raw body code points: {UnicodeDiagnostics.DescribeSuspiciousCodePoints(notification.Body)}",
            "  Raw text elements:"
        };

        if (notification.RawTextElements.Count == 0)
        {
            lines.Add("    <none>");
        }
        else
        {
            for (var index = 0; index < notification.RawTextElements.Count; index++)
            {
                var value = notification.RawTextElements[index];
                lines.Add($"    [{index}] {OneLineOrEmpty(value)}");
                lines.Add($"        code points: {UnicodeDiagnostics.DescribeSuspiciousCodePoints(value)}");
            }
        }

        lines.Add($"  Derived primary text: {OneLineOrEmpty(display.PrimaryText)}");
        lines.Add($"  Derived primary text code points: {UnicodeDiagnostics.DescribeSuspiciousCodePoints(display.PrimaryText)}");
        lines.Add($"  Derived message text: {OneLineOrEmpty(display.MessageText)}");
        lines.Add($"  Derived message text code points: {UnicodeDiagnostics.DescribeSuspiciousCodePoints(display.MessageText)}");

        return lines;
    }

    private static string OneLineOrEmpty(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "<empty>";
        }

        return value
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }
}

public static class UnicodeDiagnostics
{
    public static string DescribeSuspiciousCodePoints(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "<none>";
        }

        var parts = new List<string>();
        var charIndex = 0;
        var span = value.AsSpan();

        while (charIndex < value.Length)
        {
            var status = Rune.DecodeFromUtf16(span[charIndex..], out var rune, out var charsConsumed);
            if (status != System.Buffers.OperationStatus.Done)
            {
                rune = Rune.ReplacementChar;
                charsConsumed = 1;
            }

            if (ShouldReport(rune))
            {
                parts.Add($"char {charIndex}: {FormatRune(rune)}");
            }

            charIndex += charsConsumed;
        }

        return parts.Count == 0 ? "<none>" : string.Join(", ", parts);
    }

    private static bool ShouldReport(Rune rune)
    {
        return rune.Value == '?'
            || rune.Value == 0xFFFD
            || rune.Value > 0x7F;
    }

    private static string FormatRune(Rune rune)
    {
        var codePoint = $"U+{rune.Value:X4}";
        return rune.Value switch
        {
            '?' => "'?' U+003F (literal question mark)",
            0xFFFD => "'\uFFFD' U+FFFD (replacement character)",
            0x2068 => $"{codePoint} (first strong isolate)",
            0x2069 => $"{codePoint} (pop directional isolate)",
            _ => $"{EscapeRune(rune)} {codePoint}"
        };
    }

    private static string EscapeRune(Rune rune)
    {
        return rune.Value switch
        {
            '\n' => "'\\n'",
            '\r' => "'\\r'",
            '\t' => "'\\t'",
            _ => $"'{rune}'"
        };
    }
}
