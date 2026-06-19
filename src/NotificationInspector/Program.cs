using System.Collections.Concurrent;
using System.Threading.Channels;
using Windows.ApplicationModel;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace WindowsCleanNotifs.NotificationInspector;

internal static class Program
{
    private const int UsageError = 64;
    private static readonly object ConsoleGate = new();

    public static async Task<int> Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options.ShowHelp)
        {
            PrintUsage();
            return options.Invalid ? UsageError : 0;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This spike only runs on Windows.");
            return 1;
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            Console.Error.WriteLine("This spike expects Windows 10 2004 / Windows 11 or newer.");
            return 1;
        }

        PrintPackageIdentity();

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
                Console.Error.WriteLine("TODO: If this fails from the console host on your machine, verify whether the request must be initiated from a packaged UI thread.");
                return 1;
            }

            Console.WriteLine($"Access status after request: {accessStatus}");
        }

        if (!options.Listen)
        {
            PrintAccessAdvice(accessStatus);
            return accessStatus == UserNotificationListenerAccessStatus.Allowed ? 0 : 2;
        }

        if (!options.PrintContent)
        {
            Console.Error.WriteLine("Refusing to listen without --print-content. This inspector prints notification title/body/raw text to the terminal.");
            return UsageError;
        }

        if (accessStatus != UserNotificationListenerAccessStatus.Allowed)
        {
            PrintAccessAdvice(accessStatus);
            return 2;
        }

        Console.WriteLine("Content printing is ON for this process because --print-content was supplied.");
        Console.WriteLine("Listening for toast notifications. Press Ctrl+C to stop.");
        Console.WriteLine();

        var seen = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        using var cts = new CancellationTokenSource();
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var changes = Channel.CreateUnbounded<UserNotificationChangedEventArgs>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            CancelQuietly(cts);
            stopped.TrySetResult(true);
        };

        void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs eventArgs)
        {
            if (!changes.Writer.TryWrite(eventArgs))
            {
                Console.Error.WriteLine($"Dropped notification change event: {eventArgs.ChangeKind} id={eventArgs.UserNotificationId}");
            }
        }

        var eventSubscriptionActive = false;
        Task? pump = null;
        try
        {
            listener.NotificationChanged += OnNotificationChanged;
            eventSubscriptionActive = true;
            pump = ProcessNotificationChangesAsync(listener, changes.Reader, seen, cts.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"NotificationChanged event subscription failed: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("Falling back to polling visible toast notifications once per second.");
        }

        try
        {
            await PrintCurrentNotificationsAsync(listener, seen);
            if (eventSubscriptionActive)
            {
                await stopped.Task;
            }
            else
            {
                try
                {
                    await PollNotificationsAsync(listener, seen, cts.Token);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                }
            }
        }
        finally
        {
            if (eventSubscriptionActive)
            {
                listener.NotificationChanged -= OnNotificationChanged;
            }

            changes.Writer.TryComplete();
            CancelQuietly(cts);

            if (pump is not null)
            {
                try
                {
                    await pump;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        Console.WriteLine("Stopped.");
        return 0;
    }

    private static async Task PrintCurrentNotificationsAsync(
        UserNotificationListener listener,
        ConcurrentDictionary<string, byte> seen,
        bool printCount = true,
        string eventSource = "existing")
    {
        IReadOnlyList<UserNotification> notifications;
        try
        {
            notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not read current notifications: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (printCount)
        {
            Console.WriteLine($"Current toast notifications visible to listener: {notifications.Count}");
        }

        foreach (var notification in notifications.OrderBy(notification => notification.CreationTime).ThenBy(notification => notification.Id))
        {
            TryPrintNotification(notification, eventSource, seen);
        }
    }

    private static async Task PollNotificationsAsync(
        UserNotificationListener listener,
        ConcurrentDictionary<string, byte> seen,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!cancellationToken.IsCancellationRequested && await timer.WaitForNextTickAsync(cancellationToken))
        {
            await PrintCurrentNotificationsAsync(listener, seen, printCount: false, eventSource: "polled");
        }
    }

    private static void CancelQuietly(CancellationTokenSource cts)
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

    private static async Task ProcessNotificationChangesAsync(
        UserNotificationListener listener,
        ChannelReader<UserNotificationChangedEventArgs> changes,
        ConcurrentDictionary<string, byte> seen,
        CancellationToken cancellationToken)
    {
        await foreach (var change in changes.ReadAllAsync(cancellationToken))
        {
            if (change.ChangeKind != UserNotificationChangedKind.Added)
            {
                PrintNonAddedChange(change);
                continue;
            }

            UserNotification notification;
            try
            {
                notification = listener.GetNotification(change.UserNotificationId);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not read notification {change.UserNotificationId}: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (notification is null)
            {
                Console.Error.WriteLine($"Notification {change.UserNotificationId} was added but is no longer available.");
                continue;
            }

            TryPrintNotification(notification, "added", seen);
        }
    }

    private static void TryPrintNotification(
        UserNotification notification,
        string eventSource,
        ConcurrentDictionary<string, byte> seen)
    {
        try
        {
            var appId = EmptyAsUnknown(notification.AppInfo.AppUserModelId);
            var dedupeKey = $"{appId}:{notification.Id}";
            if (!seen.TryAdd(dedupeKey, 0))
            {
                return;
            }

            var appName = EmptyAsUnknown(notification.AppInfo.DisplayInfo.DisplayName);
            var timestamp = notification.CreationTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
            var capturedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
            var rawText = ExtractRawText(notification);
            var title = rawText.FirstOrDefault() ?? string.Empty;
            var body = rawText.Count > 1 ? string.Join(Environment.NewLine, rawText.Skip(1)) : string.Empty;

            lock (ConsoleGate)
            {
                Console.WriteLine("----");
                Console.WriteLine($"Event: {eventSource}");
                Console.WriteLine($"App name: {appName}");
                Console.WriteLine($"App id: {appId}");
                Console.WriteLine($"Windows notification id: {notification.Id}");
                Console.WriteLine($"Timestamp: {timestamp}");
                Console.WriteLine($"Captured at: {capturedAt}");
                Console.WriteLine("Title:");
                Console.WriteLine(Indent(title));
                Console.WriteLine("Body/message:");
                Console.WriteLine(Indent(body));
                Console.WriteLine("Raw text elements:");

                if (rawText.Count == 0)
                {
                    Console.WriteLine("  <none>");
                }
                else
                {
                    for (var index = 0; index < rawText.Count; index++)
                    {
                        Console.WriteLine($"  [{index}] {OneLine(rawText[index])}");
                    }
                }

                Console.WriteLine();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not print notification: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static IReadOnlyList<string> ExtractRawText(UserNotification notification)
    {
        var visual = notification.Notification.Visual;
        var toastBinding = visual.GetBinding(KnownNotificationBindings.ToastGeneric);
        if (toastBinding is not null)
        {
            return toastBinding.GetTextElements().Select(element => element.Text ?? string.Empty).ToArray();
        }

        var allText = new List<string>();
        foreach (var binding in visual.Bindings)
        {
            allText.AddRange(binding.GetTextElements().Select(element => element.Text ?? string.Empty));
        }

        return allText;
    }

    private static void PrintNonAddedChange(UserNotificationChangedEventArgs change)
    {
        lock (ConsoleGate)
        {
            Console.WriteLine("----");
            Console.WriteLine($"Event: {change.ChangeKind}");
            Console.WriteLine($"Windows notification id: {change.UserNotificationId}");
            Console.WriteLine($"Observed at: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}");
            Console.WriteLine();
        }
    }

    private static void PrintPackageIdentity()
    {
        try
        {
            Console.WriteLine($"Package identity: {Package.Current.Id.FullName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Package identity: not detected ({ex.GetType().Name}). Running unpackaged console spike.");
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
                Console.WriteLine("Notification listener access is denied.");
                Console.WriteLine("Open Windows Settings > Privacy & security > App permissions > Notifications and allow this packaged app.");
                Console.WriteLine("Shortcut URI: ms-settings:privacy-notifications");
                break;
            case UserNotificationListenerAccessStatus.Unspecified:
                Console.WriteLine("Notification listener access is unspecified. Re-run with --request-access to show the Windows permission prompt.");
                break;
            default:
                Console.WriteLine($"Notification listener access is {status}.");
                break;
        }
    }

    private static string EmptyAsUnknown(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<unknown>" : value;
    }

    private static string Indent(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "  <empty>";
        }

        return "  " + value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\n  ", StringComparison.Ordinal);
    }

    private static string OneLine(string value)
    {
        return string.IsNullOrEmpty(value)
            ? "<empty>"
            : value.Replace("\r\n", "\\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Windows notification inspector spike");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  NotificationInspector.exe --check-access");
        Console.WriteLine("  NotificationInspector.exe --request-access");
        Console.WriteLine("  NotificationInspector.exe --listen --request-access --print-content");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --check-access     Print current UserNotificationListener access and exit.");
        Console.WriteLine("  --request-access   Ask Windows for notification listener access, then print status.");
        Console.WriteLine("  --listen           Subscribe to notification changes and print current/new toasts.");
        Console.WriteLine("  --print-content    Required with --listen; acknowledges notification contents will be printed.");
        Console.WriteLine("  --help             Show this help.");
    }

    private sealed record Options(
        bool CheckAccess,
        bool RequestAccess,
        bool Listen,
        bool PrintContent,
        bool ShowHelp,
        bool Invalid)
    {
        public static Options Parse(IReadOnlyList<string> args)
        {
            if (args.Count == 0)
            {
                return new Options(false, false, false, false, true, false);
            }

            var checkAccess = false;
            var requestAccess = false;
            var listen = false;
            var printContent = false;

            foreach (var arg in args)
            {
                switch (arg)
                {
                    case "--check-access":
                        checkAccess = true;
                        break;
                    case "--request-access":
                        requestAccess = true;
                        break;
                    case "--listen":
                        listen = true;
                        break;
                    case "--print-content":
                        printContent = true;
                        break;
                    case "--help":
                    case "-h":
                    case "/?":
                        return new Options(false, false, false, false, true, false);
                    default:
                        Console.Error.WriteLine($"Unknown option: {arg}");
                        return new Options(false, false, false, false, true, true);
                }
            }

            return new Options(checkAccess, requestAccess, listen, printContent, false, false);
        }
    }
}
