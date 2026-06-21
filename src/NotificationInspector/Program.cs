using System.Globalization;
using System.Security;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel;
using Windows.UI.Notifications.Management;

namespace WindowsCleanNotifs.NotificationInspector;

internal static class Program
{
    private const int UsageError = 64;
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromHours(72);
    private static readonly TimeSpan RetentionCleanupInterval = TimeSpan.FromMinutes(15);
    private static readonly object ConsoleGate = new();

    public static async Task<int> Main(string[] args)
    {
        ConfigureConsoleEncoding();

        var parseResult = Options.Parse(args);
        if (parseResult.Error is not null)
        {
            Console.Error.WriteLine(parseResult.Error);
            PrintUsage();
            return UsageError;
        }

        var options = parseResult.Options;
        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This collector only runs on Windows.");
            return 1;
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            Console.Error.WriteLine("This collector expects Windows 10 2004 / Windows 11 or newer.");
            return 1;
        }

        var store = new SqliteNotificationStore(StoragePaths.GetDefaultDatabasePath());
        try
        {
            await store.InitializeAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not initialize notification database: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        await RunRetentionCleanupAsync(store, DateTimeOffset.Now, CancellationToken.None);

        if (options.ListSources)
        {
            await PrintSourcesAsync(store, CancellationToken.None);
            return 0;
        }

        if (options.EnableSourceAppId is not null)
        {
            return await SetSourceEnabledAsync(store, options.EnableSourceAppId, enabled: true, CancellationToken.None);
        }

        if (options.DisableSourceAppId is not null)
        {
            return await SetSourceEnabledAsync(store, options.DisableSourceAppId, enabled: false, CancellationToken.None);
        }

        PrintPackageIdentity();
        Console.WriteLine($"Database: {store.DatabasePath}");

        UserNotificationListener listener;
        try
        {
            listener = UserNotificationListener.Current;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not create UserNotificationListener: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        var accessStatus = listener.GetAccessStatus();
        Console.WriteLine($"Access status: {accessStatus}");

        if (options.RequestAccess)
        {
            Console.WriteLine("Requesting notification listener access...");
            try
            {
                accessStatus = await listener.RequestAccessAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Access request failed: {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine("Try opening Windows Settings with: start ms-settings:privacy-notifications");
                return 1;
            }

            Console.WriteLine($"Access status after request: {accessStatus}");
        }

        if (options.CheckAccess || (!options.Listen && !options.Serve))
        {
            PrintAccessAdvice(accessStatus);
            return accessStatus == UserNotificationListenerAccessStatus.Allowed ? 0 : 2;
        }

        if (accessStatus != UserNotificationListenerAccessStatus.Allowed)
        {
            PrintAccessAdvice(accessStatus);
            return 2;
        }

        if (options.Serve)
        {
            return await RunServerModeAsync(listener, store, accessStatus, options);
        }

        Console.WriteLine(options.PrintContent
            ? "Content printing is ON for enabled sources because --print-content was supplied."
            : "Content printing is OFF. Enabled-source notifications will be stored but not printed.");
        if (options.DebugRaw)
        {
            Console.WriteLine("Raw debug printing is ON because --debug-raw was supplied.");
        }

        Console.WriteLine("Newly discovered sources are disabled by default.");
        Console.WriteLine($"Polling visible toast notifications every {FormatInterval(options.PollInterval)}.");
        Console.WriteLine("Press Ctrl+C to stop.");
        Console.WriteLine();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            RequestStop(cts);
        };

        var provider = new WindowsNotificationSnapshotProvider(listener);
        var collector = new PollingNotificationCollector(
            provider,
            dedupeRetention: TimeSpan.FromMinutes(10));
        var storageCoordinator = new NotificationStorageCoordinator(store);

        try
        {
            await RunCollectorAsync(
                collector,
                storageCoordinator,
                store,
                options.PollInterval,
                options.PrintContent,
                options.DebugRaw,
                eventHub: null,
                serverState: null,
                cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }

        Console.WriteLine("Stopped.");
        return 0;
    }

    private static async Task<int> RunServerModeAsync(
        UserNotificationListener listener,
        INotificationStore store,
        UserNotificationListenerAccessStatus accessStatus,
        Options options)
    {
        var eventHub = new NotificationEventHub();
        var serverState = new NotificationServerState();
        serverState.SetListenerAccessStatus(accessStatus.ToString());

        var frontendAssetsPath = NotificationApi.GetDefaultFrontendAssetsPath();
        var apiOptions = new NotificationApiOptions(
            PollInterval: options.PollInterval,
            RetentionWindow: RetentionWindow,
            FrontendAssetsPath: frontendAssetsPath);
        var dashboardUri = GetDashboardUri(options.Port);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = "Production"
        });
        builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port.ToString(CultureInfo.InvariantCulture)}");
        builder.Logging.ClearProviders();
        NotificationApi.ConfigureServices(builder.Services, store, eventHub, serverState, apiOptions);

