import { useCallback, useEffect, useMemo, useState } from 'react';
import { ApiError, createHttpDashboardApi, type DashboardApi } from './api';
import { createBrowserNotificationEventSource, type NotificationEventSourceFactory } from './events';
import type { HealthResponse, NotificationItem, NotificationSource } from './types';

const PageSize = 100;
const defaultApi = createHttpDashboardApi();

type ConnectionStatus = 'connecting' | 'connected' | 'reconnecting' | 'unavailable';

type AppProps = {
  api?: DashboardApi;
  createEventSource?: NotificationEventSourceFactory;
};

export function App({
  api = defaultApi,
  createEventSource = createBrowserNotificationEventSource
}: AppProps) {
  const [health, setHealth] = useState<HealthResponse | null>(null);
  const [notifications, setNotifications] = useState<NotificationItem[]>([]);
  const [sources, setSources] = useState<NotificationSource[]>([]);
  const [loading, setLoading] = useState(true);
  const [feedError, setFeedError] = useState<string | null>(null);
  const [sourceError, setSourceError] = useState<string | null>(null);
  const [sourcesOpen, setSourcesOpen] = useState(false);
  const [sourcesLoading, setSourcesLoading] = useState(false);
  const [pendingSourceAppId, setPendingSourceAppId] = useState<string | null>(null);
  const [loadingOlder, setLoadingOlder] = useState(false);
  const [hasOlder, setHasOlder] = useState(false);
  const [connectionStatus, setConnectionStatus] = useState<ConnectionStatus>('connecting');
  const [streamCursor, setStreamCursor] = useState<number | undefined>();
  const [streamVersion, setStreamVersion] = useState(0);
  const [streamReady, setStreamReady] = useState(false);

  const enabledSourceCount = useMemo(
    () => sources.filter((source) => source.enabled).length,
    [sources]
  );

  const replaceNotifications = useCallback(async () => {
    const nextNotifications = sortNewestFirst(await api.getNotifications({ limit: PageSize }));
    setNotifications(nextNotifications);
    setHasOlder(nextNotifications.length === PageSize);
    setStreamCursor(getHighestId(nextNotifications));
    setStreamVersion((version) => version + 1);
    setStreamReady(true);
  }, [api]);

  const reloadSources = useCallback(async () => {
    const nextSources = sortSources(await api.getSources());
    setSources(nextSources);
  }, [api]);

  useEffect(() => {
    let cancelled = false;

    async function loadInitialState() {
      setLoading(true);
      setFeedError(null);
      setSourceError(null);

      try {
        const nextHealth = await api.getHealth();
        if (!cancelled) {
          setHealth(nextHealth);
        }
      } catch (error) {
        if (!cancelled) {
          setFeedError(toUserMessage(error));
        }
      }

      try {
        const [nextNotifications, nextSources] = await Promise.all([
          api.getNotifications({ limit: PageSize }),
          api.getSources()
        ]);

        if (cancelled) {
          return;
        }

        const sortedNotifications = sortNewestFirst(nextNotifications);
        setFeedError(null);
        setNotifications(sortedNotifications);
        setSources(sortSources(nextSources));
        setHasOlder(sortedNotifications.length === PageSize);
        setStreamCursor(getHighestId(sortedNotifications));
        setStreamVersion((version) => version + 1);
        setStreamReady(true);
      } catch (error) {
        if (!cancelled) {
          setFeedError(toUserMessage(error));
          setConnectionStatus('unavailable');
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    }

    void loadInitialState();

    return () => {
      cancelled = true;
    };
  }, [api]);

  useEffect(() => {
    if (!streamReady) {
      return;
    }

    if (health && health.listenerAccessStatus !== 'Allowed') {
      setConnectionStatus('unavailable');
      return;
    }

    let eventSource: ReturnType<NotificationEventSourceFactory>;
    try {
      eventSource = createEventSource(streamCursor);
    } catch (error) {
      setConnectionStatus('unavailable');
      setFeedError(toUserMessage(error));
      return;
    }

    setConnectionStatus('reconnecting');
    eventSource.onopen = () => {
      setConnectionStatus('connected');
    };
    eventSource.onerror = () => {
      setConnectionStatus('reconnecting');
    };
    eventSource.addEventListener('notification', (event) => {
      try {
        const notification = JSON.parse(event.data) as NotificationItem;
        setNotifications((current) => mergeNotification(current, notification));
        setFeedError(null);
      } catch {
        setConnectionStatus('unavailable');
        setFeedError('A live notification could not be read.');
      }
    });

    return () => {
      eventSource.close();
    };
  }, [createEventSource, health, streamCursor, streamReady, streamVersion]);

  async function openSources() {
    setSourcesOpen(true);
    await refreshSourcesForPanel();
  }

  async function refreshSourcesForPanel() {
    setSourcesLoading(true);
    setSourceError(null);
    try {
      await reloadSources();
    } catch (error) {
      setSourceError(toUserMessage(error));
    } finally {
      setSourcesLoading(false);
    }
  }

  async function toggleSource(source: NotificationSource, enabled: boolean) {
    setPendingSourceAppId(source.appId);
    setSourceError(null);

    try {
      await api.setSourceSelection(source.appId, enabled);
      await reloadSources();
      await replaceNotifications();
    } catch (error) {
      setSourceError(toUserMessage(error));
    } finally {
      setPendingSourceAppId(null);
    }
  }

  async function loadOlder() {
    const oldestId = getOldestId(notifications);
    if (oldestId === undefined) {
      setHasOlder(false);
      return;
    }

    setLoadingOlder(true);
    setFeedError(null);
    try {
      const older = sortNewestFirst(await api.getNotifications({ limit: PageSize, beforeId: oldestId }));
      setNotifications((current) => mergeOlderNotifications(current, older));
      setHasOlder(older.length === PageSize);
    } catch (error) {
      setFeedError(toUserMessage(error));
    } finally {
      setLoadingOlder(false);
    }
  }

  return (
    <div className="app-shell">
      <header className="app-header">
        <div>
          <h1>Windows Clean Notifications</h1>
          <p className="status-line">
            <span className={`status-dot status-${connectionStatus}`} aria-hidden="true" />
            {connectionLabel(connectionStatus)}
          </p>
        </div>
        <button type="button" className="button" onClick={openSources}>
          Sources
        </button>
      </header>

      <main className="feed-shell">
        {health && health.listenerAccessStatus !== 'Allowed' ? (
          <StateMessage title="Notification access unavailable" detail={`Access status: ${health.listenerAccessStatus}`} />
        ) : null}

        {feedError ? <StateMessage title="Backend unavailable" detail={feedError} /> : null}

        {loading ? <StateMessage title="Loading notifications" /> : null}

        {!loading && !feedError && health?.listenerAccessStatus === 'Allowed' && enabledSourceCount === 0 ? (
          <StateMessage title="No enabled sources" detail="Open Sources to enable a detected application." />
        ) : null}

        {!loading
        && !feedError
        && health?.listenerAccessStatus === 'Allowed'
        && enabledSourceCount > 0
        && notifications.length === 0 ? (
          <StateMessage title="No notifications yet" detail="Enabled sources will appear here when Windows exposes new toasts." />
        ) : null}

        {notifications.length > 0 ? (
          <ol className="notification-feed" aria-label="Notifications">
            {notifications.map((notification) => (
              <NotificationRow key={notification.id} notification={notification} />
            ))}
          </ol>
        ) : null}

        {notifications.length > 0 && hasOlder ? (
          <div className="load-older-row">
            <button type="button" className="button subtle" disabled={loadingOlder} onClick={loadOlder}>
              {loadingOlder ? 'Loading...' : 'Load older'}
            </button>
          </div>
        ) : null}
      </main>

      {sourcesOpen ? (
        <section className="sources-panel" role="dialog" aria-modal="true" aria-labelledby="sources-title">
          <div className="sources-header">
            <h2 id="sources-title">Sources</h2>
            <div className="sources-actions">
              <button type="button" className="button subtle" disabled={sourcesLoading} onClick={refreshSourcesForPanel}>
                Refresh
              </button>
              <button type="button" className="button" onClick={() => setSourcesOpen(false)}>
                Close
              </button>
            </div>
          </div>

          {sourceError ? <p className="error-text">{sourceError}</p> : null}
          {sourcesLoading ? <p className="muted-text">Loading sources...</p> : null}
          {!sourcesLoading && sources.length === 0 ? (
            <p className="muted-text">No sources discovered yet.</p>
          ) : null}

          {sources.length > 0 ? (
            <ul className="source-list">
              {sources.map((source) => (
                <li key={source.appId} className="source-row" data-testid="source-row">
                  <label>
                    <input
                      type="checkbox"
                      checked={source.enabled}
                      disabled={pendingSourceAppId === source.appId}
                      onChange={(event) => void toggleSource(source, event.currentTarget.checked)}
                    />
                    <span>
                      <strong>{source.displayName || source.appId}</strong>
                      <small>{source.appId}</small>
                    </span>
                  </label>
                </li>
              ))}
            </ul>
          ) : null}
        </section>
      ) : null}
    </div>
  );
}

function NotificationRow({ notification }: { notification: NotificationItem }) {
  return (
    <li className="notification-row" data-testid="notification-row" data-id={notification.id}>
      <time dateTime={notification.timestamp}>{formatLocalTimestamp(notification.timestamp)}</time>
      <div className="notification-content">
        <div className="source-name">{notification.sourceApp}</div>
        {isPresent(notification.primaryText) ? (
          <div className="primary-text">{notification.primaryText}</div>
        ) : null}
        {isPresent(notification.messageText) ? (
          <div className="message-text" data-testid={`message-text-${notification.id}`}>{notification.messageText}</div>
        ) : null}
      </div>
    </li>
  );
}

function StateMessage({ title, detail }: { title: string; detail?: string }) {
  return (
    <div className="state-message">
      <strong>{title}</strong>
      {detail ? <span>{detail}</span> : null}
    </div>
  );
}

function sortNewestFirst(items: NotificationItem[]): NotificationItem[] {
  return [...items].sort((left, right) => right.id - left.id);
}

function sortSources(items: NotificationSource[]): NotificationSource[] {
  return [...items].sort((left, right) => {
    if (left.enabled !== right.enabled) {
      return left.enabled ? -1 : 1;
    }

    return (left.displayName || left.appId).localeCompare(right.displayName || right.appId);
  });
}

function mergeNotification(current: NotificationItem[], next: NotificationItem): NotificationItem[] {
  if (current.some((item) => item.id === next.id)) {
    return current;
  }

  return sortNewestFirst([next, ...current]);
}

function mergeOlderNotifications(current: NotificationItem[], older: NotificationItem[]): NotificationItem[] {
  const seen = new Set(current.map((item) => item.id));
  const merged = [...current];
  for (const item of older) {
    if (!seen.has(item.id)) {
      merged.push(item);
      seen.add(item.id);
    }
  }

  return sortNewestFirst(merged);
}

function getHighestId(items: NotificationItem[]): number | undefined {
  if (items.length === 0) {
    return undefined;
  }

  return Math.max(...items.map((item) => item.id));
}

function getOldestId(items: NotificationItem[]): number | undefined {
  if (items.length === 0) {
    return undefined;
  }

  return Math.min(...items.map((item) => item.id));
}

function isPresent(value: string | null | undefined): value is string {
  return value !== null && value !== undefined && value.length > 0;
}

function formatLocalTimestamp(value: string): string {
  const timestamp = new Date(value);
  if (Number.isNaN(timestamp.getTime())) {
    return value;
  }

  return new Intl.DateTimeFormat(undefined, {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit'
  }).format(timestamp);
}

function connectionLabel(status: ConnectionStatus): string {
  switch (status) {
    case 'connected':
      return 'Live connected';
    case 'reconnecting':
      return 'Live reconnecting';
    case 'unavailable':
      return 'Live unavailable';
    default:
      return 'Live connecting';
  }
}

function toUserMessage(error: unknown): string {
  if (error instanceof ApiError) {
    return error.message;
  }

  if (error instanceof Error && error.message) {
    return error.message;
  }

  return 'Request failed.';
}
