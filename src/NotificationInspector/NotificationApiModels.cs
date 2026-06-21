namespace WindowsCleanNotifs.NotificationInspector;

public sealed record NotificationApiOptions(
    TimeSpan PollInterval,
    TimeSpan RetentionWindow,
    string? FrontendAssetsPath = null);

public sealed record HealthResponse(
    string Status,
    string ListenerAccessStatus,
    bool CollectorRunning,
    string PollingInterval,
    string RetentionPeriod);

public sealed record SourceResponse(
    string AppId,
    string DisplayName,
    bool Enabled,
    string FirstSeenAt,
    string LastSeenAt);

public sealed record SourceSelectionRequest(
    string? AppId,
    bool? Enabled);

public sealed record NotificationResponse(
    long Id,
    string AppId,
    string SourceApp,
    string Timestamp,
    string? PrimaryText,
    string? MessageText,
    DiscordNotificationContextResponse? Discord);

public sealed record DiscordNotificationContextResponse(
    string? Sender,
    string? Server,
    string? Channel,
    string Confidence);

public sealed record ErrorResponse(string Error);
