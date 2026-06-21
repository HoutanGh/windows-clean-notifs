using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace WindowsCleanNotifs.NotificationInspector;

public static class NotificationApi
{
    public const int DefaultPort = 4827;
    public const int DefaultNotificationLimit = 100;
    public const int MaximumNotificationLimit = 500;
    public const string FrontendAssetsDirectoryName = "wwwroot";

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string GetDefaultFrontendAssetsPath()
    {
        return Path.Combine(AppContext.BaseDirectory, FrontendAssetsDirectoryName);
    }

    public static bool FrontendAssetsAreBuilt(string assetsPath)
    {
        return File.Exists(Path.Combine(assetsPath, "index.html"));
    }

    public static void ConfigureServices(
        IServiceCollection services,
        INotificationStore store,
        NotificationEventHub eventHub,
        NotificationServerState serverState,
        NotificationApiOptions options)
    {
        services.AddRouting();
        services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

        services.AddSingleton(store);
        services.AddSingleton(eventHub);
        services.AddSingleton(serverState);
        services.AddSingleton(options);
    }

    public static void MapEndpoints(WebApplication app)
    {
        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                await WriteErrorAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "An internal server error occurred.");
            });
        });

        app.MapGet("/api/health", (
            NotificationServerState state,
            NotificationApiOptions options) =>
        {
            return Results.Json(
                new HealthResponse(
                    Status: "ok",
                    ListenerAccessStatus: state.ListenerAccessStatus,
                    CollectorRunning: state.CollectorRunning,
                    PollingInterval: options.PollInterval.ToString(),
                    RetentionPeriod: options.RetentionWindow.ToString()),
                JsonOptions);
        });

        app.MapGet("/api/sources", async (
            INotificationStore store,
            CancellationToken cancellationToken) =>
        {
            var sources = await store.ListSourcesAsync(cancellationToken);
            return Results.Json(sources.Select(NotificationApiMapper.ToSourceResponse), JsonOptions);
        });

        app.MapPut("/api/sources/selection", async (
            SourceSelectionRequest? request,
            INotificationStore store,
            CancellationToken cancellationToken) =>
        {
            if (request is null
                || string.IsNullOrWhiteSpace(request.AppId)
                || request.Enabled is null)
            {
                return Results.BadRequest(new ErrorResponse("Request must include appId and enabled."));
            }

            var appId = request.AppId.Trim();
            var changed = await store.SetSourceEnabledAsync(appId, request.Enabled.Value, cancellationToken);
            if (!changed)
            {
                return Results.NotFound(new ErrorResponse($"No source found for app id: {appId}"));
            }

            var updated = await store.GetSourceAsync(appId, cancellationToken)
                ?? throw new InvalidOperationException($"Updated source {appId} could not be read back.");

            return Results.Json(NotificationApiMapper.ToSourceResponse(updated), JsonOptions);
        });

        app.MapGet("/api/notifications", async (
            HttpContext context,
            INotificationStore store) =>
        {
            if (!TryReadLimit(context, out var limit, out var limitError))
            {
                return limitError;
            }

            if (!TryReadOptionalNonNegativeLong(context, "beforeId", out var beforeId, out var cursorError))
            {
                return cursorError;
            }

            var items = await store.ListEnabledNotificationsAsync(
                limit,
                beforeId,
                context.RequestAborted);
            return Results.Json(items.Select(NotificationApiMapper.ToNotificationResponse), JsonOptions);
        });

        app.MapGet("/api/events", StreamEventsAsync);

        MapFrontend(app);
    }

    private static async Task StreamEventsAsync(
        HttpContext context,
        INotificationStore store,
        NotificationEventHub eventHub)
    {
        if (!TryReadOptionalNonNegativeLong(context, "afterId", out var afterId, out var cursorError))
        {
            await ExecuteResultAsync(context, cursorError);
            return;
        }

        var replayAfterId = afterId;
        if (replayAfterId is null
            && !TryReadOptionalNonNegativeHeaderLong(context, "Last-Event-ID", out replayAfterId, out var headerError))
        {
            await ExecuteResultAsync(context, headerError);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        await context.Response.StartAsync(context.RequestAborted);
        await context.Response.WriteAsync(": connected\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);

        var sentIds = new HashSet<long>();
        await using var subscription = eventHub.Subscribe();

        try
        {
            if (replayAfterId is not null)
            {
                var replay = await store.ListEnabledNotificationsAfterIdAsync(replayAfterId.Value, context.RequestAborted);
                foreach (var item in replay.Select(NotificationApiMapper.ToNotificationResponse))
                {
                    if (sentIds.Add(item.Id))
                    {
                        await WriteNotificationEventAsync(context.Response, item, context.RequestAborted);
                    }
                }
            }

            while (!context.RequestAborted.IsCancellationRequested)
            {
                using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                var readTask = subscription.Reader.WaitToReadAsync(context.RequestAborted).AsTask();
                var heartbeatTask = Task.Delay(HeartbeatInterval, heartbeatCts.Token);
                var completed = await Task.WhenAny(readTask, heartbeatTask);

                if (completed == readTask)
                {
                    await heartbeatCts.CancelAsync();
                    if (!await readTask)
                    {
                        break;
                    }

                    while (subscription.Reader.TryRead(out var notification))
                    {
                        if (sentIds.Add(notification.Id))
                        {
                            await WriteNotificationEventAsync(context.Response, notification, context.RequestAborted);
                        }
                    }
                }
                else
                {
                    await context.Response.WriteAsync(": heartbeat\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
    }

    private static void MapFrontend(WebApplication app)
    {
        var options = app.Services.GetRequiredService<NotificationApiOptions>();
        var assetsPath = options.FrontendAssetsPath ?? GetDefaultFrontendAssetsPath();

        if (FrontendAssetsAreBuilt(assetsPath))
        {
            var fileProvider = new PhysicalFileProvider(assetsPath);
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = fileProvider
            });

            app.MapGet("/", context => WriteIndexHtmlAsync(context, assetsPath));
            app.MapFallback(context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    return WriteErrorAsync(
                        context,
                        StatusCodes.Status404NotFound,
                        "API endpoint not found.");
                }

                return WriteIndexHtmlAsync(context, assetsPath);
            });
            return;
        }

        app.MapGet("/", context => WriteMissingFrontendAssetsAsync(context, assetsPath));
        app.MapFallback(context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                return WriteErrorAsync(
                    context,
                    StatusCodes.Status404NotFound,
                    "API endpoint not found.");
            }

            return WriteMissingFrontendAssetsAsync(context, assetsPath);
        });
    }

    private static async Task WriteIndexHtmlAsync(HttpContext context, string assetsPath)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(
            Path.Combine(assetsPath, "index.html"),
            context.RequestAborted);
    }

    private static async Task WriteMissingFrontendAssetsAsync(HttpContext context, string assetsPath)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(
            "Frontend assets have not been built.\n"
            + "Run npm install and npm run build in src/NotificationDashboard.Web, then publish or run --serve again.\n"
            + $"Expected index.html at: {assetsPath}",
            context.RequestAborted);
    }

    private static bool TryReadLimit(
        HttpContext context,
        out int limit,
        out IResult error)
    {
        limit = DefaultNotificationLimit;
        error = Results.Empty;

        if (!context.Request.Query.TryGetValue("limit", out var values) || values.Count == 0)
        {
            return true;
        }

        if (values.Count != 1
            || !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out limit)
            || limit <= 0
            || limit > MaximumNotificationLimit)
        {
            error = Results.BadRequest(new ErrorResponse($"limit must be between 1 and {MaximumNotificationLimit}."));
            return false;
        }

        return true;
    }

    private static bool TryReadOptionalNonNegativeLong(
        HttpContext context,
        string name,
        out long? value,
        out IResult error)
    {
        value = null;
        error = Results.Empty;

        if (!context.Request.Query.TryGetValue(name, out var values) || values.Count == 0)
        {
            return true;
        }

        if (values.Count != 1
            || !long.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 0)
        {
            error = Results.BadRequest(new ErrorResponse($"{name} must be a non-negative integer."));
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryReadOptionalNonNegativeHeaderLong(
        HttpContext context,
        string name,
        out long? value,
        out IResult error)
    {
        value = null;
        error = Results.Empty;

        if (!context.Request.Headers.TryGetValue(name, out var values) || values.Count == 0)
        {
            return true;
        }

        var headerValues = values
            .Where(headerValue => !string.IsNullOrWhiteSpace(headerValue))
            .ToArray();
        if (headerValues.Length == 0)
        {
            return true;
        }

        if (headerValues.Length != 1
            || !long.TryParse(headerValues[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 0)
        {
            error = Results.BadRequest(new ErrorResponse($"{name} must be a non-negative integer."));
            return false;
        }

        value = parsed;
        return true;
    }

    private static async Task ExecuteResultAsync(HttpContext context, IResult result)
    {
        await result.ExecuteAsync(context);
    }

    private static async Task WriteNotificationEventAsync(
        HttpResponse response,
        NotificationResponse notification,
        CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(notification, JsonOptions);
        await response.WriteAsync($"event: notification\nid: {notification.Id}\ndata: {data}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static async Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string error)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            new ErrorResponse(error),
            JsonOptions,
            context.RequestAborted);
    }
}
