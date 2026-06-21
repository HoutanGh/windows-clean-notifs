using WindowsCleanNotifs.NotificationInspector;

namespace WindowsCleanNotifs.NotificationInspector.Tests;

internal static class Program
{
    public static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("duplicate snapshots", DuplicateSnapshotsDoNotEmitNewNotifications),
            ("newly appearing notifications", NewlyAppearingNotificationsAreEmitted),
            ("multiple notifications from one app", MultipleNotificationsFromOneAppEmitOncePerNotification),
            ("multiple applications", MultipleApplicationsDiscoverSeparateSources),
            ("notifications disappearing from later snapshots", DisappearingNotificationsStayDedupedWithinRetention),
            ("bounded dedupe retention", StaleIdentitiesArePruned)
        };

        var failures = 0;
        foreach (var test in tests)
        {
            try
            {
                await test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
            }
        }

        return failures == 0 ? 0 : 1;
    }

    private static async Task DuplicateSnapshotsDoNotEmitNewNotifications()
    {
        var notification = Notification("App One", "app.one", 10, "2026-06-21T10:00:00+01:00");
        var provider = new FakeSnapshotProvider(
            [notification],
            [notification]);
        var collector = NewCollector(provider);

        var startup = await collector.SeedAsync(CancellationToken.None);
        AssertEqual(1, startup.SnapshotCount);
        AssertEqual(1, startup.DiscoveredSources.Count);
        AssertEqual(0, startup.NewNotifications.Count);

        var poll = await collector.PollOnceAsync(CancellationToken.None);
        AssertEqual(1, poll.SnapshotCount);
        AssertEqual(0, poll.DiscoveredSources.Count);
        AssertEqual(0, poll.NewNotifications.Count);
    }

    private static async Task NewlyAppearingNotificationsAreEmitted()
    {
        var notification = Notification("App One", "app.one", 11, "2026-06-21T10:01:00+01:00");
        var provider = new FakeSnapshotProvider(
            [],
            [notification],
            [notification]);
        var collector = NewCollector(provider);

        var startup = await collector.SeedAsync(CancellationToken.None);
        AssertEqual(0, startup.NewNotifications.Count);

        var firstPoll = await collector.PollOnceAsync(CancellationToken.None);
        AssertEqual(1, firstPoll.DiscoveredSources.Count);
        AssertEqual(1, firstPoll.NewNotifications.Count);
        AssertEqual(notification.Identity, firstPoll.NewNotifications[0].Identity);

        var secondPoll = await collector.PollOnceAsync(CancellationToken.None);
        AssertEqual(0, secondPoll.DiscoveredSources.Count);
        AssertEqual(0, secondPoll.NewNotifications.Count);
    }

    private static async Task MultipleNotificationsFromOneAppEmitOncePerNotification()
    {
        var first = Notification("App One", "app.one", 20, "2026-06-21T10:02:00+01:00");
        var second = Notification("App One", "app.one", 21, "2026-06-21T10:02:01+01:00");
        var provider = new FakeSnapshotProvider(
            [],
            [first, second]);
        var collector = NewCollector(provider);

        await collector.SeedAsync(CancellationToken.None);
        var poll = await collector.PollOnceAsync(CancellationToken.None);

        AssertEqual(1, poll.DiscoveredSources.Count);
        AssertEqual(2, poll.NewNotifications.Count);
        AssertEqual(first.Identity, poll.NewNotifications[0].Identity);
        AssertEqual(second.Identity, poll.NewNotifications[1].Identity);
    }

    private static async Task MultipleApplicationsDiscoverSeparateSources()
    {
        var first = Notification("App One", "app.one", 30, "2026-06-21T10:03:00+01:00");
        var second = Notification("App Two", "app.two", 31, "2026-06-21T10:03:01+01:00");
        var provider = new FakeSnapshotProvider(
            [],
            [first, second]);
        var collector = NewCollector(provider);

        await collector.SeedAsync(CancellationToken.None);
        var poll = await collector.PollOnceAsync(CancellationToken.None);

        AssertEqual(2, poll.DiscoveredSources.Count);
        AssertEqual("app.one", poll.DiscoveredSources[0].AppId);
        AssertEqual("app.two", poll.DiscoveredSources[1].AppId);
        AssertEqual(2, poll.NewNotifications.Count);
    }

    private static async Task DisappearingNotificationsStayDedupedWithinRetention()
    {
        var notification = Notification("App One", "app.one", 40, "2026-06-21T10:04:00+01:00");
        var provider = new FakeSnapshotProvider(
            [],
            [notification],
            [],
            [notification]);
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-21T10:04:00+01:00"));
        var collector = NewCollector(provider, clock);

        await collector.SeedAsync(CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(1));
        var firstPoll = await collector.PollOnceAsync(CancellationToken.None);
        AssertEqual(1, firstPoll.NewNotifications.Count);

        clock.Advance(TimeSpan.FromSeconds(1));
        var emptyPoll = await collector.PollOnceAsync(CancellationToken.None);
        AssertEqual(0, emptyPoll.NewNotifications.Count);
        AssertEqual(1, collector.KnownIdentityCount);

        clock.Advance(TimeSpan.FromSeconds(1));
        var reappearedPoll = await collector.PollOnceAsync(CancellationToken.None);
        AssertEqual(0, reappearedPoll.NewNotifications.Count);
    }

    private static async Task StaleIdentitiesArePruned()
    {
        var notification = Notification("App One", "app.one", 50, "2026-06-21T10:05:00+01:00");
        var provider = new FakeSnapshotProvider(
            [],
            [notification],
            [],
            [notification]);
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-21T10:05:00+01:00"));
        var collector = NewCollector(provider, clock);

        await collector.SeedAsync(CancellationToken.None);

        var firstPoll = await collector.PollOnceAsync(CancellationToken.None);
        AssertEqual(1, firstPoll.NewNotifications.Count);

        clock.Advance(TimeSpan.FromMinutes(11));
        var prunePoll = await collector.PollOnceAsync(CancellationToken.None);
        AssertEqual(0, prunePoll.NewNotifications.Count);
        AssertEqual(0, collector.KnownIdentityCount);

        var reappearedPoll = await collector.PollOnceAsync(CancellationToken.None);
        AssertEqual(1, reappearedPoll.NewNotifications.Count);
    }

    private static PollingNotificationCollector NewCollector(
        INotificationSnapshotProvider provider,
        FakeClock? clock = null)
    {
        return new PollingNotificationCollector(
            provider,
            dedupeRetention: TimeSpan.FromMinutes(10),
            clock: clock is null ? null : clock.Now);
    }

    private static CapturedNotification Notification(
        string appDisplayName,
        string appId,
        uint windowsNotificationId,
        string creationTime)
    {
        return new CapturedNotification(
            AppDisplayName: appDisplayName,
            AppId: appId,
            WindowsNotificationId: windowsNotificationId,
            CreationTime: DateTimeOffset.Parse(creationTime),
            Title: $"Title {windowsNotificationId}",
            Body: $"Body {windowsNotificationId}",
            RawTextElements: [$"Title {windowsNotificationId}", $"Body {windowsNotificationId}"]);
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }

    private sealed class FakeSnapshotProvider : INotificationSnapshotProvider
    {
        private readonly Queue<IReadOnlyList<CapturedNotification>> _snapshots;

        public FakeSnapshotProvider(params IReadOnlyList<CapturedNotification>[] snapshots)
        {
            _snapshots = new Queue<IReadOnlyList<CapturedNotification>>(snapshots);
        }

        public Task<IReadOnlyList<CapturedNotification>> GetCurrentToastNotificationsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_snapshots.Count == 0 ? [] : _snapshots.Dequeue());
        }
    }

    private sealed class FakeClock
    {
        private DateTimeOffset _current;

        public FakeClock(DateTimeOffset current)
        {
            _current = current;
        }

        public DateTimeOffset Now()
        {
            return _current;
        }

        public void Advance(TimeSpan amount)
        {
            _current += amount;
        }
    }
}
