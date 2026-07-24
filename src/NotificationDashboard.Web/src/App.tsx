import { type CSSProperties, type ReactNode, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { FocusEvent as ReactFocusEvent, PointerEvent as ReactPointerEvent } from 'react';
import { ApiError, createHttpDashboardApi, type DashboardApi } from './api';
import { createBrowserNotificationEventSource, type NotificationEventSourceFactory } from './events';
import type { HealthResponse, NotificationItem, NotificationSource } from './types';

const PageSize = 100;
const defaultApi = createHttpDashboardApi();
const DiscordAppId = 'com.squirrel.Discord.Discord';
const UngroupedLabel = 'Ungrouped';
const ThemeStorageKey = 'windows-clean-notifs-theme';
const HiddenDiscordChannelsStorageKey = 'windows-clean-notifs-hidden-discord-channels';
const DiscordChannelOrderStorageKey = 'windows-clean-notifs-discord-channel-order';
const ChromeHiddenStorageKey = 'windows-clean-notifs-chrome-hidden';
const ChromeRailPlacementStorageKey = 'windows-clean-notifs-chrome-rail-placement';
const HighlightedDiscordNotificationsStorageKey = 'windows-clean-notifs-highlighted-discord-notifications';
const TradingChatChannelName = '#💰│trading-chat';
const ChromeRailDragThreshold = 4;
const DiscordControlCharactersRegex = /[\u200e\u200f\u202a\u202b\u202c\u202d\u202e\u2066\u2067\u2068\u2069]/g;
const DiscordMarkdownLinkRegex = /\[([^\]]+)\]\s*(?:\(<[^>]+>\)|\([^)]*\)|<[^>]+>\)?)[⬏↗]?/g;
const FlagTokenRegex = /:flag_([a-z]{2}):/gi;
const MarkdownCodeSpanRegex = /`([^`]+)`/g;

type ConnectionStatus = 'connecting' | 'connected' | 'reconnecting' | 'unavailable';
type ViewMode = 'feed' | 'discord';
type ThemeMode = 'light' | 'night';
type DashboardControlsVariant = 'header' | 'rail';
type ChromeRailEdge = 'top' | 'right' | 'bottom' | 'left';
type ChromeRailAlignment = 'start' | 'center' | 'end';

type ChromeRailPlacement = {
  edge: ChromeRailEdge;
  offset: number;
  locked: boolean;
};

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

type TradingMetric = {
  label: string;
  value: string;
};

type TradingRelatedItem = {
  label: string;
  age: string;
  value: string;
};

type TradingBotMessage =
  | {
    kind: 'scanner';
    time: string;
    direction: string;
    ticker: string;
    price: string;
    percent: string | null;
    sequence: string | null;
    trigger: string | null;
    countryCode: string | null;
    signals: string[];
    metrics: TradingMetric[];
    related: TradingRelatedItem[];
    notes: string[];
  }
  | {
    kind: 'halt';
    time: string;
    ticker: string;
    status: string;
    reason: string | null;
    price: string | null;
    volume: string | null;
  }
  | {
    kind: 'pressRelease';
    ticker: string;
    price: string;
    headline: string;
    countryCode: string | null;
    signal: string;
    signals: string[];
    metrics: TradingMetric[];
  }
  | {
    kind: 'secFiling';
    time: string;
    ticker: string;
    form: string;
  }
  | {
    kind: 'marketStatus';
    text: string;
  }
  | {
    kind: 'newsHeadline';
    ticker: string;
    headline: string;
    age: string | null;
  }
  | {
    kind: 'tickerOnly';
    ticker: string;
  }
  | {
    kind: 'topGainer';
    ticker: string;
    rank: string;
    percent: string;
    volume: string;
    signal: string | null;
  }
  | {
    kind: 'fallback';
    text: string;
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
  chromeRailLocked: boolean;
  onChromeRailLockedChange: (locked: boolean) => void;
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
  const [chromeRailPlacement, setChromeRailPlacement] = useState<ChromeRailPlacement>(
    readInitialChromeRailPlacement
  );
  const [hiddenDiscordChannelKeys, setHiddenDiscordChannelKeys] = useState<Set<string>>(
    readInitialHiddenDiscordChannelKeys
  );
  const [discordChannelOrderKeys, setDiscordChannelOrderKeys] = useState<string[]>(
    readInitialDiscordChannelOrderKeys
  );
  const [highlightedDiscordNotifications, setHighlightedDiscordNotifications] = useState<Map<string, number>>(
    readInitialHighlightedDiscordNotifications
  );
  const [discordChannelPulseVersions, setDiscordChannelPulseVersions] = useState<Map<string, number>>(new Map());
  const activeViewRef = useRef(activeView);
  const hiddenDiscordChannelKeysRef = useRef(hiddenDiscordChannelKeys);
  const knownNotificationIdsRef = useRef<Set<number>>(new Set());
  const streamOpenedAtRef = useRef<number | null>(null);

  activeViewRef.current = activeView;
  hiddenDiscordChannelKeysRef.current = hiddenDiscordChannelKeys;

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
  const orderedDiscordChannels = useMemo(
    () => orderDiscordChannels(discordChannels, discordChannelOrderKeys),
    [discordChannels, discordChannelOrderKeys]
  );
  const visibleDiscordChannels = useMemo(
    () => orderedDiscordChannels.filter((channel) => !hiddenDiscordChannelKeys.has(channel.key)),
    [orderedDiscordChannels, hiddenDiscordChannelKeys]
  );
  const hiddenDiscordChannels = useMemo(
    () => orderedDiscordChannels.filter((channel) => hiddenDiscordChannelKeys.has(channel.key)),
    [orderedDiscordChannels, hiddenDiscordChannelKeys]
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
    setDiscordChannelOrderKeys((current) => appendMissingChannelOrderKeys(current, discordChannels));
  }, [discordChannels]);

  useEffect(() => {
    try {
      window.localStorage.setItem(DiscordChannelOrderStorageKey, JSON.stringify(discordChannelOrderKeys));
    } catch {
    }
  }, [discordChannelOrderKeys]);

  useEffect(() => {
    try {
      window.localStorage.setItem(ChromeHiddenStorageKey, chromeHidden ? 'true' : 'false');
    } catch {
    }
  }, [chromeHidden]);

  useEffect(() => {
    try {
      window.localStorage.setItem(ChromeRailPlacementStorageKey, JSON.stringify(chromeRailPlacement));
    } catch {
    }
  }, [chromeRailPlacement]);

  useEffect(() => {
    try {
      window.localStorage.setItem(
        HighlightedDiscordNotificationsStorageKey,
        JSON.stringify([...highlightedDiscordNotifications.entries()].sort(([left], [right]) => left.localeCompare(right)))
      );
    } catch {
    }
  }, [highlightedDiscordNotifications]);

  useEffect(() => {
    if (!streamReady) {
      return;
    }

    const notificationsById = new Map(notifications.map((notification) => [notification.id, notification]));
    setHighlightedDiscordNotifications((current) => {
      let next: Map<string, number> | null = null;

      for (const [channelKey, notificationId] of current) {
        const notification = notificationsById.get(notificationId);
        if (
          !notification
          || !isDiscordNotification(notification)
          || getDiscordChannelKey(notification) !== channelKey
          || isTradingChatNotification(notification)
        ) {
          next ??= new Map(current);
          next.delete(channelKey);
        }
      }

      return next ?? current;
    });
  }, [notifications, streamReady]);

  const highlightDiscordActivity = useCallback((notification: NotificationItem) => {
    if (!shouldHighlightDiscordActivity(
      notification,
      activeViewRef.current,
      hiddenDiscordChannelKeysRef.current,
      streamOpenedAtRef.current
    )) {
      return;
    }

    const channelKey = getDiscordChannelKey(notification);
    setHighlightedDiscordNotifications((current) => {
      const next = new Map(current);
      next.set(channelKey, notification.id);
      return next;
    });
    setDiscordChannelPulseVersions((current) => {
      const next = new Map(current);
      next.set(channelKey, (next.get(channelKey) ?? 0) + 1);
      return next;
    });
  }, []);

  const acknowledgeDiscordNotification = useCallback((channelKey: string, notificationId: number) => {
    setHighlightedDiscordNotifications((current) => {
      if (current.get(channelKey) !== notificationId) {
        return current;
      }

      const next = new Map(current);
      next.delete(channelKey);
      return next;
    });
  }, []);

  const replaceNotifications = useCallback(async () => {
    const nextNotifications = sortNewestFirst(await api.getNotifications({ limit: PageSize }));
    knownNotificationIdsRef.current = new Set(nextNotifications.map((notification) => notification.id));
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
        knownNotificationIdsRef.current = new Set(sortedNotifications.map((notification) => notification.id));
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
      streamOpenedAtRef.current = Date.now();
      setConnectionStatus('connected');
    };
    eventSource.onerror = () => {
      streamOpenedAtRef.current = null;
      setConnectionStatus('reconnecting');
    };
    eventSource.addEventListener('notification', (event) => {
      try {
        const notification = JSON.parse(event.data) as NotificationItem;
        const isNewNotification = !knownNotificationIdsRef.current.has(notification.id);
        knownNotificationIdsRef.current.add(notification.id);
        setNotifications((current) => mergeNotification(current, notification));
        if (isNewNotification) {
          highlightDiscordActivity(notification);
        }
        setFeedError(null);
      } catch {
        setConnectionStatus('unavailable');
        setFeedError('A live notification could not be read.');
      }
    });

    return () => {
      eventSource.close();
    };
  }, [createEventSource, health, highlightDiscordActivity, streamCursor, streamReady, streamVersion]);

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
      for (const notification of older) {
        knownNotificationIdsRef.current.add(notification.id);
      }
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
        <ChromeControlRail
          placement={chromeRailPlacement}
          onPlacementChange={setChromeRailPlacement}
        >
          <DashboardControls
            variant="rail"
            discordAvailable={discordAvailable}
            activeView={activeView}
            onViewChange={setActiveView}
            themeMode={themeMode}
            onThemeModeChange={setThemeMode}
            onOpenSources={openSources}
            chromeHidden={chromeHidden}
            onChromeHiddenChange={setChromeHidden}
            chromeRailLocked={chromeRailPlacement.locked}
            onChromeRailLockedChange={(locked) => {
              setChromeRailPlacement((current) => ({ ...current, locked }));
            }}
            hiddenChannels={hiddenDiscordChannels}
            onShowChannel={showDiscordChannel}
            onShowAllChannels={showAllDiscordChannels}
          />
        </ChromeControlRail>
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
            chromeRailLocked={chromeRailPlacement.locked}
            onChromeRailLockedChange={(locked) => {
              setChromeRailPlacement((current) => ({ ...current, locked }));
            }}
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
            highlightedNotifications={highlightedDiscordNotifications}
            channelPulseVersions={discordChannelPulseVersions}
            showHiddenChannelsBar={!chromeHidden}
            onAcknowledgeNotification={acknowledgeDiscordNotification}
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

function ChromeControlRail({
  placement,
  onPlacementChange,
  children
}: {
  placement: ChromeRailPlacement;
  onPlacementChange: (placement: ChromeRailPlacement) => void;
  children: ReactNode;
}) {
  const [menuOpen, setMenuOpen] = useState(false);
  const [dragPlacement, setDragPlacement] = useState<ChromeRailPlacement | null>(null);
  const [dragging, setDragging] = useState(false);
  const dragRef = useRef<{
    pointerId: number;
    startX: number;
    startY: number;
    dragging: boolean;
    placement: ChromeRailPlacement;
  } | null>(null);
  const suppressClickRef = useRef(false);
  const visiblePlacement = dragPlacement ?? placement;
  const alignment = getChromeRailAlignment(visiblePlacement.offset);
  const railStyle = {
    '--chrome-rail-offset': `${visiblePlacement.offset * 100}%`
  } as CSSProperties;
  const railClassName = [
    'chrome-control-rail',
    `rail-edge-${visiblePlacement.edge}`,
    `rail-align-${alignment}`,
    visiblePlacement.locked ? 'position-locked' : 'position-unlocked',
    dragging ? 'is-dragging' : '',
    menuOpen && !dragging ? 'menu-open' : ''
  ].filter(Boolean).join(' ');
  const horizontalEdge = visiblePlacement.edge === 'top' || visiblePlacement.edge === 'bottom';

  function handlePointerDown(event: ReactPointerEvent<HTMLButtonElement>) {
    if (placement.locked || event.button !== 0) {
      return;
    }

    setMenuOpen(false);
    dragRef.current = {
      pointerId: event.pointerId,
      startX: event.clientX,
      startY: event.clientY,
      dragging: false,
      placement
    };
    event.currentTarget.setPointerCapture?.(event.pointerId);
  }

  function handlePointerMove(event: ReactPointerEvent<HTMLButtonElement>) {
    const drag = dragRef.current;
    if (!drag || drag.pointerId !== event.pointerId) {
      return;
    }

    if (!drag.dragging) {
      const distance = Math.hypot(event.clientX - drag.startX, event.clientY - drag.startY);
      if (distance < ChromeRailDragThreshold) {
        return;
      }

      drag.dragging = true;
      setDragging(true);
      setMenuOpen(false);
    }

    const nextPlacement = getChromeRailPlacementForPoint(
      event.clientX,
      event.clientY,
      placement.locked
    );
    drag.placement = nextPlacement;
    setDragPlacement(nextPlacement);
  }

  function finishPointerInteraction(event: ReactPointerEvent<HTMLButtonElement>) {
    const drag = dragRef.current;
    if (!drag || drag.pointerId !== event.pointerId) {
      return;
    }

    if (drag.dragging) {
      onPlacementChange(drag.placement);
      suppressClickRef.current = true;
      window.setTimeout(() => {
        suppressClickRef.current = false;
      }, 0);
    }

    dragRef.current = null;
    setDragPlacement(null);
    setDragging(false);
    if (event.currentTarget.hasPointerCapture?.(event.pointerId)) {
      event.currentTarget.releasePointerCapture?.(event.pointerId);
    }
  }

  function handleBlur(event: ReactFocusEvent<HTMLDivElement>) {
    if (!event.currentTarget.contains(event.relatedTarget as Node | null)) {
      setMenuOpen(false);
    }
  }

  return (
    <div
      className={railClassName}
      style={railStyle}
      aria-label="Hidden dashboard controls"
      onPointerLeave={(event) => {
        if (!event.currentTarget.contains(document.activeElement)) {
          setMenuOpen(false);
        }
      }}
      onFocusCapture={() => {
        if (!dragRef.current) {
          setMenuOpen(true);
        }
      }}
      onBlurCapture={handleBlur}
    >
      <button
        type="button"
        className="chrome-rail-handle"
        aria-label="Show dashboard controls"
        aria-controls="chrome-rail-menu"
        aria-expanded={menuOpen && !dragging}
        title={placement.locked ? 'Dashboard controls. Position locked.' : 'Dashboard controls. Drag to move.'}
        onPointerEnter={() => {
          if (!dragRef.current) {
            setMenuOpen(true);
          }
        }}
        onPointerDown={handlePointerDown}
        onPointerMove={handlePointerMove}
        onPointerUp={finishPointerInteraction}
        onPointerCancel={finishPointerInteraction}
        onClick={() => {
          if (suppressClickRef.current) {
            return;
          }

          setMenuOpen((current) => !current);
        }}
      >
        <span aria-hidden="true">{horizontalEdge ? '⋯' : '⋮'}</span>
      </button>
      <div id="chrome-rail-menu" className="chrome-rail-menu">
        {children}
      </div>
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
  chromeRailLocked,
  onChromeRailLockedChange,
  hiddenChannels,
  onShowChannel,
  onShowAllChannels
}: DashboardControlsProps) {
  return (
    <div className={variant === 'header' ? 'header-actions' : 'chrome-rail-actions'}>
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
      {variant === 'rail' && hiddenChannels.length > 0 ? (
        <HiddenDiscordChannelsControls
          channels={hiddenChannels}
          onShowChannel={onShowChannel}
          onShowAllChannels={onShowAllChannels}
          compact
        />
      ) : null}
      {variant === 'rail' ? (
        <button
          type="button"
          className="button subtle"
          onClick={() => onChromeRailLockedChange(!chromeRailLocked)}
        >
          {chromeRailLocked ? 'Unlock position' : 'Lock position'}
        </button>
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
  highlightedNotifications,
  channelPulseVersions,
  showHiddenChannelsBar,
  onAcknowledgeNotification,
  onHideChannel,
  onShowChannel,
  onShowAllChannels
}: {
  channels: DiscordChannelGroup[];
  hiddenChannels: DiscordChannelGroup[];
  highlightedNotifications: Map<string, number>;
  channelPulseVersions: Map<string, number>;
  showHiddenChannelsBar: boolean;
  onAcknowledgeNotification: (channelKey: string, notificationId: number) => void;
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
          {channels.map((channel) => {
            const highlightedNotificationId = highlightedNotifications.get(channel.key);
            const pulseVersion = channelPulseVersions.get(channel.key) ?? 0;
            return (
              <section
                key={channel.key}
                className="discord-channel-column"
                data-testid="discord-channel-column"
                aria-label={`${channel.name} channel`}
              >
              <header className="discord-channel-header">
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
                  <DiscordNotificationCard
                    key={notification.id}
                    notification={notification}
                    highlighted={notification.id === highlightedNotificationId}
                    animateArrival={notification.id === highlightedNotificationId && pulseVersion > 0}
                    onAcknowledge={() => onAcknowledgeNotification(channel.key, notification.id)}
                  />
                ))}
              </ol>
            </section>
            );
          })}
        </div>
      )}
    </section>
  );
}

function DiscordNotificationCard({
  notification,
  highlighted,
  animateArrival,
  onAcknowledge
}: {
  notification: NotificationItem;
  highlighted: boolean;
  animateArrival: boolean;
  onAcknowledge: () => void;
}) {
  const sender = normalizeGroupLabel(notification.discord?.sender)
    ?? normalizeGroupLabel(notification.primaryText)
    ?? notification.sourceApp;
  const tradingMessage = parseTradingBotMessage(notification, sender);

  return (
    <li
      className={[
        'discord-card',
        tradingMessage ? 'trading-bot-card' : '',
        tradingMessage?.kind === 'scanner'
          ? `trading-card-${formatTradingDirectionClass(tradingMessage.direction)}`
          : '',
        highlighted ? 'activity-highlight' : '',
        animateArrival ? 'activity-arrival' : ''
      ].filter(Boolean).join(' ')}
      data-testid="discord-notification-card"
      data-id={notification.id}
      role={highlighted ? 'button' : undefined}
      tabIndex={highlighted ? 0 : undefined}
      aria-label={highlighted ? `Acknowledge new message from ${sender}` : undefined}
      title={highlighted ? 'Click to clear highlight' : undefined}
      onClick={highlighted ? onAcknowledge : undefined}
      onKeyDown={highlighted ? (event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault();
          onAcknowledge();
        }
      } : undefined}
    >
      <div className="discord-card-meta">
        <strong>{sender}</strong>
        <time dateTime={notification.timestamp}>{formatCompactTimestamp(notification.timestamp)}</time>
      </div>
      {tradingMessage ? (
        <TradingBotMessageView message={tradingMessage} />
      ) : isPresent(notification.messageText) ? (
        <p>{notification.messageText}</p>
      ) : null}
    </li>
  );
}

function TradingBotMessageView({ message }: { message: TradingBotMessage }) {
  if (message.kind === 'scanner') {
    return (
      <div className="trading-card-body">
        <div className="trading-card-head">
          <div className="trading-symbol-row">
            <span className={`trading-direction ${formatTradingDirectionClass(message.direction)}`}>
              {message.direction}
            </span>
            <strong className="trading-ticker">{message.ticker}</strong>
            <span className="trading-price">&lt; {message.price}</span>
            {message.percent ? <span className="trading-percent">{message.percent}</span> : null}
            <TradingFlag countryCode={message.countryCode} />
          </div>
          <span className="trading-time">{message.time}</span>
        </div>
        <TradingChipRow
          signals={[
            message.sequence,
            message.trigger,
            ...message.signals
          ]}
          metrics={message.metrics}
        />
        {message.related.length > 0 ? (
          <div className="trading-related" aria-label="Related trading signals">
            {message.related.map((item) => (
              <div key={`${item.label}-${item.age}-${item.value}`}>
                <span className={`trading-signal ${formatTradingSignalClass(item.label)}`}>{item.label}</span>
                <span>{item.age}</span>
                <strong>{item.value}</strong>
              </div>
            ))}
          </div>
        ) : null}
        {message.notes.length > 0 ? (
          <p className="trading-note">{message.notes.join('\n')}</p>
        ) : null}
      </div>
    );
  }

  if (message.kind === 'halt') {
    return (
      <div className="trading-card-body">
        <div className="trading-card-head">
          <div className="trading-symbol-row">
            <strong className="trading-ticker">{message.ticker}</strong>
            <span className={`trading-signal ${formatHaltSignalClass(message.status)}`}>
              {message.status}
            </span>
          </div>
          <span className="trading-time">{message.time}</span>
        </div>
        <TradingChipRow
          signals={[message.reason]}
          metrics={[
            message.price ? { label: 'Price', value: message.price } : null,
            message.volume ? { label: 'Vol', value: message.volume } : null
          ]}
        />
      </div>
    );
  }

  if (message.kind === 'pressRelease') {
    return (
      <div className="trading-card-body">
        <div className="trading-card-head">
          <div className="trading-symbol-row">
            <strong className="trading-ticker">{message.ticker}</strong>
            <span className="trading-price">&lt; {message.price}</span>
            <span className="trading-signal signal-news">{message.signal}</span>
            <TradingFlag countryCode={message.countryCode} />
          </div>
        </div>
        <p className="trading-headline">{message.headline}</p>
        <TradingChipRow signals={message.signals} metrics={message.metrics} />
      </div>
    );
  }

  if (message.kind === 'secFiling') {
    return (
      <div className="trading-card-body">
        <div className="trading-card-head">
          <div className="trading-symbol-row">
            <strong className="trading-ticker">{message.ticker}</strong>
            <span className="trading-signal signal-filing">SEC</span>
            <span className="trading-price">{message.form}</span>
          </div>
          <span className="trading-time">{message.time}</span>
        </div>
      </div>
    );
  }

  if (message.kind === 'marketStatus') {
    return (
      <div className="trading-card-body">
        <div className="trading-card-head">
          <div className="trading-symbol-row">
            <span className="trading-signal signal-neutral">{message.text}</span>
          </div>
        </div>
      </div>
    );
  }

  if (message.kind === 'newsHeadline') {
    return (
      <div className="trading-card-body">
        <div className="trading-card-head">
          <div className="trading-symbol-row">
            <strong className="trading-ticker">{message.ticker}</strong>
            <span className="trading-signal signal-news">News</span>
          </div>
          {message.age ? <span className="trading-time">{message.age}</span> : null}
        </div>
        <p className="trading-headline">{message.headline}</p>
      </div>
    );
  }

  if (message.kind === 'tickerOnly') {
    return (
      <div className="trading-card-body">
        <div className="trading-card-head">
          <div className="trading-symbol-row">
            <strong className="trading-ticker">{message.ticker}</strong>
          </div>
        </div>
      </div>
    );
  }

  if (message.kind === 'topGainer') {
    return (
      <div className="trading-card-body">
        <div className="trading-card-head">
          <div className="trading-symbol-row">
            <strong className="trading-ticker">{message.ticker}</strong>
            <span className="trading-signal signal-up">{message.rank} Top gainer</span>
            <span className="trading-percent">{message.percent}</span>
          </div>
        </div>
        <TradingChipRow
          signals={[message.signal]}
          metrics={[{ label: 'Vol', value: message.volume }]}
        />
      </div>
    );
  }

  return (
    <p className="trading-clean-text">{renderTextWithFlags(message.text)}</p>
  );
}

function TradingFlag({ countryCode }: { countryCode: string | null }) {
  if (!countryCode || !/^[a-z]{2}$/i.test(countryCode)) {
    return null;
  }

  const normalizedCode = countryCode.toLowerCase();
  const label = `${normalizedCode.toUpperCase()} flag`;

  return (
    <span
      className={`trading-flag fi fi-${normalizedCode}`}
      aria-label={label}
      title={label}
    />
  );
}

function TradingChipRow({
  signals,
  metrics
}: {
  signals: Array<string | null>;
  metrics?: Array<TradingMetric | null>;
}) {
  const visibleSignals = uniqueStrings(signals.filter(isPresent));
  const visibleMetrics = (metrics ?? []).filter((metric): metric is TradingMetric => (
    metric !== null && isPresent(metric.label) && isPresent(metric.value)
  ));

  if (visibleSignals.length === 0 && visibleMetrics.length === 0) {
    return null;
  }

  return (
    <div className="trading-chip-row">
      {visibleSignals.map((signal) => (
        <span key={`signal-${signal}`} className={`trading-signal ${formatTradingSignalClass(signal)}`}>
          {signal}
        </span>
      ))}
      {visibleMetrics.map((metric) => (
        <span key={`metric-${metric.label}-${metric.value}`} className="trading-metric">
          <span>{metric.label}</span>
          <strong>{metric.value}</strong>
        </span>
      ))}
    </div>
  );
}

function parseTradingBotMessage(notification: NotificationItem, sender: string): TradingBotMessage | null {
  if (!isTradingBotNotification(notification, sender) || !isPresent(notification.messageText)) {
    return null;
  }

  const text = normalizeDiscordText(notification.messageText);
  return parseScannerAlert(text)
    ?? parseHaltAlert(text)
    ?? parsePressReleaseAlert(text, sender)
    ?? parseStandaloneSecFiling(text)
    ?? parseMarketStatus(text)
    ?? parseNewsHeadline(text)
    ?? parseTopGainer(text)
    ?? parseTickerOnly(text)
    ?? {
      kind: 'fallback',
      text: cleanDiscordMarkdownText(text)
    };
}

function isTradingBotNotification(notification: NotificationItem, sender: string): boolean {
  const channel = normalizeDiscordText(notification.discord?.channel ?? '');
  return channel === TradingChatChannelName && sender.startsWith('NuntioBot');
}

function parseScannerAlert(text: string): TradingBotMessage | null {
  const [primaryLine = '', ...extraLines] = text.split('\n');
  const match = /^`(?<time>\d{1,2}:\d{2})`\s+(?<direction>[↑↗↓↘→])\s+\*\*(?<ticker>[A-Z.]{1,7})\*\*\s+<\s+(?<price>\$\.?\d+(?:\.\d+)?c?)\s*(?<rest>.*)$/u.exec(primaryLine);
  if (!match?.groups) {
    return null;
  }

  const rest = match.groups.rest.trim();
  const countryMatch = /:flag_(?<countryCode>[a-z]{2}):/i.exec(rest);
  const beforeCountry = countryMatch ? rest.slice(0, countryMatch.index) : rest;
  const detailText = countryMatch
    ? rest.slice(countryMatch.index + countryMatch[0].length)
    : rest.includes('|')
      ? rest.slice(rest.indexOf('|'))
      : '';
  const percent = /`(?<percent>-?\d+(?:\.\d+)?%)`/.exec(beforeCountry)?.groups?.percent ?? null;
  const sequence = /(?:^|\s)·\s*(?<sequence>\d+)/.exec(beforeCountry)?.groups?.sequence ?? null;
  const trigger = [...beforeCountry.matchAll(MarkdownCodeSpanRegex)]
    .map((span) => cleanInlineText(span[1]))
    .find((span) => span !== percent) ?? null;
  const details = parseTradingDetails(detailText);
  const related: TradingRelatedItem[] = [];
  const notes: string[] = [];

  for (const line of extraLines) {
    const relatedItem = parseRelatedLine(line);
    if (relatedItem) {
      related.push(relatedItem);
      continue;
    }

    const cleanedLine = cleanDiscordMarkdownText(line.replace(/^>\s*\*?\s*/, ''));
    if (cleanedLine) {
      notes.push(cleanedLine);
    }
  }

  return {
    kind: 'scanner',
    time: match.groups.time,
    direction: match.groups.direction,
    ticker: match.groups.ticker,
    price: match.groups.price,
    percent,
    sequence,
    trigger,
    countryCode: countryMatch?.groups?.countryCode.toLowerCase() ?? null,
    signals: details.signals,
    metrics: details.metrics,
    related,
    notes: uniqueStrings([...details.notes, ...notes])
  };
}

function parseHaltAlert(text: string): TradingBotMessage | null {
  const match = /^`(?<time>\d{1,2}:\d{2}:\d{2})`\s+\*\*(?<ticker>[A-Z.]{1,7})\*\*\s+`(?<status>Halted(?:\s+(?:UP|DOWN))?)`\s*(?:\|\s*(?<details>.*))?$/u.exec(text);
  if (!match?.groups) {
    return null;
  }

  const details = cleanDiscordMarkdownText(match.groups.details ?? '');
  const detailMatch = /^(?<reason>.+?)(?:\s*→\s*(?<price>\$\.?\d+(?:\.\d+)?c?)\s*~\s*(?<volume>.+?)\s+vol)?$/.exec(details);

  return {
    kind: 'halt',
    time: match.groups.time,
    ticker: match.groups.ticker,
    status: match.groups.status,
    reason: detailMatch?.groups?.reason?.trim() ?? null,
    price: detailMatch?.groups?.price ?? null,
    volume: detailMatch?.groups?.volume ?? null
  };
}

function parsePressReleaseAlert(text: string, sender: string): TradingBotMessage | null {
  const match = /^\*\*(?<ticker>[A-Z.]{1,7})\*\*\s+<\s+(?<price>\$\.?\d+(?:\.\d+)?c?)\s+-\s+(?<rest>[\s\S]+)$/u.exec(text);
  if (!match?.groups) {
    return null;
  }

  const detailMatch = /\s+~\s+(?::flag_(?<countryCode>[a-z]{2}):\s*)?/i.exec(match.groups.rest);
  const headlineText = detailMatch
    ? match.groups.rest.slice(0, detailMatch.index)
    : match.groups.rest;
  const detailText = detailMatch
    ? match.groups.rest.slice(detailMatch.index + detailMatch[0].length)
    : '';

  const headline = cleanDiscordMarkdownText(headlineText);
  const details = parseTradingDetails(detailText);

  return {
    kind: 'pressRelease',
    ticker: match.groups.ticker,
    price: match.groups.price,
    headline,
    countryCode: detailMatch?.groups?.countryCode?.toLowerCase() ?? null,
    signal: sender.includes('DROP') ? 'PR Drop' : 'PR Spike',
    signals: details.signals,
    metrics: details.metrics
  };
}

function parseStandaloneSecFiling(text: string): TradingBotMessage | null {
  const match = /^`(?<time>\d{1,2}:\d{2})`\s+`SEC`\s+\*\*(?<ticker>[A-Z.]{1,7})\*\*\s+-\s+`Form\s+(?<form>[A-Z0-9-]+)`/u.exec(text);
  if (!match?.groups) {
    return null;
  }

  return {
    kind: 'secFiling',
    time: match.groups.time,
    ticker: match.groups.ticker,
    form: match.groups.form
  };
}

function parseMarketStatus(text: string): TradingBotMessage | null {
  if (/^Market Open\s*:bell:\s*$/i.test(text) || /^Market Open\s*<t:\d+:R>\s*$/i.test(text)) {
    return {
      kind: 'marketStatus',
      text: 'Market Open'
    };
  }

  const openInMatch = /^Market Open in (?<minutes>\d+) minutes?$/i.exec(text);
  if (openInMatch?.groups) {
    return {
      kind: 'marketStatus',
      text: `Market opens in ${openInMatch.groups.minutes}m`
    };
  }

  if (/^Market Close(?:\s*<t:\d+:R>)?\s*$/i.test(text)) {
    return {
      kind: 'marketStatus',
      text: 'Market Close'
    };
  }

  const closeInMatch = /^Market Close in (?<minutes>\d+) minutes?$/i.exec(text);
  if (closeInMatch?.groups) {
    return {
      kind: 'marketStatus',
      text: `Market closes in ${closeInMatch.groups.minutes}m`
    };
  }

  if (/^Market Closed/i.test(text)) {
    return {
      kind: 'marketStatus',
      text: cleanDiscordMarkdownText(text)
    };
  }

  return null;
}

function parseNewsHeadline(text: string): TradingBotMessage | null {
  const timedMatch = /^(?<ticker>[A-Z.]{1,7})\s+-\s+<t:(?<timestamp>\d+):R>\s+(?<headline>[\s\S]+?)\s*(?:\/\s*)?\[Link\]/u.exec(text);
  if (timedMatch?.groups) {
    return {
      kind: 'newsHeadline',
      ticker: timedMatch.groups.ticker,
      headline: cleanDiscordMarkdownText(timedMatch.groups.headline),
      age: formatDiscordRelativeTimestamp(timedMatch.groups.timestamp)
    };
  }

  const plainMatch = /^(?<ticker>[A-Z.]{1,7})\s+-\s+(?<headline>[\s\S]+?)\s*\[Link\]/u.exec(text);
  if (!plainMatch?.groups) {
    return null;
  }

  return {
    kind: 'newsHeadline',
    ticker: plainMatch.groups.ticker,
    headline: cleanDiscordMarkdownText(plainMatch.groups.headline),
    age: null
  };
}

function parseTopGainer(text: string): TradingBotMessage | null {
  const cleanedText = cleanDiscordMarkdownText(text);
  const match = /^(?<ticker>[A-Z.]{1,7})\s+#(?<rank>\d+)\s+top-gainer\s+(?<percent>[+-]?\d+(?:\.\d+)?%)\s+-\s+(?<volume>[\d,.]+)\s+vol(?:\s+~\s+(?<signal>[A-Z][A-Z0-9+*-]*))?$/i.exec(cleanedText);
  if (!match?.groups) {
    return null;
  }

  return {
    kind: 'topGainer',
    ticker: match.groups.ticker.toUpperCase(),
    rank: `#${match.groups.rank}`,
    percent: match.groups.percent,
    volume: match.groups.volume,
    signal: match.groups.signal?.toUpperCase() ?? null
  };
}

