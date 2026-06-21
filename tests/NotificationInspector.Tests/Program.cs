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
            ("bounded dedupe retention", StaleIdentitiesArePruned),
            ("new sources default disabled", NewlyDiscoveredSourcesDefaultToDisabled),
            ("source metadata survives reopening", SourceMetadataSurvivesReopeningDatabase),
            ("source selection persists", EnablingAndDisablingSourcePersists),
            ("rediscovery preserves selection", RediscoveryUpdatesMetadataWithoutResettingSelection),
            ("disabled source content is not stored", DisabledSourceNotificationContentIsNotStored),
            ("enabled source content is stored", EnabledSourceNotificationContentIsStored),
            ("duplicate notifications are ignored", DuplicateNotificationsAreNotInsertedTwice),
            ("raw text round trips", RawTextElementsRoundTripInOrder),
            ("startup notifications are not stored", StartupSnapshotNotificationsAreNotStored),
            ("retention preserves sources", RetentionRemovesOldNotificationsButPreservesSources),
            ("display maps normal title and body", DisplayMapsNormalTitleAndBody),
            ("display title fallback", DisplayUsesRawTextWhenTitleIsMissing),
            ("display body fallback", DisplayUsesRemainingRawTextWhenBodyIsMissing),
            ("display hides empty fields", DisplayHidesEmptyFields),
            ("display suppresses duplicate title and body", DisplaySuppressesDuplicateTitleAndBody),
            ("display preserves raw fallback order", DisplayPreservesRawTextElementOrder),
            ("display preserves multiline text", DisplayPreservesMultilineText),
            ("display preserves symbols", DisplayPreservesUrlsTickersPricesEmojiAndSymbols),
            ("display does not mutate raw values", DisplayMappingDoesNotMutateRawValues),
            ("display mapping is deterministic", DisplayMappingIsDeterministic),
            ("normal terminal output excludes debug metadata", NormalTerminalOutputExcludesDebugMetadata),
            ("debug raw output includes code points", DebugRawOutputIncludesRawValuesAndCodePoints)
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

    private static async Task NewlyDiscoveredSourcesDefaultToDisabled()
    {
        var testDb = TestDatabase.Create();
        try
        {
            var store = await testDb.OpenStoreAsync();
            var coordinator = new NotificationStorageCoordinator(store);
            var result = SnapshotResult(
                observedAt: "2026-06-21T11:00:00+01:00",
                seenSources: [new NotificationSource("App One", "app.one")]);

            var persisted = await coordinator.ApplyAsync(result, storeNewNotifications: false, CancellationToken.None);
            var source = AssertNotNull(await store.GetSourceAsync("app.one", CancellationToken.None));

            AssertEqual(1, persisted.DiscoveredSources.Count);
            AssertEqual(false, source.Enabled);
        }
        finally
        {
            testDb.Delete();
        }
    }

    private static async Task SourceMetadataSurvivesReopeningDatabase()
    {
        var testDb = TestDatabase.Create();
        try
        {
            var firstStore = await testDb.OpenStoreAsync();
            await firstStore.UpsertSourceAsync(
                new NotificationSource("App One", "app.one"),
                DateTimeOffset.Parse("2026-06-21T11:01:00+01:00"),
                CancellationToken.None);

            var secondStore = await testDb.OpenStoreAsync();
            var source = AssertNotNull(await secondStore.GetSourceAsync("app.one", CancellationToken.None));

            AssertEqual("App One", source.AppDisplayName);
            AssertEqual(false, source.Enabled);
        }
        finally
        {
            testDb.Delete();
        }
    }

    private static async Task EnablingAndDisablingSourcePersists()
    {
        var testDb = TestDatabase.Create();
        try
        {
            var firstStore = await testDb.OpenStoreAsync();
            await firstStore.UpsertSourceAsync(
                new NotificationSource("App One", "app.one"),
                DateTimeOffset.Parse("2026-06-21T11:02:00+01:00"),
                CancellationToken.None);

            AssertEqual(true, await firstStore.SetSourceEnabledAsync("app.one", true, CancellationToken.None));

            var secondStore = await testDb.OpenStoreAsync();
            var enabledSource = AssertNotNull(await secondStore.GetSourceAsync("app.one", CancellationToken.None));
            AssertEqual(true, enabledSource.Enabled);

            AssertEqual(true, await secondStore.SetSourceEnabledAsync("app.one", false, CancellationToken.None));

            var thirdStore = await testDb.OpenStoreAsync();
            var disabledSource = AssertNotNull(await thirdStore.GetSourceAsync("app.one", CancellationToken.None));
            AssertEqual(false, disabledSource.Enabled);
        }
        finally
        {
            testDb.Delete();
        }
    }

    private static async Task RediscoveryUpdatesMetadataWithoutResettingSelection()
    {
        var testDb = TestDatabase.Create();
        try
        {
            var store = await testDb.OpenStoreAsync();
            var firstSeen = DateTimeOffset.Parse("2026-06-21T11:03:00+01:00");
            var lastSeen = DateTimeOffset.Parse("2026-06-21T11:04:00+01:00");

            await store.UpsertSourceAsync(new NotificationSource("Old Name", "app.one"), firstSeen, CancellationToken.None);
            await store.SetSourceEnabledAsync("app.one", true, CancellationToken.None);
            await store.UpsertSourceAsync(new NotificationSource("New Name", "app.one"), lastSeen, CancellationToken.None);

            var source = AssertNotNull(await store.GetSourceAsync("app.one", CancellationToken.None));
            AssertEqual("New Name", source.AppDisplayName);
            AssertEqual(true, source.Enabled);
            AssertEqual(firstSeen.ToUniversalTime(), source.FirstSeenAt.ToUniversalTime());
            AssertEqual(lastSeen.ToUniversalTime(), source.LastSeenAt.ToUniversalTime());
        }
        finally
        {
            testDb.Delete();
        }
    }

    private static async Task DisabledSourceNotificationContentIsNotStored()
    {
        var testDb = TestDatabase.Create();
        try
        {
            var store = await testDb.OpenStoreAsync();
            await store.UpsertSourceAsync(
                new NotificationSource("App One", "app.one"),
                DateTimeOffset.Parse("2026-06-21T11:05:00+01:00"),
                CancellationToken.None);

            var insert = await store.TryInsertNotificationAsync(
                Notification("App One", "app.one", 60, "2026-06-21T11:05:01+01:00"),
                DateTimeOffset.Parse("2026-06-21T11:05:02+01:00"),
                CancellationToken.None);

            var notifications = await store.ListNotificationsAsync(CancellationToken.None);
            AssertEqual(NotificationInsertStatus.SourceDisabled, insert.Status);
            AssertEqual(0, notifications.Count);
        }
        finally
        {
            testDb.Delete();
        }
    }

    private static async Task EnabledSourceNotificationContentIsStored()
    {
        var testDb = TestDatabase.Create();
        try
        {
            var store = await testDb.OpenStoreAsync();
            await store.UpsertSourceAsync(
                new NotificationSource("App One", "app.one"),
                DateTimeOffset.Parse("2026-06-21T11:06:00+01:00"),
                CancellationToken.None);
            await store.SetSourceEnabledAsync("app.one", true, CancellationToken.None);

            var notification = Notification(
                "App One",
                "app.one",
                61,
                "2026-06-21T11:06:01+01:00",
                ["Title 61", "Line one", "Line two"]);
            var insert = await store.TryInsertNotificationAsync(
                notification,
                DateTimeOffset.Parse("2026-06-21T11:06:02+01:00"),
                CancellationToken.None);

            var notifications = await store.ListNotificationsAsync(CancellationToken.None);
            AssertEqual(NotificationInsertStatus.Stored, insert.Status);
            AssertEqual(1, notifications.Count);
            AssertEqual("Title 61", notifications[0].Title);
            AssertEqual($"Line one{Environment.NewLine}Line two", notifications[0].Body);
        }
        finally
        {
            testDb.Delete();
        }
    }

    private static async Task DuplicateNotificationsAreNotInsertedTwice()
    {
        var testDb = TestDatabase.Create();
        try
        {
            var store = await EnabledStoreAsync(testDb, "App One", "app.one", "2026-06-21T11:07:00+01:00");
            var notification = Notification("App One", "app.one", 62, "2026-06-21T11:07:01+01:00");
            var capturedAt = DateTimeOffset.Parse("2026-06-21T11:07:02+01:00");

            var first = await store.TryInsertNotificationAsync(notification, capturedAt, CancellationToken.None);
            var second = await store.TryInsertNotificationAsync(notification, capturedAt, CancellationToken.None);
            var notifications = await store.ListNotificationsAsync(CancellationToken.None);

            AssertEqual(NotificationInsertStatus.Stored, first.Status);
            AssertEqual(NotificationInsertStatus.Duplicate, second.Status);
            AssertEqual(1, notifications.Count);
        }
        finally
        {
            testDb.Delete();
        }
    }

    private static async Task RawTextElementsRoundTripInOrder()
    {
        var testDb = TestDatabase.Create();
        try
        {
            var store = await EnabledStoreAsync(testDb, "App One", "app.one", "2026-06-21T11:08:00+01:00");
            var rawText = new[] { "First", "Second", "Third" };
            var notification = Notification("App One", "app.one", 63, "2026-06-21T11:08:01+01:00", rawText);

            await store.TryInsertNotificationAsync(
                notification,
                DateTimeOffset.Parse("2026-06-21T11:08:02+01:00"),
                CancellationToken.None);

            var stored = (await store.ListNotificationsAsync(CancellationToken.None))[0];
            AssertSequenceEqual(rawText, stored.RawTextElements);
        }
        finally
        {
            testDb.Delete();
        }
    }

    private static async Task StartupSnapshotNotificationsAreNotStored()
    {
        var testDb = TestDatabase.Create();
        try
        {
            var store = await testDb.OpenStoreAsync();
            var notification = Notification("App One", "app.one", 64, "2026-06-21T11:09:01+01:00");
            var provider = new FakeSnapshotProvider([notification]);
            var clock = new FakeClock(DateTimeOffset.Parse("2026-06-21T11:09:02+01:00"));
            var collector = NewCollector(provider, clock);
            var coordinator = new NotificationStorageCoordinator(store);

            var startup = await collector.SeedAsync(CancellationToken.None);
            await coordinator.ApplyAsync(startup, storeNewNotifications: false, CancellationToken.None);

            var source = await store.GetSourceAsync("app.one", CancellationToken.None);
            var notifications = await store.ListNotificationsAsync(CancellationToken.None);
            AssertNotNull(source);
            AssertEqual(0, notifications.Count);
        }
        finally
        {
            testDb.Delete();
        }
    }

    private static async Task RetentionRemovesOldNotificationsButPreservesSources()
    {
        var testDb = TestDatabase.Create();
        try
        {
            var store = await EnabledStoreAsync(testDb, "App One", "app.one", "2026-06-21T11:10:00+01:00");
            await store.TryInsertNotificationAsync(
                Notification("App One", "app.one", 65, "2026-06-18T10:00:00+01:00"),
                DateTimeOffset.Parse("2026-06-18T10:00:00+01:00"),
                CancellationToken.None);
            await store.TryInsertNotificationAsync(
                Notification("App One", "app.one", 66, "2026-06-21T11:10:01+01:00"),
                DateTimeOffset.Parse("2026-06-21T11:10:01+01:00"),
                CancellationToken.None);

            var deleted = await store.DeleteNotificationsOlderThanAsync(
                DateTimeOffset.Parse("2026-06-21T11:10:00+01:00") - TimeSpan.FromHours(72),
                CancellationToken.None);
            var notifications = await store.ListNotificationsAsync(CancellationToken.None);
            var sources = await store.ListSourcesAsync(CancellationToken.None);

            AssertEqual(1, deleted);
            AssertEqual(1, notifications.Count);
            AssertEqual(1, sources.Count);
            AssertEqual("app.one", sources[0].AppId);
        }
        finally
        {
            testDb.Delete();
        }
    }

    private static Task DisplayMapsNormalTitleAndBody()
    {
        var notification = DisplayNotification(
            title: "  Title  ",
            body: "  Body  ",
            rawTextElements: ["  Title  ", "  Body  "]);

        var display = NotificationDisplayMapper.Map(notification);

        AssertEqual("App One", display.SourceApp);
        AssertEqual(DateTimeOffset.Parse("2026-06-21T12:00:00+01:00"), display.Timestamp);
        AssertEqual("Title", display.PrimaryText);
        AssertEqual("Body", display.MessageText);
        return Task.CompletedTask;
    }

    private static Task DisplayUsesRawTextWhenTitleIsMissing()
    {
        var notification = DisplayNotification(
            title: "   ",
            body: "Body",
            rawTextElements: ["  Raw primary  ", "Raw secondary"]);

        var display = NotificationDisplayMapper.Map(notification);

        AssertEqual("Raw primary", display.PrimaryText);
        AssertEqual("Body", display.MessageText);
        return Task.CompletedTask;
    }

    private static Task DisplayUsesRemainingRawTextWhenBodyIsMissing()
    {
        var notification = DisplayNotification(
            title: "Title",
            body: "",
            rawTextElements: ["Title", "Line one", "Line two"]);

        var display = NotificationDisplayMapper.Map(notification);

        AssertEqual("Title", display.PrimaryText);
        AssertEqual("Line one\nLine two", display.MessageText);
        return Task.CompletedTask;
    }

    private static Task DisplayHidesEmptyFields()
    {
        var notification = DisplayNotification(
            title: "   ",
            body: "\t",
            rawTextElements: [" ", ""]);

        var display = NotificationDisplayMapper.Map(notification);

        AssertNull(display.PrimaryText);
        AssertNull(display.MessageText);
        return Task.CompletedTask;
    }

    private static Task DisplaySuppressesDuplicateTitleAndBody()
    {
        var notification = DisplayNotification(
            title: " Same ",
            body: "Same",
            rawTextElements: ["Same", "Same"]);

        var display = NotificationDisplayMapper.Map(notification);

        AssertEqual("Same", display.PrimaryText);
        AssertNull(display.MessageText);
        return Task.CompletedTask;
    }

    private static Task DisplayPreservesRawTextElementOrder()
    {
        var notification = DisplayNotification(
            title: "First",
            body: "",
            rawTextElements: ["First", "Second", "Second", "Third"]);

        var display = NotificationDisplayMapper.Map(notification);

        AssertEqual("Second\nSecond\nThird", display.MessageText);
        return Task.CompletedTask;
    }

    private static Task DisplayPreservesMultilineText()
    {
        var notification = DisplayNotification(
            title: "Title",
            body: "  first\r\nsecond\nthird  ",
            rawTextElements: ["Title", "first\r\nsecond\nthird"]);

        var display = NotificationDisplayMapper.Map(notification);

        AssertEqual("first\nsecond\nthird", display.MessageText);
        return Task.CompletedTask;
    }

    private static Task DisplayPreservesUrlsTickersPricesEmojiAndSymbols()
    {
        const string body = "https://example.com?q=$NVDA $123.45 +4.2% 🚀 ✓ ?";
        var notification = DisplayNotification(
            title: "Market update",
            body: body,
            rawTextElements: ["Market update", body]);

        var display = NotificationDisplayMapper.Map(notification);

        AssertEqual(body, display.MessageText);
        return Task.CompletedTask;
    }

    private static Task DisplayMappingDoesNotMutateRawValues()
    {
        const string title = "  Title\r\n";
        const string body = "  Body\r\nLine  ";
        var rawText = new[] { "  Title\r\n", "  Body\r\nLine  " };
        var notification = DisplayNotification(
            title: title,
            body: body,
            rawTextElements: rawText);

        _ = NotificationDisplayMapper.Map(notification);

        AssertEqual(title, notification.Title);
        AssertEqual(body, notification.Body);
        AssertSequenceEqual(rawText, notification.RawTextElements);
        return Task.CompletedTask;
    }

    private static Task DisplayMappingIsDeterministic()
    {
        var notification = DisplayNotification(
            title: "Title",
            body: "Body",
            rawTextElements: ["Title", "Body"]);

        var first = NotificationDisplayMapper.Map(notification);
        var second = NotificationDisplayMapper.Map(notification);

        AssertEqual(first, second);
        return Task.CompletedTask;
    }

    private static Task NormalTerminalOutputExcludesDebugMetadata()
    {
        var notification = DisplayNotification(
            appDisplayName: "App One",
            appId: "app.one",
            windowsNotificationId: 101,
            title: "Title",
            body: "Body",
            rawTextElements: ["Title", "Body"]);

        var output = string.Join('\n', NotificationTerminalFormatter.FormatNotification(notification, includeDebugRaw: false));

        AssertContains("12:00:00 · App One", output);
        AssertContains("Title", output);
        AssertContains("Body", output);
        AssertDoesNotContain("App id:", output);
        AssertDoesNotContain("Windows notification id:", output);
        AssertDoesNotContain("Raw text elements:", output);
        AssertDoesNotContain("Debug raw:", output);
        AssertDoesNotContain("Event:", output);
        AssertDoesNotContain("Enabled:", output);
        return Task.CompletedTask;
    }

    private static Task DebugRawOutputIncludesRawValuesAndCodePoints()
    {
        var notification = DisplayNotification(
            appDisplayName: "App One",
            appId: "app.one",
            windowsNotificationId: 102,
            title: "?\uFFFD🚀\u2068",
            body: "Body",
            rawTextElements: ["?\uFFFD🚀\u2068", "Body"]);

        var output = string.Join('\n', NotificationTerminalFormatter.FormatNotification(notification, includeDebugRaw: true));

        AssertContains("Debug raw:", output);
        AssertContains("Raw title:", output);
        AssertContains("Derived primary text:", output);
        AssertContains("App id: app.one", output);
        AssertContains("Windows notification id: 102", output);
        AssertContains("U+003F", output);
        AssertContains("U+FFFD", output);
        AssertContains("U+1F680", output);
        AssertContains("U+2068", output);
        return Task.CompletedTask;
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
        string creationTime,
        IReadOnlyList<string>? rawTextElements = null)
    {
        var rawText = rawTextElements ?? [$"Title {windowsNotificationId}", $"Body {windowsNotificationId}"];

        return new CapturedNotification(
            AppDisplayName: appDisplayName,
            AppId: appId,
            WindowsNotificationId: windowsNotificationId,
            CreationTime: DateTimeOffset.Parse(creationTime),
            Title: rawText.FirstOrDefault() ?? string.Empty,
            Body: rawText.Count > 1 ? string.Join(Environment.NewLine, rawText.Skip(1)) : string.Empty,
            RawTextElements: rawText);
    }

    private static CapturedNotification DisplayNotification(
        string title,
        string body,
        IReadOnlyList<string> rawTextElements,
        string appDisplayName = "App One",
        string appId = "app.one",
        uint windowsNotificationId = 100,
        string creationTime = "2026-06-21T12:00:00+01:00")
    {
        return new CapturedNotification(
            AppDisplayName: appDisplayName,
            AppId: appId,
            WindowsNotificationId: windowsNotificationId,
            CreationTime: DateTimeOffset.Parse(creationTime),
            Title: title,
            Body: body,
            RawTextElements: rawTextElements);
    }

    private static CollectorSnapshotResult SnapshotResult(
        string observedAt,
        IReadOnlyList<NotificationSource>? seenSources = null,
        IReadOnlyList<CapturedNotification>? newNotifications = null)
    {
        return new CollectorSnapshotResult(
            ObservedAt: DateTimeOffset.Parse(observedAt),
            SnapshotCount: newNotifications?.Count ?? seenSources?.Count ?? 0,
            SeenSources: seenSources ?? [],
            DiscoveredSources: [],
            NewNotifications: newNotifications ?? []);
    }

    private static async Task<SqliteNotificationStore> EnabledStoreAsync(
        TestDatabase testDb,
        string appDisplayName,
        string appId,
        string seenAt)
    {
        var store = await testDb.OpenStoreAsync();
        await store.UpsertSourceAsync(
            new NotificationSource(appDisplayName, appId),
            DateTimeOffset.Parse(seenAt),
            CancellationToken.None);
        await store.SetSourceEnabledAsync(appId, true, CancellationToken.None);
        return store;
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }

    private static T AssertNotNull<T>(T? value)
    {
        if (value is null)
        {
            throw new InvalidOperationException("Expected value not to be null.");
        }

        return value;
    }

    private static void AssertNull<T>(T? value)
    {
        if (value is not null)
        {
            throw new InvalidOperationException($"Expected value to be null, got {value}.");
        }
    }

    private static void AssertContains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected output to contain {expected}.");
        }
    }

    private static void AssertDoesNotContain(string unexpected, string actual)
    {
        if (actual.Contains(unexpected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected output not to contain {unexpected}.");
        }
    }

    private static void AssertSequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual)
    {
        AssertEqual(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            AssertEqual(expected[index], actual[index]);
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

    private sealed class TestDatabase
    {
        private readonly string _directory;

        private TestDatabase(string directory, string path)
        {
            _directory = directory;
            Path = path;
        }

        public string Path { get; }

        public static TestDatabase Create()
        {
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "windows-clean-notifs-tests",
                Guid.NewGuid().ToString("N"));
            var path = System.IO.Path.Combine(directory, "notifications.db");

            return new TestDatabase(directory, path);
        }

        public async Task<SqliteNotificationStore> OpenStoreAsync()
        {
            var store = new SqliteNotificationStore(Path);
            await store.InitializeAsync(CancellationToken.None);
            return store;
        }

        public void Delete()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