        await using var app = builder.Build();
        NotificationApi.MapEndpoints(app);

        try
        {
            await app.StartAsync(CancellationToken.None);
        }
        catch (IOException ex)
        {
            foreach (var line in FormatPortConflictMessage(options.Port, ex.Message))
            {
                Console.Error.WriteLine(line);
            }

            return 1;
        }

        var browserOpener = new DashboardBrowserOpener(
            new ShellDashboardBrowserLauncher(),
            Console.Error);
        browserOpener.OpenAfterServerStarted(options.OpenBrowser, dashboardUri);

        Console.WriteLine(options.PrintContent
            ? "Content printing is ON for enabled sources because --print-content was supplied."
            : "Content printing is OFF. Enabled-source notifications will be stored but not printed.");
        if (options.DebugRaw)
        {
            Console.WriteLine("Raw debug printing is ON because --debug-raw was supplied.");
        }

        Console.WriteLine("Newly discovered sources are disabled by default.");
        Console.WriteLine($"Polling visible toast notifications every {FormatInterval(options.PollInterval)}.");
        Console.WriteLine($"Local dashboard: {dashboardUri}");
        Console.WriteLine($"Local API: http://127.0.0.1:{options.Port.ToString(CultureInfo.InvariantCulture)}/api/health");
        if (!NotificationApi.FrontendAssetsAreBuilt(frontendAssetsPath))
        {
            Console.WriteLine("Frontend assets have not been built; the dashboard URL will show build instructions.");
        }