function parseTickerOnly(text: string): TradingBotMessage | null {
  const ticker = cleanDiscordMarkdownText(text);
  if (!/^[A-Z.]{1,7}$/.test(ticker)) {
    return null;
  }

  return {
    kind: 'tickerOnly',
    ticker
  };
}

function parseTradingDetails(text: string): { signals: string[]; metrics: TradingMetric[]; notes: string[] } {
  const signals: string[] = [];
  const metrics: TradingMetric[] = [];
  const notes: string[] = [];

  for (const segment of text.split('|')) {
    for (const rawPart of segment.split('\n')) {
      signals.push(...extractVisibleLinkedLabels(rawPart));

      for (const codeSpan of rawPart.matchAll(MarkdownCodeSpanRegex)) {
        const signal = cleanInlineText(codeSpan[1]);
        if (signal && !/^-?\d+(?:\.\d+)?%$/.test(signal)) {
          signals.push(signal);
        }
      }

      const part = cleanDiscordMarkdownText(rawPart)
        .replace(/^>\s*/, '')
        .replace(/^\*\s*/, '')
        .replace(/^~\s*/, '')
        .trim();
      if (!part || shouldHideLinkLabel(part)) {
        continue;
      }

      const metricMatch = /^(?<label>[A-Za-z][A-Za-z0-9 +/%.-]{0,20}):\s*(?<value>.+)$/.exec(part);
      if (metricMatch?.groups) {
        metrics.push({
          label: metricMatch.groups.label,
          value: metricMatch.groups.value
        });
        continue;
      }

      if (!extractVisibleLinkedLabels(rawPart).includes(part) && !rawPart.includes('`')) {
        notes.push(part);
      }
    }
  }

  return {
    signals: uniqueStrings(signals),
    metrics,
    notes: uniqueStrings(notes)
  };
}

