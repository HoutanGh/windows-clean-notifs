using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace WindowsCleanNotifs.NotificationInspector;

public static class NotificationApi
{
    public const int DefaultPort = 4827;
    public const int DefaultNotificationLimit = 100;
    public const int MaximumNotificationLimit = 500;

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
            if (afterId is not null)
            {
                var replay = await store.ListEnabledNotificationsAfterIdAsync(afterId.Value, context.RequestAborted);
                foreach (var item in replay.Select(NotificationApiMapper.ToNotificationResponse))
                {
                    if (sentIds.Add(item.Id))
                    {
                        await WriteNotificationEventAsync(context.Response, item, context.RequestAborted);
                    }
                }
            }

            using var heartbeatTimer = new PeriodicTimer(HeartbeatInterval);
            while (!context.RequestAborted.IsCancellationRequested)
            {
                var readTask = subscription.Reader.WaitToReadAsync(context.RequestAborted).AsTask();
                var heartbeatTask = heartbeatTimer.WaitForNextTickAsync(context.RequestAborted).AsTask();
                var completed = await Task.WhenAny(readTask, heartbeatTask);

                if (completed == readTask)
                {
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
                    if (!await heartbeatTask)
                    {
                        break;
                    }

                    await context.Response.WriteAsync(": heartbeat\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
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
