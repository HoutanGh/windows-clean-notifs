namespace WindowsCleanNotifs.NotificationInspector;

public sealed record NotificationApiOptions(
    TimeSpan PollInterval,
    TimeSpan RetentionWindow);

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
    string? MessageText);

public sealed record ErrorResponse(string Error);