function parseRelatedLine(line: string): TradingRelatedItem | null {
  const cleaned = normalizeDiscordText(line)
    .replace(/^>\s*/, '')
    .replace(/^\*\s*/, '')
    .trim();
  const match = /^(?<age>(?:(?:a|an|\d+)\s+(?:second|seconds|minute|minutes|hour|hours|day|days) ago|in\s+\d+\s+(?:second|seconds|minute|minutes|hour|hours|day|days)))\s+`?(?<label>SEC|PR|AR)`?\s+(?<value>[\s\S]+)$/i.exec(cleaned);
  if (!match?.groups) {
    return null;
  }

  const label = match.groups.label.toUpperCase();
  const value = label === 'SEC'
    ? (/\bForm\s+(?<form>[A-Z0-9-]+)/i.exec(match.groups.value)?.groups?.form ?? cleanDiscordMarkdownText(match.groups.value))
    : cleanDiscordMarkdownText(match.groups.value);

  return {
    label,
    age: compactRelativeAge(match.groups.age),
    value
  };
}

function compactRelativeAge(value: string): string {
  const normalized = value.toLowerCase();
  const isFuture = normalized.startsWith('in ');
  const amount = /^(?:a|an)\s/.test(normalized) ? '1' : normalized.match(/\d+/)?.[0] ?? '';
  let compactUnit = '';
  if (normalized.includes('second')) {
    compactUnit = 's';
  } else if (normalized.includes('minute')) {
    compactUnit = 'm';
  } else if (normalized.includes('hour')) {
    compactUnit = 'h';
  } else if (normalized.includes('day')) {
    compactUnit = 'd';
  }

  if (amount && compactUnit) {
    return isFuture ? `in ${amount}${compactUnit}` : `${amount}${compactUnit} ago`;
  }

  return value;
}