        Console.WriteLine("Press Ctrl+C to stop.");
        Console.WriteLine();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            RequestStop(cts);
        };

        var provider = new WindowsNotificationSnapshotProvider(listener);
        var collector = new PollingNotificationCollector(
            provider,
            dedupeRetention: TimeSpan.FromMinutes(10));
        var storageCoordinator = new NotificationStorageCoordinator(store);

        var collectorTask = RunCollectorAsync(
            collector,
            storageCoordinator,
            store,
            options.PollInterval,
            options.PrintContent,
            options.DebugRaw,
            eventHub,
            serverState,
            cts.Token);

        try
        {
            var stopTask = Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
            var completed = await Task.WhenAny(collectorTask, stopTask);
            if (completed == collectorTask)
            {
                await collectorTask;
                RequestStop(cts);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        finally
        {
            RequestStop(cts);
            using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(shutdownCts.Token);
        }

        try
        {
            await collectorTask;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }

        Console.WriteLine("Stopped.");
        return 0;
    }

    private static async Task RunCollectorAsync(
        PollingNotificationCollector collector,
        NotificationStorageCoordinator storageCoordinator,
        INotificationStore store,
        TimeSpan pollInterval,
        bool printContent,
        bool debugRaw,
        NotificationEventHub? eventHub,
        NotificationServerState? serverState,
        CancellationToken cancellationToken)
    {
        serverState?.SetCollectorRunning(true);
        try
        {
            var nextRetentionCleanup = DateTimeOffset.Now + RetentionCleanupInterval;

            await SeedUntilSuccessfulAsync(collector, storageCoordinator, pollInterval, cancellationToken);

            using var timer = new PeriodicTimer(pollInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    var result = await collector.PollOnceAsync(cancellationToken);
                    var persisted = await storageCoordinator.ApplyAsync(result, storeNewNotifications: true, cancellationToken);
                    PrintPersistedCollectorResult(persisted, printContent, debugRaw);
                    await PublishStoredNotificationsAsync(store, eventHub, persisted, cancellationToken);

                    var now = DateTimeOffset.Now;
                    if (now >= nextRetentionCleanup)
                    {
                        await RunRetentionCleanupAsync(store, now, cancellationToken);
                        nextRetentionCleanup = now + RetentionCleanupInterval;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Polling/storage failed at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}: {ex.GetType().Name}: {ex.Message}");
                    Console.Error.WriteLine("Will retry on the next poll. Access denial is not treated as an empty notification feed.");
                }
            }
        }
        finally
        {
            serverState?.SetCollectorRunning(false);
        }
    }

    private static async Task SeedUntilSuccessfulAsync(
        PollingNotificationCollector collector,
        NotificationStorageCoordinator storageCoordinator,
        TimeSpan retryInterval,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                var startup = await collector.SeedAsync(cancellationToken);
                var persisted = await storageCoordinator.ApplyAsync(startup, storeNewNotifications: false, cancellationToken);
                Console.WriteLine($"Startup snapshot visible to listener: {startup.SnapshotCount}");
                PrintPersistedCollectorResult(persisted, printContent: false, debugRaw: false);
                Console.WriteLine();
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Startup snapshot failed at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}: {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine("Will retry before storing new notifications so existing toasts are not captured as new.");
                await Task.Delay(retryInterval, cancellationToken);
            }
        }
    }

    private static async Task RunRetentionCleanupAsync(
        INotificationStore store,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var deleted = await store.DeleteNotificationsOlderThanAsync(now - RetentionWindow, cancellationToken);
        if (deleted > 0)
        {
            Console.WriteLine($"Retention cleanup deleted {deleted} stored notification(s) older than {RetentionWindow.TotalHours:0} hours.");
        }
    }

    private static async Task PublishStoredNotificationsAsync(
        INotificationStore store,
        NotificationEventHub? eventHub,
        PersistedCollectorResult result,
        CancellationToken cancellationToken)
    {
        if (eventHub is null)
        {
            return;
        }

        foreach (var outcome in result.NotificationOutcomes)
        {
            if (outcome.Status == NotificationInsertStatus.Stored && outcome.StoredNotification is not null)
            {
                await NotificationPublisher.PublishStoredNotificationAsync(
                    store,
                    eventHub,
                    outcome.StoredNotification,
                    cancellationToken);
            }
        }
    }

    private static void PrintPersistedCollectorResult(
        PersistedCollectorResult result,
        bool printContent,
        bool debugRaw)
    {
        foreach (var source in result.DiscoveredSources)
        {
            PrintSourceDiscovered(source);
        }

        if (!printContent)
        {
            return;
        }

        foreach (var outcome in result.NotificationOutcomes)
        {
            if (outcome.Status == NotificationInsertStatus.Stored)
            {
                PrintNotification(outcome.Notification, debugRaw);
            }
        }
    }

    private static void PrintSourceDiscovered(NotificationSource source)
    {
        lock (ConsoleGate)
        {
            Console.WriteLine("----");
            Console.WriteLine("Source discovered");
            Console.WriteLine($"App name: {source.AppDisplayName}");
            Console.WriteLine($"App id: {source.AppId}");
            Console.WriteLine();
        }
    }

    private static void PrintNotification(CapturedNotification notification, bool debugRaw)
    {
        lock (ConsoleGate)
        {
            foreach (var line in NotificationTerminalFormatter.FormatNotification(notification, debugRaw))
            {
                Console.WriteLine(line);
            }
        }
    }

    private static async Task PrintSourcesAsync(
        INotificationStore store,
        CancellationToken cancellationToken)
    {
        var sources = await store.ListSourcesAsync(cancellationToken);
        Console.WriteLine($"Database: {store.DatabasePath}");

        if (sources.Count == 0)
        {
            Console.WriteLine("No notification sources have been discovered yet.");
            return;
        }

        foreach (var source in sources)
        {
            Console.WriteLine("----");
            Console.WriteLine($"App name: {source.AppDisplayName}");
            Console.WriteLine($"App id: {source.AppId}");
            Console.WriteLine($"Enabled: {source.Enabled.ToString().ToLowerInvariant()}");
            Console.WriteLine($"First seen: {FormatTimestamp(source.FirstSeenAt)}");
            Console.WriteLine($"Last seen: {FormatTimestamp(source.LastSeenAt)}");
        }
    }

    private static async Task<int> SetSourceEnabledAsync(
        INotificationStore store,
        string appId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var changed = await store.SetSourceEnabledAsync(appId, enabled, cancellationToken);
        if (!changed)
        {
            Console.Error.WriteLine($"No source found for app id: {appId}");
            Console.Error.WriteLine("Run --list-sources after the app has produced at least one Windows notification.");
            return 2;
        }

        var source = await store.GetSourceAsync(appId, cancellationToken);
        var state = enabled ? "enabled" : "disabled";
        Console.WriteLine($"Source {state}: {source?.AppDisplayName ?? appId}");
        Console.WriteLine($"App id: {appId}");
        return 0;
    }

    private static void PrintPackageIdentity()
    {
        try
        {
            Console.WriteLine($"Package identity: {Package.Current.Id.FullName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Package identity: not detected ({ex.GetType().Name}). Running unpackaged console collector.");
        }
    }

    private static void PrintAccessAdvice(UserNotificationListenerAccessStatus status)
    {
        switch (status)
        {
            case UserNotificationListenerAccessStatus.Allowed:
                Console.WriteLine("Notification listener access is allowed.");
                break;
            case UserNotificationListenerAccessStatus.Denied:
                Console.WriteLine("Notification listener access is denied; this is not an empty notification feed.");
                Console.WriteLine("Run again with --request-access, or open Windows Settings with: start ms-settings:privacy-notifications");
                break;
            case UserNotificationListenerAccessStatus.Unspecified:
                Console.WriteLine("Notification listener access is unspecified. Re-run with --request-access to show the Windows permission prompt.");
                break;
            default:
                Console.WriteLine($"Notification listener access is {status}.");
                break;
        }
    }

    private static void RequestStop(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (OperationCanceledException)
        {
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(exception => exception is OperationCanceledException))
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void ConfigureConsoleEncoding()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.InputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
        }
        catch (SecurityException)
        {
        }
        catch (ArgumentException)
        {
        }
    }

    private static string FormatInterval(TimeSpan interval)
    {
        if (interval.TotalMilliseconds < 1000)
        {
            return $"{interval.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms";
        }

        return $"{interval.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds";
    }

    private static string FormatTimestamp(DateTimeOffset timestamp)
    {
        return timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
    }

    internal static Uri GetDashboardUri(int port)
    {
        return new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/");
    }

    internal static IReadOnlyList<string> FormatPortConflictMessage(int port, string details)
    {
        var dashboardUri = GetDashboardUri(port);
        return
        [
            $"Could not start dashboard because {dashboardUri} is unavailable.",
            $"Port {port.ToString(CultureInfo.InvariantCulture)} may already be in use. Try: .\\notifs.cmd --port 4900",
            $"Details: {details}"
        ];
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Windows notification collector");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  NotificationInspector.exe --check-access");
        Console.WriteLine("  NotificationInspector.exe --request-access");
        Console.WriteLine("  NotificationInspector.exe --list-sources");
        Console.WriteLine("  NotificationInspector.exe --enable-source <app-id>");
        Console.WriteLine("  NotificationInspector.exe --disable-source <app-id>");
        Console.WriteLine("  NotificationInspector.exe --listen [--print-content] [--debug-raw] [--poll-interval <seconds>]");
        Console.WriteLine("  NotificationInspector.exe --serve [--open-browser] [--print-content] [--debug-raw] [--poll-interval <seconds>] [--port <number>]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --check-access             Print current UserNotificationListener access and exit.");
        Console.WriteLine("  --request-access           Ask Windows for notification listener access, then print status.");
        Console.WriteLine("  --list-sources             Print persisted notification sources and exit.");
        Console.WriteLine("  --enable-source <app-id>   Enable storage for a discovered source.");
        Console.WriteLine("  --disable-source <app-id>  Disable storage for a discovered source.");
        Console.WriteLine("  --listen                   Poll visible toast notifications and store enabled-source toasts.");
        Console.WriteLine("  --serve                    Poll notifications and expose the loopback HTTP API/SSE server.");
        Console.WriteLine("  --open-browser             With --serve, open the dashboard URL once after the server starts.");
        Console.WriteLine("  --print-content            Print concise enabled-source notification contents to the terminal.");
        Console.WriteLine("  --debug-raw                With --print-content, include raw fields and Unicode diagnostics.");
        Console.WriteLine("  --poll-interval <seconds>  Polling interval. Defaults to 1.");
        Console.WriteLine("  --port <number>            API port for --serve. Defaults to 4827.");
        Console.WriteLine("  --help                     Show this help.");
    }

    internal sealed record Options(
        bool CheckAccess,
        bool RequestAccess,
        bool Listen,
        bool Serve,
        bool OpenBrowser,
        bool PrintContent,
        bool DebugRaw,
        bool ListSources,
        string? EnableSourceAppId,
        string? DisableSourceAppId,
        TimeSpan PollInterval,
        int Port,
        bool ShowHelp)
    {
        public static OptionsParseResult Parse(IReadOnlyList<string> args)
        {
            var defaultOptions = new Options(
                CheckAccess: false,
                RequestAccess: false,
                Listen: false,
                Serve: false,
                OpenBrowser: false,
                PrintContent: false,
                DebugRaw: false,
                ListSources: false,
                EnableSourceAppId: null,
                DisableSourceAppId: null,
                PollInterval: TimeSpan.FromSeconds(1),
                Port: NotificationApi.DefaultPort,
                ShowHelp: args.Count == 0);

            if (args.Count == 0)
            {
                return new OptionsParseResult(defaultOptions, null);
            }

            var checkAccess = false;
            var requestAccess = false;
            var listen = false;
            var serve = false;
            var openBrowser = false;
            var printContent = false;
            var debugRaw = false;
            var listSources = false;
            string? enableSourceAppId = null;
            string? disableSourceAppId = null;
            var pollInterval = TimeSpan.FromSeconds(1);
            var port = NotificationApi.DefaultPort;

            for (var index = 0; index < args.Count; index++)
            {
                var arg = args[index];
                switch (arg)
                {
                    case "--check-access":
                        checkAccess = true;
                        break;
                    case "--request-access":
                        requestAccess = true;
                        break;
                    case "--list-sources":
                        listSources = true;
                        break;
                    case "--enable-source":
                        if (index + 1 >= args.Count)
                        {
                            return new OptionsParseResult(defaultOptions, "--enable-source requires an app id.");
                        }

                        enableSourceAppId = args[++index];
                        break;
                    case "--disable-source":
                        if (index + 1 >= args.Count)
                        {
                            return new OptionsParseResult(defaultOptions, "--disable-source requires an app id.");
                        }

                        disableSourceAppId = args[++index];
                        break;
                    case "--listen":
                        listen = true;
                        break;
                    case "--serve":
                        serve = true;
                        break;
                    case "--open-browser":
                        openBrowser = true;
                        break;
                    case "--print-content":
                        printContent = true;
                        break;
                    case "--debug-raw":
                        debugRaw = true;
                        break;
                    case "--poll-interval":
                        if (index + 1 >= args.Count)
                        {
                            return new OptionsParseResult(defaultOptions, "--poll-interval requires a number of seconds.");
                        }

                        index++;
                        if (!double.TryParse(args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
                        {
                            return new OptionsParseResult(defaultOptions, "--poll-interval must be a positive number of seconds.");
                        }

                        pollInterval = TimeSpan.FromSeconds(seconds);
                        break;
                    case "--port":
                        if (index + 1 >= args.Count)
                        {
                            return new OptionsParseResult(defaultOptions, "--port requires a number.");
                        }

                        index++;
                        if (!int.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out port)
                            || port < 1
                            || port > 65535)
                        {
                            return new OptionsParseResult(defaultOptions, "--port must be a number from 1 to 65535.");
                        }

                        break;
                    case "--help":
                    case "-h":
                    case "/?":
                        return new OptionsParseResult(defaultOptions with { ShowHelp = true }, null);
                    default:
                        return new OptionsParseResult(defaultOptions, $"Unknown option: {arg}");
                }
            }

            var sourceCommandCount =
                (listSources ? 1 : 0)
                + (enableSourceAppId is null ? 0 : 1)
                + (disableSourceAppId is null ? 0 : 1);

            if (sourceCommandCount > 1)
            {
                return new OptionsParseResult(defaultOptions, "Use only one source command at a time.");
            }

            if (debugRaw && !printContent)
            {
                return new OptionsParseResult(defaultOptions, "--debug-raw requires --print-content.");
            }

            if (listen && serve)
            {
                return new OptionsParseResult(defaultOptions, "Use --listen or --serve, not both.");
            }

            if (openBrowser && !serve)
            {
                return new OptionsParseResult(defaultOptions, "--open-browser requires --serve.");
            }

            return new OptionsParseResult(
                new Options(
                    checkAccess,
                    requestAccess,
                    listen,
                    serve,
                    openBrowser,
                    printContent,
                    debugRaw,
                    listSources,
                    enableSourceAppId,
                    disableSourceAppId,
                    pollInterval,
                    port,
                    ShowHelp: false),
                null);
        }
    }

    internal sealed record OptionsParseResult(Options Options, string? Error);
}
