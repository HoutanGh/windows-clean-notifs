using System.Collections.Concurrent;
using System.Threading.Channels;

namespace WindowsCleanNotifs.NotificationInspector;

public sealed class NotificationEventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<NotificationResponse>> _subscribers = new();
    private readonly int _capacity;

    public NotificationEventHub(int capacity = 100)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        _capacity = capacity;
    }

    public int SubscriberCount => _subscribers.Count;

    public NotificationEventSubscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<NotificationResponse>(
            new BoundedChannelOptions(_capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

        if (!_subscribers.TryAdd(id, channel))
        {
            throw new InvalidOperationException("Could not register notification event subscriber.");
        }

        return new NotificationEventSubscription(id, channel.Reader, RemoveSubscriber);
    }

    public Task PublishAsync(NotificationResponse notification, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Writer.TryWrite(notification);
        }

        return Task.CompletedTask;
    }

    private void RemoveSubscriber(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }
}

public sealed class NotificationEventSubscription : IAsyncDisposable
{
    private readonly Guid _id;
    private readonly Action<Guid> _unsubscribe;
    private bool _disposed;

    public NotificationEventSubscription(
        Guid id,
        ChannelReader<NotificationResponse> reader,
        Action<Guid> unsubscribe)
    {
        _id = id;
        Reader = reader;
        _unsubscribe = unsubscribe;
    }

    public ChannelReader<NotificationResponse> Reader { get; }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _unsubscribe(_id);
        }

        return ValueTask.CompletedTask;
    }
}
