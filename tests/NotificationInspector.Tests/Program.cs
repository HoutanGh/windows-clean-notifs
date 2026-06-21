using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
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
            ("debug raw output includes code points", DebugRawOutputIncludesRawValuesAndCodePoints),
            ("api health endpoint", ApiHealthEndpoint),
            ("api source listing and ordering", ApiSourceListingAndOrdering),
            ("api source selection persists", ApiEnablingAndDisablingSourcePersists),
            ("api malformed source selection returns 400", ApiMalformedSourceSelectionReturns400),
            ("api unknown source returns 404", ApiUnknownSourceReturns404),
            ("api notification limit validation", ApiNotificationLimitValidation),
            ("api cursor pagination", ApiCursorPagination),
            ("api newest-first ordering", ApiNewestFirstOrdering),
            ("api excludes disabled-source notifications", ApiDisabledSourceNotificationsAreExcluded),
            ("api responses use display mapper", ApiResponsesUseDisplayMapper),
            ("api excludes raw diagnostic fields", ApiRawDiagnosticFieldsAreNotExposed),
            ("api parses Discord context", ApiParsesDiscordContext),
            ("api marks unknown Discord context", ApiMarksUnknownDiscordContext),
            ("api omits Discord context for other apps", ApiOmitsDiscordContextForOtherApps),
            ("sse streams stored notification once", SseStoredNotificationReachesSubscriberOnce),
            ("sse afterId replay returns newer notifications", SseAfterIdReplayReturnsOnlyNewerNotifications),
            ("sse Last-Event-ID replay returns newer notifications", SseLastEventIdReplayReturnsOnlyNewerNotifications),
            ("sse excludes disabled-source notifications", SseDisabledSourceNotificationsAreNotStreamed),
            ("sse cancellation cleans subscriber", SseCancellationCleansSubscriber),
            ("frontend root serves compiled index", FrontendRootServesCompiledIndex),
            ("frontend spa fallback serves index", FrontendSpaFallbackServesIndex),
            ("frontend serving preserves api routes", FrontendServingPreservesApiRoutes),
            ("frontend does not serve repository files", FrontendDoesNotServeRepositoryFiles),
            ("frontend missing assets message", FrontendMissingAssetsMessage),
            ("open-browser option parsing", OpenBrowserOptionParsing),
            ("open-browser is not default", OpenBrowserIsNotDefault),
            ("open-browser requires serve", OpenBrowserRequiresServe),
            ("browser opener opens only once", BrowserOpenerOpensOnlyOnce),
            ("browser launch failure warns only", BrowserLaunchFailureWarnsOnly),
            ("port conflict message is actionable", PortConflictMessageIsActionable)
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
                Console.Error.WriteLine($"FAIL {test.Name}: {ex.GetType().Name}: {ex.Message}");
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

    private static async Task ApiHealthEndpoint()
    {
        await using var host = await ApiTestHost.CreateAsync();

        var response = await host.Client.GetAsync("/api/health");
        var json = await ReadJsonObjectAsync(response);

        AssertEqual(HttpStatusCode.OK, response.StatusCode);
        AssertEqual("ok", json.RootElement.GetProperty("status").GetString());
        AssertEqual("Allowed", json.RootElement.GetProperty("listenerAccessStatus").GetString());
        AssertEqual(true, json.RootElement.GetProperty("collectorRunning").GetBoolean());
        AssertEqual("00:00:01", json.RootElement.GetProperty("pollingInterval").GetString());
        AssertEqual("3.00:00:00", json.RootElement.GetProperty("retentionPeriod").GetString());
    }

    private static async Task ApiSourceListingAndOrdering()
    {
        await using var host = await ApiTestHost.CreateAsync();
        await AddSourceAsync(host.Store, "Beta", "app.beta", enabled: false, "2026-06-21T12:00:00+01:00");
        await AddSourceAsync(host.Store, "Zulu", "app.zulu", enabled: true, "2026-06-21T12:00:01+01:00");
        await AddSourceAsync(host.Store, "Alpha", "app.alpha", enabled: true, "2026-06-21T12:00:02+01:00");

        var response = await host.Client.GetAsync("/api/sources");
        var json = await ReadJsonArrayAsync(response);
        var sources = json.RootElement.EnumerateArray().ToArray();

        AssertEqual(HttpStatusCode.OK, response.StatusCode);
        AssertEqual(3, sources.Length);
        AssertEqual("app.alpha", sources[0].GetProperty("appId").GetString());
        AssertEqual(true, sources[0].GetProperty("enabled").GetBoolean());
        AssertEqual("app.zulu", sources[1].GetProperty("appId").GetString());
        AssertEqual(true, sources[1].GetProperty("enabled").GetBoolean());
        AssertEqual("app.beta", sources[2].GetProperty("appId").GetString());
        AssertEqual(false, sources[2].GetProperty("enabled").GetBoolean());
    }

    private static async Task ApiEnablingAndDisablingSourcePersists()
    {
        await using var host = await ApiTestHost.CreateAsync();
        await AddSourceAsync(host.Store, "App One", "app.one", enabled: false, "2026-06-21T12:01:00+01:00");

        var enable = await PutSourceSelectionAsync(host, "app.one", enabled: true);
        var enabledJson = await ReadJsonObjectAsync(enable);
        AssertEqual(HttpStatusCode.OK, enable.StatusCode);
        AssertEqual(true, enabledJson.RootElement.GetProperty("enabled").GetBoolean());

        var enabledSource = AssertNotNull(await host.Store.GetSourceAsync("app.one", CancellationToken.None));
        AssertEqual(true, enabledSource.Enabled);

        var disable = await PutSourceSelectionAsync(host, "app.one", enabled: false);
        var disabledJson = await ReadJsonObjectAsync(disable);
        AssertEqual(HttpStatusCode.OK, disable.StatusCode);
        AssertEqual(false, disabledJson.RootElement.GetProperty("enabled").GetBoolean());

        var disabledSource = AssertNotNull(await host.Store.GetSourceAsync("app.one", CancellationToken.None));
        AssertEqual(false, disabledSource.Enabled);
    }

    private static async Task ApiUnknownSourceReturns404()
    {
        await using var host = await ApiTestHost.CreateAsync();

        var response = await PutSourceSelectionAsync(host, "missing.app", enabled: true);

        AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task ApiMalformedSourceSelectionReturns400()
    {
        await using var host = await ApiTestHost.CreateAsync();

        var missingEnabled = new StringContent(
            """{"appId":"app.one"}""",
            Encoding.UTF8,
            "application/json");
        var missingEnabledResponse = await host.Client.PutAsync("/api/sources/selection", missingEnabled);

        var missingAppId = new StringContent(
            """{"enabled":true}""",
            Encoding.UTF8,
            "application/json");
        var missingAppIdResponse = await host.Client.PutAsync("/api/sources/selection", missingAppId);

        AssertEqual(HttpStatusCode.BadRequest, missingEnabledResponse.StatusCode);
        AssertEqual(HttpStatusCode.BadRequest, missingAppIdResponse.StatusCode);
    }

    private static async Task ApiNotificationLimitValidation()
    {
        await using var host = await ApiTestHost.CreateAsync();

        var tooLarge = await host.Client.GetAsync("/api/notifications?limit=501");
        var zero = await host.Client.GetAsync("/api/notifications?limit=0");
        var invalid = await host.Client.GetAsync("/api/notifications?limit=not-a-number");

        AssertEqual(HttpStatusCode.BadRequest, tooLarge.StatusCode);
        AssertEqual(HttpStatusCode.BadRequest, zero.StatusCode);
        AssertEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private static async Task ApiCursorPagination()
    {
        await using var host = await ApiTestHost.CreateAsync();
        await AddSourceAsync(host.Store, "App One", "app.one", enabled: true, "2026-06-21T12:03:00+01:00");
        var first = await InsertNotificationAsync(host.Store, "App One", "app.one", 201, "2026-06-21T12:03:01+01:00");
        var second = await InsertNotificationAsync(host.Store, "App One", "app.one", 202, "2026-06-21T12:03:02+01:00");
        var third = await InsertNotificationAsync(host.Store, "App One", "app.one", 203, "2026-06-21T12:03:03+01:00");

        var firstPage = await host.Client.GetAsync("/api/notifications?limit=2");
        var firstPageJson = await ReadJsonArrayAsync(firstPage);
        var firstPageItems = firstPageJson.RootElement.EnumerateArray().ToArray();

        AssertEqual(2, firstPageItems.Length);
        AssertEqual(third.Id, firstPageItems[0].GetProperty("id").GetInt64());
        AssertEqual(second.Id, firstPageItems[1].GetProperty("id").GetInt64());

        var nextPage = await host.Client.GetAsync($"/api/notifications?beforeId={second.Id.ToString(CultureInfo.InvariantCulture)}");
        var nextPageJson = await ReadJsonArrayAsync(nextPage);
        var nextPageItems = nextPageJson.RootElement.EnumerateArray().ToArray();

        AssertEqual(1, nextPageItems.Length);
        AssertEqual(first.Id, nextPageItems[0].GetProperty("id").GetInt64());
    }

    private static async Task ApiNewestFirstOrdering()
    {
        await using var host = await ApiTestHost.CreateAsync();
        await AddSourceAsync(host.Store, "App One", "app.one", enabled: true, "2026-06-21T12:04:00+01:00");
        var first = await InsertNotificationAsync(host.Store, "App One", "app.one", 211, "2026-06-21T12:04:01+01:00");
        var second = await InsertNotificationAsync(host.Store, "App One", "app.one", 212, "2026-06-21T12:04:02+01:00");
        var third = await InsertNotificationAsync(host.Store, "App One", "app.one", 213, "2026-06-21T12:04:03+01:00");

        var response = await host.Client.GetAsync("/api/notifications");
        var json = await ReadJsonArrayAsync(response);
        var items = json.RootElement.EnumerateArray().ToArray();

        AssertEqual(3, items.Length);
        AssertEqual(third.Id, items[0].GetProperty("id").GetInt64());
        AssertEqual(second.Id, items[1].GetProperty("id").GetInt64());
        AssertEqual(first.Id, items[2].GetProperty("id").GetInt64());
    }

    private static async Task ApiDisabledSourceNotificationsAreExcluded()
    {
        await using var host = await ApiTestHost.CreateAsync();
        await AddSourceAsync(host.Store, "Enabled App", "app.enabled", enabled: true, "2026-06-21T12:05:00+01:00");
        await AddSourceAsync(host.Store, "Disabled App", "app.disabled", enabled: true, "2026-06-21T12:05:01+01:00");
        var enabled = await InsertNotificationAsync(host.Store, "Enabled App", "app.enabled", 221, "2026-06-21T12:05:02+01:00");
        var disabled = await InsertNotificationAsync(host.Store, "Disabled App", "app.disabled", 222, "2026-06-21T12:05:03+01:00");
        await host.Store.SetSourceEnabledAsync("app.disabled", false, CancellationToken.None);

        var response = await host.Client.GetAsync("/api/notifications");
        var json = await ReadJsonArrayAsync(response);
        var items = json.RootElement.EnumerateArray().ToArray();

        AssertEqual(1, items.Length);
        AssertEqual(enabled.Id, items[0].GetProperty("id").GetInt64());
        AssertEqual(false, items.Any(item => item.GetProperty("id").GetInt64() == disabled.Id));
    }

    private static async Task ApiResponsesUseDisplayMapper()
    {
        await using var host = await ApiTestHost.CreateAsync();
        await AddSourceAsync(host.Store, "App One", "app.one", enabled: true, "2026-06-21T12:06:00+01:00");
        var notification = new CapturedNotification(
            AppDisplayName: "App One",
            AppId: "app.one",
            WindowsNotificationId: 231,
            CreationTime: DateTimeOffset.Parse("2026-06-21T12:06:01+01:00"),
            Title: "  Title  ",
            Body: "  Body  ",
            RawTextElements: ["  Title  ", "  Body  "]);
        await InsertNotificationAsync(host.Store, notification, DateTimeOffset.Parse("2026-06-21T12:06:02+01:00"));

        var response = await host.Client.GetAsync("/api/notifications");
        var json = await ReadJsonArrayAsync(response);
        var item = json.RootElement.EnumerateArray().Single();

        AssertEqual("App One", item.GetProperty("sourceApp").GetString());
        AssertEqual("Title", item.GetProperty("primaryText").GetString());
        AssertEqual("Body", item.GetProperty("messageText").GetString());
        AssertEqual("2026-06-21T11:06:01.0000000Z", item.GetProperty("timestamp").GetString());
    }

    private static async Task ApiRawDiagnosticFieldsAreNotExposed()
    {
        await using var host = await ApiTestHost.CreateAsync();
        await AddSourceAsync(host.Store, "App One", "app.one", enabled: true, "2026-06-21T12:07:00+01:00");
        await InsertNotificationAsync(host.Store, "App One", "app.one", 241, "2026-06-21T12:07:01+01:00");

        var response = await host.Client.GetAsync("/api/notifications");
        var body = await response.Content.ReadAsStringAsync();

        AssertDoesNotContain("rawText", body);
        AssertDoesNotContain("rawTextElements", body);
        AssertDoesNotContain("windowsNotificationId", body);
        AssertDoesNotContain("capturedAt", body);
        AssertDoesNotContain("body", body);
        AssertDoesNotContain("title", body);
    }

    private static async Task ApiParsesDiscordContext()
    {
        await using var host = await ApiTestHost.CreateAsync();
        const string appId = "com.squirrel.Discord.Discord";
        const string title = "\u2068Trader Bot\u2069 (\u2068#stocks-and-options\u2069, \u2068Main Chat\u2069)";
        await AddSourceAsync(host.Store, "Discord", appId, enabled: true, "2026-06-21T12:07:10+01:00");
        await InsertNotificationAsync(
            host.Store,
            new CapturedNotification(
                AppDisplayName: "Discord",
                AppId: appId,
                WindowsNotificationId: 242,
                CreationTime: DateTimeOffset.Parse("2026-06-21T12:07:11+01:00"),
                Title: title,
                Body: "NVDA breaking premarket high",
                RawTextElements: [title, "NVDA breaking premarket high"]),
            DateTimeOffset.Parse("2026-06-21T12:07:12+01:00"));

        var response = await host.Client.GetAsync("/api/notifications");
        var json = await ReadJsonArrayAsync(response);
        var item = json.RootElement.EnumerateArray().Single();
        var discord = item.GetProperty("discord");

        AssertEqual("parsed", discord.GetProperty("confidence").GetString());
        AssertEqual("Trader Bot", discord.GetProperty("sender").GetString());
        AssertEqual("#stocks-and-options", discord.GetProperty("channel").GetString());
        AssertEqual("Main Chat", discord.GetProperty("server").GetString());
    }

    private static async Task ApiMarksUnknownDiscordContext()
    {
        await using var host = await ApiTestHost.CreateAsync();
        const string appId = "com.squirrel.Discord.Discord";
        await AddSourceAsync(host.Store, "Discord", appId, enabled: true, "2026-06-21T12:07:20+01:00");
        await InsertNotificationAsync(
            host.Store,
            new CapturedNotification(
                AppDisplayName: "Discord",
                AppId: appId,
                WindowsNotificationId: 243,
                CreationTime: DateTimeOffset.Parse("2026-06-21T12:07:21+01:00"),
                Title: "Standalone Discord title",
                Body: "Message",
                RawTextElements: ["Standalone Discord title", "Message"]),
            DateTimeOffset.Parse("2026-06-21T12:07:22+01:00"));

        var response = await host.Client.GetAsync("/api/notifications");
        var json = await ReadJsonArrayAsync(response);
        var discord = json.RootElement.EnumerateArray().Single().GetProperty("discord");

        AssertEqual("unknown", discord.GetProperty("confidence").GetString());
        AssertEqual(JsonValueKind.Null, discord.GetProperty("sender").ValueKind);
        AssertEqual(JsonValueKind.Null, discord.GetProperty("channel").ValueKind);
        AssertEqual(JsonValueKind.Null, discord.GetProperty("server").ValueKind);
    }

    private static async Task ApiOmitsDiscordContextForOtherApps()
    {
        await using var host = await ApiTestHost.CreateAsync();
        await AddSourceAsync(host.Store, "App One", "app.one", enabled: true, "2026-06-21T12:07:30+01:00");
        await InsertNotificationAsync(host.Store, "App One", "app.one", 244, "2026-06-21T12:07:31+01:00");

        var response = await host.Client.GetAsync("/api/notifications");
        var json = await ReadJsonArrayAsync(response);
        var item = json.RootElement.EnumerateArray().Single();

        AssertEqual(JsonValueKind.Null, item.GetProperty("discord").ValueKind);
    }

    private static async Task SseStoredNotificationReachesSubscriberOnce()
    {
        await using var host = await ApiTestHost.CreateAsync();
        await AddSourceAsync(host.Store, "App One", "app.one", enabled: true, "2026-06-21T12:08:00+01:00");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await host.Client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var reader = new SseReader(stream);
        await WaitForSubscriberCountAsync(host.Hub, 1, cts.Token);

        var stored = await InsertNotificationAsync(host.Store, "App One", "app.one", 251, "2026-06-21T12:08:01+01:00");
        AssertEqual(true, await NotificationPublisher.PublishStoredNotificationAsync(host.Store, host.Hub, stored, cts.Token));

        var notification = await reader.ReadNextNotificationAsync(cts.Token);

        AssertEqual(stored.Id, notification.Id);
        AssertEqual("app.one", notification.AppId);

        var second = await InsertNotificationAsync(host.Store, "App One", "app.one", 252, "2026-06-21T12:08:02+01:00");
        AssertEqual(true, await NotificationPublisher.PublishStoredNotificationAsync(host.Store, host.Hub, second, cts.Token));

        var secondNotification = await reader.ReadNextNotificationAsync(cts.Token);

        AssertEqual(second.Id, secondNotification.Id);
        AssertEqual("app.one", secondNotification.AppId);
    }

    private static async Task SseAfterIdReplayReturnsOnlyNewerNotifications()
    {
        await using var host = await ApiTestHost.CreateAsync();
        await AddSourceAsync(host.Store, "App One", "app.one", enabled: true, "2026-06-21T12:09:00+01:00");
        var first = await InsertNotificationAsync(host.Store, "App One", "app.one", 261, "2026-06-21T12:09:01+01:00");
        var second = await InsertNotificationAsync(host.Store, "App One", "app.one", 262, "2026-06-21T12:09:02+01:00");
        var third = await InsertNotificationAsync(host.Store, "App One", "app.one", 263, "2026-06-21T12:09:03+01:00");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await host.Client.GetAsync($"/api/events?afterId={first.Id.ToString(CultureInfo.InvariantCulture)}", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var reader = new SseReader(stream);

        var replaySecond = await reader.ReadNextNotificationAsync(cts.Token);
        var replayThird = await reader.ReadNextNotificationAsync(cts.Token);

        AssertEqual(second.Id, replaySecond.Id);
        AssertEqual(third.Id, replayThird.Id);
    }

    private static async Task SseLastEventIdReplayReturnsOnlyNewerNotifications()
    {
        await using var host = await ApiTestHost.CreateAsync();
        await AddSourceAsync(host.Store, "App One", "app.one", enabled: true, "2026-06-21T12:09:10+01:00");
        var first = await InsertNotificationAsync(host.Store, "App One", "app.one", 264, "2026-06-21T12:09:11+01:00");
        var second = await InsertNotificationAsync(host.Store, "App One", "app.one", 265, "2026-06-21T12:09:12+01:00");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        request.Headers.Add("Last-Event-ID", first.Id.ToString(CultureInfo.InvariantCulture));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var reader = new SseReader(stream);

        var replaySecond = await reader.ReadNextNotificationAsync(cts.Token);

        AssertEqual(second.Id, replaySecond.Id);
    }

    private static async Task SseDisabledSourceNotificationsAreNotStreamed()
    {
        await using var host = await ApiTestHost.CreateAsync();
        await AddSourceAsync(host.Store, "App One", "app.one", enabled: true, "2026-06-21T12:10:00+01:00");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await host.Client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var reader = new SseReader(stream);
        await WaitForSubscriberCountAsync(host.Hub, 1, cts.Token);

        var stored = await InsertNotificationAsync(host.Store, "App One", "app.one", 271, "2026-06-21T12:10:01+01:00");
        await host.Store.SetSourceEnabledAsync("app.one", false, CancellationToken.None);

        AssertEqual(false, await NotificationPublisher.PublishStoredNotificationAsync(host.Store, host.Hub, stored, cts.Token));
        await AssertNoSseNotificationAsync(reader);
    }

    private static async Task SseCancellationCleansSubscriber()
    {
        await using var host = await ApiTestHost.CreateAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await host.Client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        await WaitForSubscriberCountAsync(host.Hub, 1, cts.Token);

        cts.Cancel();
        response.Dispose();

        using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await WaitForSubscriberCountAsync(host.Hub, 0, cleanupCts.Token);
    }

    private static async Task FrontendRootServesCompiledIndex()
    {
        using var frontend = TestFrontendAssets.Create();
        await using var host = await ApiTestHost.CreateAsync(frontend.DirectoryPath);

        var response = await host.Client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        AssertEqual(HttpStatusCode.OK, response.StatusCode);
        AssertContains("dashboard-root-marker", body);
        AssertEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    private static async Task FrontendServingPreservesApiRoutes()
    {
        using var frontend = TestFrontendAssets.Create();
        await using var host = await ApiTestHost.CreateAsync(frontend.DirectoryPath);

        var response = await host.Client.GetAsync("/api/health");
        var json = await ReadJsonObjectAsync(response);

        AssertEqual(HttpStatusCode.OK, response.StatusCode);
        AssertEqual("ok", json.RootElement.GetProperty("status").GetString());
    }

    private static async Task FrontendSpaFallbackServesIndex()
    {
        using var frontend = TestFrontendAssets.Create();
        await using var host = await ApiTestHost.CreateAsync(frontend.DirectoryPath);

        var response = await host.Client.GetAsync("/sources");
        var body = await response.Content.ReadAsStringAsync();

        AssertEqual(HttpStatusCode.OK, response.StatusCode);
        AssertContains("dashboard-root-marker", body);
    }

    private static async Task FrontendDoesNotServeRepositoryFiles()
    {
        using var frontend = TestFrontendAssets.Create();
        await using var host = await ApiTestHost.CreateAsync(frontend.DirectoryPath);

        var response = await host.Client.GetAsync("/AGENTS.md");
        var body = await response.Content.ReadAsStringAsync();

        AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
        AssertDoesNotContain("Required response format", body);
    }

    private static async Task FrontendMissingAssetsMessage()
    {
        var missingAssetsPath = Path.Combine(Path.GetTempPath(), "missing-dashboard-assets-" + Guid.NewGuid().ToString("N"));
        await using var host = await ApiTestHost.CreateAsync(missingAssetsPath);

        var response = await host.Client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        AssertEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertContains("Frontend assets have not been built.", body);
    }

    private static Task OpenBrowserOptionParsing()
    {
        var parseResult = WindowsCleanNotifs.NotificationInspector.Program.Options.Parse(
            ["--serve", "--open-browser", "--port", "4900"]);

        AssertNull(parseResult.Error);
        AssertEqual(true, parseResult.Options.Serve);
        AssertEqual(true, parseResult.Options.OpenBrowser);
        AssertEqual(4900, parseResult.Options.Port);
        return Task.CompletedTask;
    }

    private static Task OpenBrowserIsNotDefault()
    {
        var parseResult = WindowsCleanNotifs.NotificationInspector.Program.Options.Parse(["--serve"]);

        AssertNull(parseResult.Error);
        AssertEqual(true, parseResult.Options.Serve);
        AssertEqual(false, parseResult.Options.OpenBrowser);
        return Task.CompletedTask;
    }

    private static Task OpenBrowserRequiresServe()
    {
        var parseResult = WindowsCleanNotifs.NotificationInspector.Program.Options.Parse(["--open-browser"]);

        AssertEqual("--open-browser requires --serve.", parseResult.Error);
        return Task.CompletedTask;
    }

    private static Task BrowserOpenerOpensOnlyOnce()
    {
        var launcher = new RecordingDashboardBrowserLauncher();
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var opener = new DashboardBrowserOpener(launcher, error);
        var dashboardUri = WindowsCleanNotifs.NotificationInspector.Program.GetDashboardUri(4827);

        opener.OpenAfterServerStarted(enabled: false, dashboardUri);
        opener.OpenAfterServerStarted(enabled: true, dashboardUri);
        opener.OpenAfterServerStarted(enabled: true, dashboardUri);

        AssertEqual(1, launcher.OpenedUris.Count);
        AssertEqual(dashboardUri, launcher.OpenedUris[0]);
        AssertEqual(string.Empty, error.ToString());
        return Task.CompletedTask;
    }

    private static Task BrowserLaunchFailureWarnsOnly()
    {
        var launcher = new RecordingDashboardBrowserLauncher
        {
            ExceptionToThrow = new InvalidOperationException("browser blocked")
        };
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var opener = new DashboardBrowserOpener(launcher, error);

        opener.OpenAfterServerStarted(
            enabled: true,
            WindowsCleanNotifs.NotificationInspector.Program.GetDashboardUri(4827));

        AssertContains("Warning: could not open dashboard browser: browser blocked", error.ToString());
        AssertEqual(1, launcher.OpenedUris.Count);
        return Task.CompletedTask;
    }

    private static Task PortConflictMessageIsActionable()
    {
        var message = string.Join(
            '\n',
            WindowsCleanNotifs.NotificationInspector.Program.FormatPortConflictMessage(
                4827,
                "address already in use"));

        AssertContains("http://127.0.0.1:4827/", message);
        AssertContains("Port 4827 may already be in use.", message);
        AssertContains(".\\notifs.cmd --port 4900", message);
        AssertContains("address already in use", message);
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

    private static async Task AddSourceAsync(
        INotificationStore store,
        string appDisplayName,
        string appId,
        bool enabled,
        string seenAt)
    {
        await store.UpsertSourceAsync(
            new NotificationSource(appDisplayName, appId),
            DateTimeOffset.Parse(seenAt),
            CancellationToken.None);

        if (enabled)
        {
            await store.SetSourceEnabledAsync(appId, true, CancellationToken.None);
        }
    }

    private static async Task<HttpResponseMessage> PutSourceSelectionAsync(
        ApiTestHost host,
        string appId,
        bool enabled)
    {
        var content = new StringContent(
            $$"""{"appId":"{{appId}}","enabled":{{enabled.ToString().ToLowerInvariant()}}}""",
            Encoding.UTF8,
            "application/json");
        return await host.Client.PutAsync("/api/sources/selection", content);
    }

    private static async Task<StoredNotificationRecord> InsertNotificationAsync(
        INotificationStore store,
        string appDisplayName,
        string appId,
        uint windowsNotificationId,
        string creationTime)
    {
        return await InsertNotificationAsync(
            store,
            Notification(appDisplayName, appId, windowsNotificationId, creationTime),
            DateTimeOffset.Parse(creationTime).AddSeconds(1));
    }

    private static async Task<StoredNotificationRecord> InsertNotificationAsync(
        INotificationStore store,
        CapturedNotification notification,
        DateTimeOffset capturedAt)
    {
        var result = await store.TryInsertNotificationAsync(notification, capturedAt, CancellationToken.None);
        AssertEqual(NotificationInsertStatus.Stored, result.Status);
        return AssertNotNull(result.Notification);
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

    private static async Task<JsonDocument> ReadJsonObjectAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        var document = JsonDocument.Parse(content);
        AssertEqual(JsonValueKind.Object, document.RootElement.ValueKind);
        return document;
    }

    private static async Task<JsonDocument> ReadJsonArrayAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        var document = JsonDocument.Parse(content);
        AssertEqual(JsonValueKind.Array, document.RootElement.ValueKind);
        return document;
    }

    private static async Task AssertNoSseNotificationAsync(SseReader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try
        {
            var notification = await reader.ReadNextNotificationAsync(timeout.Token);
            throw new InvalidOperationException($"Expected no SSE notification, got {notification.Id}.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException) when (timeout.IsCancellationRequested)
        {
        }
    }

    private sealed class SseReader
    {
        private readonly StreamReader _reader;

        public SseReader(Stream stream)
        {
            _reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        }

        public async Task<NotificationResponse> ReadNextNotificationAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                string? eventName = null;
                string? id = null;
                string? data = null;

                while (true)
                {
                    var line = await _reader.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        throw new InvalidOperationException("SSE stream ended before a notification event was received.");
                    }

                    if (line.Length == 0)
                    {
                        break;
                    }

                    if (line.StartsWith(":", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (line.StartsWith("event:", StringComparison.Ordinal))
                    {
                        eventName = line["event:".Length..].Trim();
                    }
                    else if (line.StartsWith("id:", StringComparison.Ordinal))
                    {
                        id = line["id:".Length..].Trim();
                    }
                    else if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        data = line["data:".Length..].Trim();
                    }
                }

                if (eventName != "notification")
                {
                    continue;
                }

                var notification = JsonSerializer.Deserialize<NotificationResponse>(
                    data ?? throw new InvalidOperationException("SSE notification event had no data."),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidOperationException("SSE notification event data could not be parsed.");

                if (id is not null)
                {
                    AssertEqual(long.Parse(id, CultureInfo.InvariantCulture), notification.Id);
                }

                return notification;
            }
        }
    }

    private static async Task WaitForSubscriberCountAsync(
        NotificationEventHub hub,
        int expected,
        CancellationToken cancellationToken)
    {
        while (hub.SubscriberCount != expected)
        {
            await Task.Delay(10, cancellationToken);
        }
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

    private sealed class RecordingDashboardBrowserLauncher : IDashboardBrowserLauncher
    {
        public List<Uri> OpenedUris { get; } = [];

        public Exception? ExceptionToThrow { get; init; }

        public void Open(Uri dashboardUri)
        {
            OpenedUris.Add(dashboardUri);
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }
        }
    }

    private sealed class ApiTestHost : IAsyncDisposable
    {
        private readonly TestDatabase _testDb;
        private readonly WebApplication _app;

        private ApiTestHost(
            TestDatabase testDb,
            WebApplication app,
            HttpClient client,
            SqliteNotificationStore store,
            NotificationEventHub hub)
        {
            _testDb = testDb;
            _app = app;
            Client = client;
            Store = store;
            Hub = hub;
        }

        public HttpClient Client { get; }

        public SqliteNotificationStore Store { get; }

        public NotificationEventHub Hub { get; }

        public static async Task<ApiTestHost> CreateAsync(string? frontendAssetsPath = null)
        {
            var testDb = TestDatabase.Create();
            var store = await testDb.OpenStoreAsync();
            var hub = new NotificationEventHub(capacity: 10);
            var state = new NotificationServerState();
            state.SetListenerAccessStatus("Allowed");
            state.SetCollectorRunning(true);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [],
                EnvironmentName = "Production",
                ContentRootPath = System.IO.Path.GetTempPath()
            });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            var resolvedFrontendAssetsPath = frontendAssetsPath
                ?? System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "missing-notification-dashboard-assets-" + Guid.NewGuid().ToString("N"));

            NotificationApi.ConfigureServices(
                builder.Services,
                store,
                hub,
                state,
                new NotificationApiOptions(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromHours(72),
                    resolvedFrontendAssetsPath));

            var app = builder.Build();
            NotificationApi.MapEndpoints(app);
            await app.StartAsync();

            return new ApiTestHost(
                testDb,
                app,
                app.GetTestClient(),
                store,
                hub);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            _testDb.Delete();
        }
    }

    private sealed class TestFrontendAssets : IDisposable
    {
        private TestFrontendAssets(string directoryPath)
        {
            DirectoryPath = directoryPath;
        }

        public string DirectoryPath { get; }

        public static TestFrontendAssets Create()
        {
            var directoryPath = Path.Combine(
                Path.GetTempPath(),
                "notification-dashboard-assets-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(directoryPath, "assets"));
            File.WriteAllText(
                Path.Combine(directoryPath, "index.html"),
                """
                <!doctype html>
                <html>
                  <body>
                    <div id="root">dashboard-root-marker</div>
                    <script type="module" src="/assets/index.js"></script>
                  </body>
                </html>
                """);
            File.WriteAllText(Path.Combine(directoryPath, "assets", "index.js"), "console.log('dashboard asset');");

            return new TestFrontendAssets(directoryPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
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
