namespace WindowsCleanNotifs.NotificationInspector;

public sealed class NotificationServerState
{
    private readonly object _gate = new();
    private string _listenerAccessStatus = "Unknown";
    private bool _collectorRunning;

    public string ListenerAccessStatus
    {
        get
        {
            lock (_gate)
            {
                return _listenerAccessStatus;
            }
        }
    }

    public bool CollectorRunning
    {
        get
        {
            lock (_gate)
            {
                return _collectorRunning;
            }
        }
    }

    public void SetListenerAccessStatus(string status)
    {
        lock (_gate)
        {
            _listenerAccessStatus = status;
        }
    }

    public void SetCollectorRunning(bool running)
    {
        lock (_gate)
        {
            _collectorRunning = running;
        }
    }
}