function formatDiscordRelativeTimestamp(value: string): string | null {
  const timestamp = Number(value);
  if (!Number.isFinite(timestamp) || timestamp <= 0) {
    return null;
  }

  const diffSeconds = Math.max(0, Math.floor((Date.now() - timestamp * 1000) / 1000));
  if (diffSeconds < 60) {
    return `${diffSeconds}s ago`;
  }

  const diffMinutes = Math.floor(diffSeconds / 60);
  if (diffMinutes < 60) {
    return `${diffMinutes}m ago`;
  }

  const diffHours = Math.floor(diffMinutes / 60);
  if (diffHours < 24) {
    return `${diffHours}h ago`;
  }

  return `${Math.floor(diffHours / 24)}d ago`;
}

function normalizeDiscordText(value: string): string {
  return value
    .replace(DiscordControlCharactersRegex, '')
    .replace(/\r\n?/g, '\n')
    .trim();
}

function cleanDiscordMarkdownText(value: string): string {
  return normalizeDiscordText(value)
    .replace(DiscordMarkdownLinkRegex, (_match, label: string) => {
      const cleanedLabel = cleanInlineText(label);
      return shouldHideLinkLabel(cleanedLabel) ? '' : cleanedLabel;
    })
    .replace(/```/g, '')
    .replace(/\*\*/g, '')
    .replace(/`([^`]+)`/g, '$1')
    .replace(/<https?:\/\/[^>\s]+>/g, '')
    .replace(/https?:\/\/\S+/g, '')
    .replace(/\s+⬏/g, '')
    .replace(/[ \t]+/g, ' ')
    .replace(/[ \t]*\n[ \t]*/g, '\n')
    .trim();
}

function cleanInlineText(value: string | undefined): string {
  return normalizeDiscordText(value ?? '')
    .replace(/\*\*/g, '')
    .replace(/`/g, '')
    .trim();
}

function extractVisibleLinkedLabels(value: string): string[] {
  return [...value.matchAll(DiscordMarkdownLinkRegex)]
    .map((match) => cleanInlineText(match[1]))
    .filter((label) => isPresent(label) && !shouldHideLinkLabel(label));
}

function shouldHideLinkLabel(label: string): boolean {
  return /^-?\s*Link$/i.test(label);
}

function renderTextWithFlags(value: string): ReactNode[] {
  const nodes: ReactNode[] = [];
  let lastIndex = 0;

  for (const match of value.matchAll(FlagTokenRegex)) {
    const index = match.index ?? 0;
    if (index > lastIndex) {
      nodes.push(value.slice(lastIndex, index));
    }

    nodes.push(<TradingFlag key={`${match[1]}-${index}`} countryCode={match[1].toLowerCase()} />);
    lastIndex = index + match[0].length;
  }

  if (lastIndex < value.length) {
    nodes.push(value.slice(lastIndex));
  }

  return nodes;
}

function uniqueStrings(values: string[]): string[] {
  const seen = new Set<string>();
  const result: string[] = [];
  for (const value of values) {
    if (!seen.has(value)) {
      result.push(value);
      seen.add(value);
    }
  }

  return result;
}

function formatTradingDirectionClass(direction: string): string {
  return direction === '↓' || direction === '↘' ? 'direction-down' : 'direction-up';
}

function formatHaltSignalClass(status: string): string {
  if (status.includes('DOWN')) {
    return 'signal-down';
  }

  if (status.includes('UP')) {
    return 'signal-up';
  }

  return 'signal-neutral';
}

function formatTradingSignalClass(signal: string): string {
  if (signal === 'PR' || signal === 'PR+' || signal === 'PR*' || signal.startsWith('PR ')) {
    return 'signal-news';
  }

  if (signal === 'SEC') {
    return 'signal-filing';
  }

  if (signal.includes('CTB') || signal === 'Reg SHO') {
    return 'signal-risk';
  }

  return 'signal-neutral';
}

function getChromeRailPlacementForPoint(
  clientX: number,
  clientY: number,
  locked: boolean
): ChromeRailPlacement {
  const viewportWidth = Math.max(window.innerWidth, 1);
  const viewportHeight = Math.max(window.innerHeight, 1);
  const x = Math.min(viewportWidth, Math.max(0, clientX));
  const y = Math.min(viewportHeight, Math.max(0, clientY));
  const candidates: Array<{ edge: ChromeRailEdge; distance: number }> = [
    { edge: 'top', distance: y },
    { edge: 'right', distance: viewportWidth - x },
    { edge: 'bottom', distance: viewportHeight - y },
    { edge: 'left', distance: x }
  ];
  let nearest = candidates[0];
  for (const candidate of candidates.slice(1)) {
    if (candidate.distance < nearest.distance) {
      nearest = candidate;
    }
  }

  const offset = nearest.edge === 'top' || nearest.edge === 'bottom'
    ? x / viewportWidth
    : y / viewportHeight;

  return {
    edge: nearest.edge,
    offset: Math.min(1, Math.max(0, offset)),
    locked
  };
}

function getChromeRailAlignment(offset: number): ChromeRailAlignment {
  if (offset < 1 / 3) {
    return 'start';
  }

  return offset > 2 / 3 ? 'end' : 'center';
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

function orderDiscordChannels(
  channels: DiscordChannelGroup[],
  channelOrderKeys: string[]
): DiscordChannelGroup[] {
  const channelMap = new Map(channels.map((channel) => [channel.key, channel]));
  const orderedChannels: DiscordChannelGroup[] = [];
  const seenKeys = new Set<string>();

  for (const channelKey of channelOrderKeys) {
    const channel = channelMap.get(channelKey);
    if (channel && !seenKeys.has(channelKey)) {
      orderedChannels.push(channel);
      seenKeys.add(channelKey);
    }
  }

  for (const channel of channels) {
    if (!seenKeys.has(channel.key)) {
      orderedChannels.push(channel);
      seenKeys.add(channel.key);
    }
  }

  return orderedChannels;
}

function appendMissingChannelOrderKeys(
  channelOrderKeys: string[],
  channels: DiscordChannelGroup[]
): string[] {
  const seenKeys = new Set(channelOrderKeys);
  let nextChannelOrderKeys: string[] | null = null;

  for (const channel of channels) {
    if (!seenKeys.has(channel.key)) {
      nextChannelOrderKeys ??= [...channelOrderKeys];
      nextChannelOrderKeys.push(channel.key);
      seenKeys.add(channel.key);
    }
  }

  return nextChannelOrderKeys ?? channelOrderKeys;
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

function shouldHighlightDiscordActivity(
  notification: NotificationItem,
  activeView: ViewMode,
  hiddenChannelKeys: Set<string>,
  streamOpenedAt: number | null
): boolean {
  if (
    activeView !== 'discord'
    || document.visibilityState !== 'visible'
    || streamOpenedAt === null
    || !isDiscordNotification(notification)
  ) {
    return false;
  }

  if (isTradingChatNotification(notification)) {
    return false;
  }

  const timestamp = Date.parse(notification.timestamp);
  return Number.isFinite(timestamp)
    && timestamp > streamOpenedAt
    && !hiddenChannelKeys.has(getDiscordChannelKey(notification));
}

function isTradingChatNotification(notification: NotificationItem): boolean {
  const channelName = normalizeGroupLabel(notification.discord?.channel) ?? UngroupedLabel;
  return normalizeDiscordText(channelName) === TradingChatChannelName;
}

function getDiscordChannelKey(notification: NotificationItem): string {
  const channelName = normalizeGroupLabel(notification.discord?.channel) ?? UngroupedLabel;
  const contextName = normalizeGroupLabel(notification.discord?.context);
  return createDiscordChannelKey(channelName, contextName);
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

function readInitialChromeRailPlacement(): ChromeRailPlacement {
  const fallback: ChromeRailPlacement = {
    edge: 'right',
    offset: 0.5,
    locked: false
  };

  try {
    const storedPlacement = window.localStorage.getItem(ChromeRailPlacementStorageKey);
    if (!storedPlacement) {
      return fallback;
    }

    const parsedPlacement: unknown = JSON.parse(storedPlacement);
    if (!parsedPlacement || typeof parsedPlacement !== 'object') {
      return fallback;
    }

    const candidate = parsedPlacement as Partial<ChromeRailPlacement>;
    if (
      !isChromeRailEdge(candidate.edge)
      || typeof candidate.offset !== 'number'
      || !Number.isFinite(candidate.offset)
      || typeof candidate.locked !== 'boolean'
    ) {
      return fallback;
    }

    return {
      edge: candidate.edge,
      offset: Math.min(1, Math.max(0, candidate.offset)),
      locked: candidate.locked
    };
  } catch {
    return fallback;
  }
}

function isChromeRailEdge(value: unknown): value is ChromeRailEdge {
  return value === 'top' || value === 'right' || value === 'bottom' || value === 'left';
}

function readInitialDiscordChannelOrderKeys(): string[] {
  try {
    const storedKeys = window.localStorage.getItem(DiscordChannelOrderStorageKey);
    if (!storedKeys) {
      return [];
    }

    const parsedKeys = JSON.parse(storedKeys);
    if (!Array.isArray(parsedKeys)) {
      return [];
    }

    return parsedKeys.filter((value): value is string => (
      typeof value === 'string' && value.length > 0
    ));
  } catch {
    return [];
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

function readInitialHighlightedDiscordNotifications(): Map<string, number> {
  try {
    const storedHighlights = window.localStorage.getItem(HighlightedDiscordNotificationsStorageKey);
    if (!storedHighlights) {
      return new Map();
    }

    const parsedHighlights: unknown = JSON.parse(storedHighlights);
    if (!Array.isArray(parsedHighlights)) {
      return new Map();
    }

    const highlights = parsedHighlights.filter((value): value is [string, number] => (
      Array.isArray(value)
      && value.length === 2
      && typeof value[0] === 'string'
      && value[0].length > 0
      && typeof value[1] === 'number'
      && Number.isSafeInteger(value[1])
      && value[1] >= 0
    ));
    return new Map(highlights);
  } catch {
    return new Map();
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
