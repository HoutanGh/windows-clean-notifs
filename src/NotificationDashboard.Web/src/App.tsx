import { type CSSProperties, useCallback, useEffect, useMemo, useState } from 'react';
import { ApiError, createHttpDashboardApi, type DashboardApi } from './api';
import { createBrowserNotificationEventSource, type NotificationEventSourceFactory } from './events';
import type { HealthResponse, NotificationItem, NotificationSource } from './types';

const PageSize = 100;
const defaultApi = createHttpDashboardApi();
const DiscordAppId = 'com.squirrel.Discord.Discord';
const UngroupedLabel = 'Ungrouped';
const ThemeStorageKey = 'windows-clean-notifs-theme';
const HiddenDiscordChannelsStorageKey = 'windows-clean-notifs-hidden-discord-channels';
const ChromeHiddenStorageKey = 'windows-clean-notifs-chrome-hidden';

type ConnectionStatus = 'connecting' | 'connected' | 'reconnecting' | 'unavailable';
type ViewMode = 'feed' | 'discord';
type ThemeMode = 'light' | 'night';
type DashboardControlsVariant = 'header' | 'overlay';

type AppProps = {
  api?: DashboardApi;
  createEventSource?: NotificationEventSourceFactory;
};

type DiscordChannelGroup = {
  key: string;
  name: string;
  context: string | null;
  latestId: number;
  notifications: NotificationItem[];
};

