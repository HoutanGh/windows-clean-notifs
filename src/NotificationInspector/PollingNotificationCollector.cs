namespace WindowsCleanNotifs.NotificationInspector;

public sealed class PollingNotificationCollector
{
    private readonly INotificationSnapshotProvider _provider;
    private readonly TimeSpan _dedupeRetention;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<NotificationIdentity, DateTimeOffset> _seen = new();
    private readonly HashSet<string> _knownAppIds = new(StringComparer.Ordinal);

    public PollingNotificationCollector(
        INotificationSnapshotProvider provider,
        TimeSpan dedupeRetention,
        Func<DateTimeOffset>? clock = null)
    {
        if (dedupeRetention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(dedupeRetention), "Dedupe retention must be positive.");
        }

        _provider = provider;
        _dedupeRetention = dedupeRetention;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public int KnownSourceCount => _knownAppIds.Count;

    public int KnownIdentityCount => _seen.Count;

    public async Task<CollectorSnapshotResult> SeedAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _provider.GetCurrentToastNotificationsAsync(cancellationToken);
        return ProcessSnapshot(snapshot, emitNewNotifications: false);
    }

    public async Task<CollectorSnapshotResult> PollOnceAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _provider.GetCurrentToastNotificationsAsync(cancellationToken);
        return ProcessSnapshot(snapshot, emitNewNotifications: true);
    }

    private CollectorSnapshotResult ProcessSnapshot(
        IReadOnlyList<CapturedNotification> snapshot,
        bool emitNewNotifications)
    {
        var now = _clock();
        var seenSourcesByAppId = new Dictionary<string, NotificationSource>(StringComparer.Ordinal);
        var discoveredSources = new List<NotificationSource>();
        var newNotifications = new List<CapturedNotification>();

        foreach (var notification in snapshot.OrderBy(item => item.CreationTime).ThenBy(item => item.WindowsNotificationId))
        {
            var source = new NotificationSource(notification.AppDisplayName, notification.AppId);
            seenSourcesByAppId[notification.AppId] = source;

            if (_knownAppIds.Add(notification.AppId))
            {
                discoveredSources.Add(source);
            }

            var identity = notification.Identity;
            var wasSeen = _seen.ContainsKey(identity);
            _seen[identity] = now;

            if (emitNewNotifications && !wasSeen)
            {
                newNotifications.Add(notification);
            }
        }

        PruneStaleIdentities(now);

        return new CollectorSnapshotResult(
            ObservedAt: now,
            SnapshotCount: snapshot.Count,
            SeenSources: seenSourcesByAppId.Values.ToArray(),
            DiscoveredSources: discoveredSources,
            NewNotifications: newNotifications);
    }

    private void PruneStaleIdentities(DateTimeOffset now)
    {
        var cutoff = now - _dedupeRetention;
        var staleIdentities = _seen
            .Where(item => item.Value < cutoff)
            .Select(item => item.Key)
            .ToArray();

        foreach (var identity in staleIdentities)
        {
            _seen.Remove(identity);
        }
    }
}