type DashboardControlsProps = {
  variant: DashboardControlsVariant;
  discordAvailable: boolean;
  activeView: ViewMode;
  onViewChange: (view: ViewMode) => void;
  themeMode: ThemeMode;
  onThemeModeChange: (themeMode: ThemeMode) => void;
  onOpenSources: () => void;
  chromeHidden: boolean;
  onChromeHiddenChange: (chromeHidden: boolean) => void;
  hiddenChannels: DiscordChannelGroup[];
  onShowChannel: (channelKey: string) => void;
  onShowAllChannels: () => void;
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
  const [activeView, setActiveView] = useState<ViewMode>('feed');
  const [themeMode, setThemeMode] = useState<ThemeMode>(readInitialThemeMode);
  const [chromeHidden, setChromeHidden] = useState(readInitialChromeHidden);
  const [hiddenDiscordChannelKeys, setHiddenDiscordChannelKeys] = useState<Set<string>>(
    readInitialHiddenDiscordChannelKeys
  );

  const enabledSourceCount = useMemo(
    () => sources.filter((source) => source.enabled).length,
    [sources]
  );
  const discordAvailable = useMemo(
    () => sources.some((source) => source.enabled && isDiscordSource(source)),
    [sources]
  );
  const discordNotifications = useMemo(
    () => sortNewestFirst(notifications.filter(isDiscordNotification)),
    [notifications]
  );
  const discordChannels = useMemo(
    () => groupDiscordNotifications(discordNotifications),
    [discordNotifications]
  );
  const visibleDiscordChannels = useMemo(
    () => discordChannels.filter((channel) => !hiddenDiscordChannelKeys.has(channel.key)),
    [discordChannels, hiddenDiscordChannelKeys]
  );
  const hiddenDiscordChannels = useMemo(
    () => discordChannels.filter((channel) => hiddenDiscordChannelKeys.has(channel.key)),
    [discordChannels, hiddenDiscordChannelKeys]
  );

  useEffect(() => {
    if (activeView === 'discord' && !discordAvailable) {
      setActiveView('feed');
    }
  }, [activeView, discordAvailable]);

  useEffect(() => {
    document.documentElement.dataset.theme = themeMode;
    try {
      window.localStorage.setItem(ThemeStorageKey, themeMode);
    } catch {
    }
  }, [themeMode]);

  useEffect(() => {
    try {
      window.localStorage.setItem(
        HiddenDiscordChannelsStorageKey,
        JSON.stringify([...hiddenDiscordChannelKeys].sort())
      );
    } catch {
    }
  }, [hiddenDiscordChannelKeys]);

  useEffect(() => {
    try {
      window.localStorage.setItem(ChromeHiddenStorageKey, chromeHidden ? 'true' : 'false');
    } catch {
    }
  }, [chromeHidden]);

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

  function hideDiscordChannel(channelKey: string) {
    setHiddenDiscordChannelKeys((current) => {
      const next = new Set(current);
      next.add(channelKey);
      return next;
    });
  }

  function showDiscordChannel(channelKey: string) {
    setHiddenDiscordChannelKeys((current) => {
      const next = new Set(current);
      next.delete(channelKey);
      return next;
    });
  }

  function showAllDiscordChannels() {
    setHiddenDiscordChannelKeys(new Set());
  }

  return (
    <div className={chromeHidden ? 'app-shell chrome-hidden' : 'app-shell'}>
      {chromeHidden ? (
        <div className="chrome-hover-zone" aria-label="Hidden dashboard controls">
          <div className="chrome-overlay-toolbar">
            <DashboardControls
              variant="overlay"
              discordAvailable={discordAvailable}
              activeView={activeView}
              onViewChange={setActiveView}
              themeMode={themeMode}
              onThemeModeChange={setThemeMode}
              onOpenSources={openSources}
              chromeHidden={chromeHidden}
              onChromeHiddenChange={setChromeHidden}
              hiddenChannels={hiddenDiscordChannels}
              onShowChannel={showDiscordChannel}
              onShowAllChannels={showAllDiscordChannels}
            />
          </div>
        </div>
      ) : (
        <header className="app-header">
          <div>
            <h1>Windows Clean Notifications</h1>
            <p className="status-line">
              <span className={`status-dot status-${connectionStatus}`} aria-hidden="true" />
              {connectionLabel(connectionStatus)}
            </p>
          </div>
          <DashboardControls
            variant="header"
            discordAvailable={discordAvailable}
            activeView={activeView}
            onViewChange={setActiveView}
            themeMode={themeMode}
            onThemeModeChange={setThemeMode}
            onOpenSources={openSources}
            chromeHidden={chromeHidden}
            onChromeHiddenChange={setChromeHidden}
            hiddenChannels={hiddenDiscordChannels}
            onShowChannel={showDiscordChannel}
            onShowAllChannels={showAllDiscordChannels}
          />
        </header>
      )}

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
        && activeView === 'feed'
        && notifications.length === 0 ? (
          <StateMessage title="No notifications yet" detail="Enabled sources will appear here when Windows exposes new toasts." />
        ) : null}

        {!loading
        && !feedError
        && health?.listenerAccessStatus === 'Allowed'
        && enabledSourceCount > 0
        && activeView === 'discord'
        && discordAvailable
        && discordNotifications.length === 0 ? (
          <StateMessage title="No Discord notifications yet" detail="Discord notifications will appear here when Windows exposes new toasts." />
        ) : null}

        {activeView === 'feed' && notifications.length > 0 ? (
          <ol className="notification-feed" aria-label="Notifications">
            {notifications.map((notification) => (
              <NotificationRow key={notification.id} notification={notification} />
            ))}
          </ol>
        ) : null}

        {activeView === 'discord' && discordAvailable && discordNotifications.length > 0 ? (
          <DiscordBoard
            channels={visibleDiscordChannels}
            hiddenChannels={hiddenDiscordChannels}
            showHiddenChannelsBar={!chromeHidden}
            onHideChannel={hideDiscordChannel}
            onShowChannel={showDiscordChannel}
            onShowAllChannels={showAllDiscordChannels}
          />
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

function DashboardControls({
  variant,
  discordAvailable,
  activeView,
  onViewChange,
  themeMode,
  onThemeModeChange,
  onOpenSources,
  chromeHidden,
  onChromeHiddenChange,
  hiddenChannels,
  onShowChannel,
  onShowAllChannels
}: DashboardControlsProps) {
  return (
    <div className={variant === 'header' ? 'header-actions' : 'chrome-overlay-actions'}>
      {discordAvailable ? (
        <div className="view-switch" role="tablist" aria-label="View">
          <button
            type="button"
            role="tab"
            className={activeView === 'feed' ? 'active' : undefined}
            aria-selected={activeView === 'feed'}
            onClick={() => onViewChange('feed')}
          >
            Feed
          </button>
          <button
            type="button"
            role="tab"
            className={activeView === 'discord' ? 'active' : undefined}
            aria-selected={activeView === 'discord'}
            onClick={() => onViewChange('discord')}
          >
            Discord
          </button>
        </div>
      ) : null}
      <div className="theme-switch" role="group" aria-label="Theme">
        <button
          type="button"
          className={themeMode === 'light' ? 'active' : undefined}
          aria-pressed={themeMode === 'light'}
          onClick={() => onThemeModeChange('light')}
        >
          Light
        </button>
        <button
          type="button"
          className={themeMode === 'night' ? 'active' : undefined}
          aria-pressed={themeMode === 'night'}
          onClick={() => onThemeModeChange('night')}
        >
          Night
        </button>
      </div>
      {variant === 'overlay' && hiddenChannels.length > 0 ? (
        <HiddenDiscordChannelsControls
          channels={hiddenChannels}
          onShowChannel={onShowChannel}
          onShowAllChannels={onShowAllChannels}
          compact
        />
      ) : null}
      <button type="button" className="button" onClick={onOpenSources}>
        Sources
      </button>
      <button type="button" className="button subtle" onClick={() => onChromeHiddenChange(!chromeHidden)}>
        {chromeHidden ? 'Show controls' : 'Hide controls'}
      </button>
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

function HiddenDiscordChannelsControls({
  channels,
  onShowChannel,
  onShowAllChannels,
  compact = false
}: {
  channels: DiscordChannelGroup[];
  onShowChannel: (channelKey: string) => void;
  onShowAllChannels: () => void;
  compact?: boolean;
}) {
  return (
    <div
      className={compact ? 'discord-hidden-controls compact' : 'discord-hidden-controls'}
      aria-label="Hidden Discord channels"
    >
      <span>{compact ? `Hidden ${channels.length}` : 'Hidden channels'}</span>
      <div>
        {channels.map((channel) => (
          <button
            key={channel.key}
            type="button"
            className="button subtle"
            aria-label={formatDiscordChannelActionLabel('Show', channel)}
            onClick={() => onShowChannel(channel.key)}
          >
            {channel.name}
          </button>
        ))}
        <button type="button" className="button subtle" onClick={onShowAllChannels}>
          Show all
        </button>
      </div>
    </div>
  );
}

function DiscordBoard({
  channels,
  hiddenChannels,
  showHiddenChannelsBar,
  onHideChannel,
  onShowChannel,
  onShowAllChannels
}: {
  channels: DiscordChannelGroup[];
  hiddenChannels: DiscordChannelGroup[];
  showHiddenChannelsBar: boolean;
  onHideChannel: (channelKey: string) => void;
  onShowChannel: (channelKey: string) => void;
  onShowAllChannels: () => void;
}) {
  const columnStyle = {
    '--discord-channel-count': channels.length
  } as CSSProperties;

  return (
    <section className="discord-board" aria-label="Discord notifications">
      {showHiddenChannelsBar && hiddenChannels.length > 0 ? (
        <HiddenDiscordChannelsControls
          channels={hiddenChannels}
          onShowChannel={onShowChannel}
          onShowAllChannels={onShowAllChannels}
        />
      ) : null}

      {channels.length === 0 ? (
        <StateMessage title="All Discord channels hidden" detail="Restore a hidden channel to show notifications here." />
      ) : (
        <div className="discord-columns" data-testid="discord-columns" style={columnStyle}>
          {channels.map((channel) => (
            <section
              key={channel.key}
              className="discord-channel-column"
              data-testid="discord-channel-column"
              aria-label={`${channel.name} channel`}
            >
              <header>
                <div>
                  <h3>{channel.name}</h3>
                  {channel.context ? <small>{channel.context}</small> : null}
                </div>
                <div className="discord-channel-actions">
                  <span>{channel.notifications.length}</span>
                  <button
                    type="button"
                    className="hide-channel-button"
                    aria-label={formatDiscordChannelActionLabel('Hide', channel)}
                    title={formatDiscordChannelActionLabel('Hide', channel)}
                    onClick={() => onHideChannel(channel.key)}
                  >
                    <span aria-hidden="true">×</span>
                  </button>
                </div>
              </header>
              <ol>
                {channel.notifications.map((notification) => (
                  <DiscordNotificationCard key={notification.id} notification={notification} />
                ))}
              </ol>
            </section>
          ))}
        </div>
      )}
    </section>
  );
}

function DiscordNotificationCard({ notification }: { notification: NotificationItem }) {
  const sender = normalizeGroupLabel(notification.discord?.sender)
    ?? normalizeGroupLabel(notification.primaryText)
    ?? notification.sourceApp;

  return (
    <li className="discord-card" data-testid="discord-notification-card" data-id={notification.id}>
      <div className="discord-card-meta">
        <strong>{sender}</strong>
        <time dateTime={notification.timestamp}>{formatCompactTimestamp(notification.timestamp)}</time>
      </div>
      {isPresent(notification.messageText) ? (
        <p>{notification.messageText}</p>
      ) : null}
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

function groupDiscordNotifications(items: NotificationItem[]): DiscordChannelGroup[] {
  const channelMap = new Map<string, { name: string; context: string | null; notifications: NotificationItem[] }>();

  for (const notification of items) {
    const channelName = normalizeGroupLabel(notification.discord?.channel) ?? UngroupedLabel;
    const contextName = normalizeGroupLabel(notification.discord?.context);
    const channelKey = createDiscordChannelKey(channelName, contextName);
    const channelGroup = channelMap.get(channelKey) ?? {
      name: channelName,
      context: contextName,
      notifications: []
    };

    channelGroup.notifications.push(notification);
    channelMap.set(channelKey, channelGroup);
  }

  return [...channelMap.entries()]
    .map(([channelKey, channelGroup]) => {
      const sortedNotifications = sortNewestFirst(channelGroup.notifications);
      return {
        key: channelKey,
        name: channelGroup.name,
        context: channelGroup.context,
        latestId: getHighestId(sortedNotifications) ?? 0,
        notifications: sortedNotifications
      };
    })
    .sort((left, right) => right.latestId - left.latestId || left.name.localeCompare(right.name));
}

function isDiscordSource(source: NotificationSource): boolean {
  const appId = source.appId.toLowerCase();
  const displayName = source.displayName.toLowerCase();

  return appId === DiscordAppId.toLowerCase()
    || (appId.includes('discord') && displayName.includes('discord'));
}

function isDiscordNotification(notification: NotificationItem): boolean {
  return notification.discord !== null && notification.discord !== undefined;
}

function normalizeGroupLabel(value: string | null | undefined): string | null {
  const normalized = value?.trim();
  return normalized && normalized.length > 0 ? normalized : null;
}

function createDiscordChannelKey(channelName: string, contextName: string | null): string {
  return JSON.stringify([contextName ?? '', channelName]);
}

function formatDiscordChannelActionLabel(action: 'Hide' | 'Show', channel: DiscordChannelGroup): string {
  return channel.context ? `${action} ${channel.name} in ${channel.context}` : `${action} ${channel.name}`;
}

function readInitialThemeMode(): ThemeMode {
  try {
    const storedTheme = window.localStorage.getItem(ThemeStorageKey);
    if (storedTheme === 'light' || storedTheme === 'night') {
      return storedTheme;
    }
  } catch {
  }

  if (window.matchMedia?.('(prefers-color-scheme: dark)').matches) {
    return 'night';
  }

  return 'light';
}

function readInitialChromeHidden(): boolean {
  try {
    return window.localStorage.getItem(ChromeHiddenStorageKey) === 'true';
  } catch {
    return false;
  }
}

function readInitialHiddenDiscordChannelKeys(): Set<string> {
  try {
    const storedKeys = window.localStorage.getItem(HiddenDiscordChannelsStorageKey);
    if (!storedKeys) {
      return new Set();
    }

    const parsedKeys = JSON.parse(storedKeys);
    if (!Array.isArray(parsedKeys)) {
      return new Set();
    }

    return new Set(parsedKeys.filter((value): value is string => (
      typeof value === 'string' && value.length > 0
    )));
  } catch {
    return new Set();
  }
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

function formatCompactTimestamp(value: string): string {
  const timestamp = new Date(value);
  if (Number.isNaN(timestamp.getTime())) {
    return value;
  }

  return new Intl.DateTimeFormat(undefined, {
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
